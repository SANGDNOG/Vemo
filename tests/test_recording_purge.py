"""Permanent Vemo-only cleanup against synthetic files and durable queue state."""
import fcntl
import json
import pathlib
import sqlite3
import time
import types
import unittest
from unittest.mock import patch

import test_engine
from test_engine import memo


class RecordingPurgeTests(unittest.TestCase):
    setUp = test_engine.EngineTests.setUp
    tearDown = test_engine.EngineTests.tearDown
    engine = test_engine.EngineTests.engine
    send = test_engine.EngineTests.send
    job = test_engine.EngineTests.job

    def create(self, imported=False):
        self.root = self.root.resolve()
        self.engine()
        source = memo('purge-fixture')
        if imported:
            source.update(source_kind='import', source_reference='import:fixture')
        self.assertTrue(self.send('import', {'memo': source})['ok'])
        return self.job(source['uuid'])

    def files(self, job):
        owned = []
        for parent in ('work', 'artifacts', 'snapshots/jobs'):
            path = self.root / parent / job['id'] / 'nested' / 'private-data'
            path.parent.mkdir(parents=True)
            path.write_bytes(b'private recording data')
            owned.append(path)
        return owned

    def import_registry(self, job):
        cache = self.root / 'snapshots/imports/cache.wav'
        cache.parent.mkdir(parents=True, exist_ok=True)
        cache.write_bytes(b'imported audio')
        native = self.root / 'native'
        native.mkdir(exist_ok=True)
        entry = {'memo': {**memo(job['uuid']), 'source_kind': 'import',
                          'source_reference': job['source_reference']},
                 'path': str(cache), 'hash': 'a' * 64, 'delivered': False}
        registry = {'schema_version': 1, 'entries': {job['source_reference']: entry}}
        (native / 'imports.json').write_text(json.dumps(registry))
        return cache, registry

    def trash(self, job):
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])

    def test_requires_trash_and_valid_job_id_without_deleting(self):
        job = self.create()
        paths = self.files(job)
        result = self.send('purge_recording', {'job_id': job['id']})
        self.assertFalse(result['ok'])
        self.assertEqual(result['error']['message'], 'recording_not_trashed')
        for value in ('', None, ['id']):
            self.assertFalse(self.send('purge_recording', {'job_id': value})['ok'])
        self.assertTrue(all(path.exists() for path in paths))

    def test_exact_owned_trees_removed_external_metadata_and_siblings_preserved(self):
        job = self.create()
        owned = self.files(job)
        preserved = []
        for path in (self.root / 'original.wav', self.root / 'exports/result.zip',
                     self.root / 'work/other-job/data',
                     self.root / 'artifacts/other-job/data',
                     self.root / 'snapshots/jobs/other-job/data',
                     self.root / 'native/recording-zips/receipt.json'):
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(b'preserve')
            preserved.append(path)
        job.update(audio_path=str(preserved[0]), artifact_path=str(preserved[1]),
                   topic='private title', asr_chunks=[{'text': 'private transcript'}])
        with self.e.store.transaction(): self.e.store.put(job)
        self.trash(job)
        self.events.clear()
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']}, 'purge')['ok'])
        self.assertTrue(all(not path.exists() for path in owned))
        self.assertTrue(all(path.read_bytes() == b'preserve' for path in preserved))
        self.assertEqual(self.e.snapshot()['jobs'], [])
        self.assertEqual([e['kind'] for e in self.events], ['job_removed', 'command_result'])
        self.assertEqual(self.events[0]['payload'], {'job_id': job['id']})
        tombstone = self.job(job['uuid'])
        self.assertTrue(tombstone.get('purged_at'))
        self.assertFalse(any(key in tombstone for key in ('topic', 'original_title', 'asr_chunks', 'artifact_path', 'audio_path', 'notes_path')))
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']}, 'purge')['ok'])
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_discover_import_restart_and_late_callbacks_cannot_resurrect(self):
        job = self.create()
        self.trash(job)
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.e.close(); self.engine()
        changed = memo(job['uuid'], revision={'size': 500, 'mtime_ns': 9})
        changed.update(metadata_revision='2', original_title='resurrected title')
        self.assertTrue(self.send('discover', {'memos': [changed]})['ok'])
        self.assertTrue(self.send('import', {'memo': changed})['ok'])
        self.assertTrue(self.send('enqueue', {'uuids': [job['uuid']]})['ok'])
        for kind, payload in (('restore_recording', {}), ('trash_recording', {}),
                              ('refresh_recording', {}), ('retry', {}),
                              ('snapshot_ready', {'snapshot_request_id': 'late'}),
                              ('publish_ack', {'publication_id': 'late'})):
            self.assertFalse(self.send(kind, {'job_id': job['id'], **payload})['ok'], kind)
        self.e.tick()
        self.assertEqual(self.e.snapshot()['jobs'], [])
        self.assertEqual(len(self.e.store.jobs()), 1)
        self.assertNotIn('original_title', self.job(job['uuid']))
        self.assertIsNone(self.e.active)

    def test_interrupted_cleanup_intent_is_committed_and_restart_finishes(self):
        job = self.create()
        owned = self.files(job)
        self.trash(job)

        def interrupted(current):
            with sqlite3.connect(self.root / 'queue.sqlite3') as db:
                durable = json.loads(db.execute('SELECT data FROM jobs WHERE id=?', (job['id'],)).fetchone()[0])
                self.assertTrue(durable.get('purge_requested_at'))
                self.assertFalse(durable['queued'])
            self.assertTrue(all(path.exists() for path in owned))
            raise SystemExit('synthetic crash after commit')

        with patch.object(self.e, 'cleanup_purge', side_effect=interrupted):
            with self.assertRaises(SystemExit): self.send('purge_recording', {'job_id': job['id']}, 'interrupted')
        self.e.close(); self.engine()
        self.assertTrue(all(not path.exists() for path in owned))
        self.assertEqual(self.e.snapshot()['jobs'], [])
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']}, 'interrupted')['ok'])

    def test_cleanup_failure_visible_retriable_never_emits_removed(self):
        job = self.create()
        owned = self.files(job)
        self.trash(job)
        self.events.clear()
        with patch.object(self.e, 'cleanup_purge', side_effect=PermissionError('private filesystem detail')):
            result = self.send('purge_recording', {'job_id': job['id']}, 'failed')
        self.assertFalse(result['ok'])
        self.assertEqual(result['error']['code'], 'purge_cleanup_failed')
        self.assertTrue(result['error']['recoverable'])
        self.assertNotIn('private filesystem', json.dumps(result))
        view = self.e.snapshot()['jobs'][0]
        self.assertTrue(view['purge_pending'])
        self.assertTrue(view['deleted_at'])
        self.assertFalse(any(e['kind'] == 'job_removed' for e in self.events))
        self.assertFalse(self.send('restore_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(all(path.exists() for path in owned))
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']}, 'retry-purge')['ok'])
        self.assertEqual(self.e.snapshot()['jobs'], [])

    def test_symlink_inside_owned_tree_never_deletes_link_target(self):
        job = self.create()
        self.files(job)
        outside = self.root / 'outside'
        outside.mkdir(); (outside / 'original').write_bytes(b'preserve')
        link = self.root / 'work' / job['id'] / 'escape'
        link.symlink_to(outside, target_is_directory=True)
        self.trash(job)
        self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertEqual((outside / 'original').read_bytes(), b'preserve')
        self.assertTrue(self.e.snapshot()['jobs'][0]['purge_pending'])
        link.unlink()
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_symlink_scope_parent_is_rejected(self):
        job = self.create()
        outside = self.root / 'external'
        (outside / job['id']).mkdir(parents=True)
        protected = outside / job['id'] / 'original'
        protected.write_bytes(b'preserve')
        (self.root / 'work').symlink_to(outside, target_is_directory=True)
        self.trash(job)
        self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertEqual(protected.read_bytes(), b'preserve')

    def test_import_copy_only_matching_entry_deleted_registry_hash_tombstone_retained(self):
        job = self.create(imported=True)
        cache, registry = self.import_registry(job)
        self.files(job)
        sibling = cache.with_name('other.wav'); sibling.write_bytes(b'preserve')
        registry['entries']['import:other'] = {**registry['entries'][job['source_reference']],
                                             'memo': memo('other'), 'path': str(sibling)}
        path = self.root / 'native/imports.json'; path.write_text(json.dumps(registry))
        self.trash(job)
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertFalse(cache.exists())
        self.assertEqual(sibling.read_bytes(), b'preserve')
        saved = json.loads(path.read_text())
        entry = saved['entries'][job['source_reference']]
        self.assertTrue(entry['purged'])
        self.assertIsNone(entry['memo']['original_title'])
        self.assertEqual(entry['hash'], 'a' * 64)
        self.assertEqual(saved['entries']['import:other'], registry['entries']['import:other'])

    def test_import_lock_contention_fails_promptly_then_retry_succeeds(self):
        job = self.create(imported=True)
        cache, _ = self.import_registry(job)
        self.trash(job)
        with open(self.root / 'native/imports.lock', 'a+b') as lock:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
            start = time.monotonic()
            self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
            self.assertLess(time.monotonic() - start, 1)
            self.assertTrue(cache.exists())
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_import_registry_mismatch_outside_path_or_symlink_never_deletes(self):
        job = self.create(imported=True)
        cache, valid = self.import_registry(job)
        protected = self.root / 'source.wav'; protected.write_bytes(b'original')
        self.trash(job)
        for bad in ('uuid', 'path', 'schema', 'symlink'):
            with self.subTest(bad=bad):
                registry = json.loads(json.dumps(valid))
                entry = registry['entries'][job['source_reference']]
                if bad == 'uuid': entry['memo']['uuid'] = 'another-uuid'
                if bad == 'path': entry['path'] = str(protected)
                if bad == 'schema': registry['schema_version'] = 2
                if bad == 'symlink':
                    cache.unlink(); cache.symlink_to(protected)
                (self.root / 'native/imports.json').write_text(json.dumps(registry))
                self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
                self.assertEqual(protected.read_bytes(), b'original')
                if bad == 'symlink': cache.unlink(); cache.write_bytes(b'imported audio')
        (self.root / 'native/imports.json').write_text(json.dumps(valid))
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_worker_stopped_before_cleanup_and_stale_completion_discarded(self):
        job = self.create()
        self.files(job)
        self.trash(job)
        output = self.root / 'late-result.json'
        output.write_text(json.dumps({'topic': 'late worker title'}))

        class Worker:
            timed_out = False
            proc = types.SimpleNamespace(returncode=None)
            def stop(inner): inner.proc.returncode = -15
            def poll(inner): return inner.proc.returncode

        active = {'job_id': job['id'], 'op': 'title', 'task': Worker(),
                  'output': output, 'index': None, 'generation': 0, 'pcm_hash': None}
        self.e.active = active
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertIsNotNone(active['task'].poll())
        self.assertIsNone(self.e.active)
        self.e.active = active
        with self.e.store.transaction(): self.e.complete_task()
        self.assertIsNone(self.e.active)
        self.assertEqual(self.e.snapshot()['jobs'], [])
        self.assertNotIn('topic', self.job(job['uuid']))

    def test_unconfirmed_worker_exit_never_deletes_owned_files(self):
        job = self.create()
        owned = self.files(job)
        self.trash(job)

        class Worker:
            def stop(inner): pass
            def poll(inner): return None

        self.e.active = {'job_id': job['id'], 'task': Worker(), 'op': 'title'}
        result = self.send('purge_recording', {'job_id': job['id']})
        self.assertFalse(result['ok'])
        self.assertTrue(all(path.exists() for path in owned))
        self.assertTrue(self.e.snapshot()['jobs'][0]['purge_pending'])
        self.e.active = None  # Synthetic worker has no real process to supervise.
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_missing_import_registry_or_copy_parent_is_visible_failure(self):
        job = self.create(imported=True)
        self.trash(job)
        self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(self.e.snapshot()['jobs'][0]['purge_pending'])
        cache, _ = self.import_registry(job)
        cache.unlink(); cache.parent.rmdir()
        self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(self.e.snapshot()['jobs'][0]['purge_pending'])
        cache.parent.mkdir()
        self.assertTrue(self.send('purge_recording', {'job_id': job['id']})['ok'])

    def test_partial_deletion_failure_restarts_without_requiring_deleted_copy(self):
        job = self.create(imported=True)
        cache, _ = self.import_registry(job)
        owned = self.files(job)
        self.trash(job)
        from voice_memo_transcriber import store
        real_unlink = store.os.unlink

        def unlink(name, *args, **kwargs):
            if name == 'private-data': raise PermissionError('synthetic removal denied')
            return real_unlink(name, *args, **kwargs)

        with patch('voice_memo_transcriber.store.os.unlink', side_effect=unlink):
            self.assertFalse(self.send('purge_recording', {'job_id': job['id']})['ok'])
        self.assertFalse(cache.exists())
        self.assertTrue(any(path.exists() for path in owned))
        self.e.close(); self.engine()
        self.assertEqual(self.e.snapshot()['jobs'], [])
        self.assertTrue(all(not path.exists() for path in owned))


if __name__ == '__main__': unittest.main()
