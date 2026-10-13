"""Independent lifecycle regressions. Synthetic owned files; no live recordings."""
import datetime
import json
import pathlib
import types
import unittest
from unittest.mock import patch

import test_engine
from test_engine import memo


class HeldProcess:
    """Controllable external-work boundary; real engine/store/publication code runs."""
    def __init__(self, command, **kwargs):
        self.proc = types.SimpleNamespace(returncode=None)
        self.timed_out = False
        self.stopped = False

    def poll(self):
        return self.proc.returncode

    def stop(self):
        self.stopped = True
        self.proc.returncode = -15


class RecordingLifecycleIndependentTests(unittest.TestCase):
    setUp = test_engine.EngineTests.setUp
    tearDown = test_engine.EngineTests.tearDown
    engine = test_engine.EngineTests.engine
    send = test_engine.EngineTests.send
    job = test_engine.EngineTests.job
    drain = test_engine.EngineTests.drain
    snapshot = test_engine.EngineTests.snapshot

    def ready(self, uid='fixture', acknowledge=True):
        self.engine()
        item = memo(uid, revision={'size': 40, 'mtime_ns': 1})
        item['duration_s'] = 3 / 16000  # Matches the three-frame synthetic snapshot.
        self.assertTrue(self.send('import', {'memo': item})['ok'])
        self.e.tick(); self.snapshot(self.job(uid))
        self.drain(lambda: self.job(uid).get('publication_id') is not None)
        job = self.job(uid)
        if acknowledge:
            self.assertTrue(self.send('publish_ack', self.receipt(job))['ok'])
        return self.job(uid)

    def receipt(self, job):
        manifest = json.loads(pathlib.Path(job['manifest_path']).read_text())
        return {'job_id': job['id'], 'publication_id': job['publication_id'], 'receipt': {
            'publication_id': job['publication_id'], 'output_manifest_path': '/synthetic/manifest.json',
            'artifact_hashes': {item['name']: item['sha256'] for item in manifest['files']}}}

    def retained_files(self, job):
        paths = [pathlib.Path(job[k]) for k in ['audio_path', 'pcm_path', 'artifact_path', 'notes_path', 'manifest_path']]
        paths.extend(pathlib.Path(x['path']) for x in json.loads(pathlib.Path(job['manifest_path']).read_text())['files'])
        return {p: p.read_bytes() for p in paths}

    def assert_retained(self, files):
        for path, content in files.items():
            self.assertTrue(path.is_file(), str(path))
            self.assertEqual(path.read_bytes(), content, str(path))

    def requests(self, job):
        return [json.loads(p.read_text()) for p in (self.root / 'work' / job['id'] / 'tasks').glob('*/request.json')]

    def revision_memo(self, job, revision):
        item = memo(job['uuid'], revision=revision)
        item['duration_s'] = job['duration_s']
        return item

    def test_revision_after_temporary_unavailability_is_not_swallowed(self):
        job = self.ready()
        unavailable = self.revision_memo(job, None)
        unavailable['completion_evidence'] = 'unknown'
        self.send('discover', {'memos': [unavailable]})
        self.send('discover', {'memos': [self.revision_memo(job, {'size': 80, 'mtime_ns': 2})]})
        self.events.clear(); self.e.tick()
        self.assertEqual(self.job('fixture')['phase'], 'snapshot', 'A -> None -> appended B must refresh media')
        self.assertTrue(any(e['kind'] == 'snapshot_requested' for e in self.events))
        self.assertTrue(pathlib.Path(job['artifact_path']).is_file())

    def test_trash_retains_files_and_does_not_resurrect_on_discovery_or_restart(self):
        job = self.ready(); files = self.retained_files(job)
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])
        deleted = self.job('fixture').get('deleted_at')
        self.assertIsInstance(deleted, str)
        self.assertIsNotNone(datetime.datetime.fromisoformat(deleted.replace('Z', '+00:00')).tzinfo)
        self.assertEqual(self.e.view(self.job('fixture'))['deleted_at'], deleted)
        self.assertFalse(self.job('fixture')['queued']); self.assert_retained(files)
        self.e.close(); self.engine(); self.events.clear()
        self.send('discover', {'memos': [self.revision_memo(job, {'size': 80, 'mtime_ns': 2})]})
        self.e.tick()
        self.assertEqual(self.job('fixture')['deleted_at'], deleted)
        self.assertFalse(self.job('fixture')['queued']); self.assertIsNone(self.e.active)
        self.assertFalse(any(e['kind'] in {'snapshot_requested', 'publication_ready'} for e in self.events))
        for kind in ['retry', 'generate_notes', 'refresh_recording']:
            self.assertFalse(self.send(kind, {'job_id': job['id']})['ok'], kind)
        self.send('enqueue', {'uuids': ['fixture']}); self.send('backfill', {'uuids': ['fixture']})
        self.e.tick(); self.assertFalse(self.job('fixture')['queued']); self.assert_retained(files)
        self.assertTrue(self.send('restore_recording', {'job_id': job['id']})['ok'])
        restored = self.job('fixture')
        self.assertIsNone(restored.get('deleted_at')); self.assert_retained(files)
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])

    def test_trash_rejects_late_snapshot_and_publication_receipt(self):
        job = self.ready(acknowledge=False); receipt = self.receipt(job); files = self.retained_files(job)
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])
        self.assertFalse(self.send('publish_ack', receipt)['ok'])
        self.assertFalse(self.send('snapshot_ready', {'job_id': job['id'], 'error': {'code': 'late'}})['ok'])
        self.events.clear(); self.e.last_replay.clear(); self.e.tick()
        self.assertFalse(any(e['kind'] in {'snapshot_requested', 'publication_ready'} for e in self.events))
        self.assert_retained(files)

    def test_trash_stops_active_notes_and_ignores_already_written_result(self):
        job = self.ready(); files = self.retained_files(job)
        self.send('generate_notes', {'job_id': job['id']})
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess): self.e.tick()
        active = self.e.active; self.assertEqual(active['op'], 'notes')
        notes = json.loads(pathlib.Path(job['notes_path']).read_text())
        notes['summary'][0]['text'] = '삭제 뒤에 도착한 결과'
        active['output'].write_text(json.dumps({'notes_protocol': 'study_v1', 'notes': notes,
                                               'completed_units': None, 'total_units': None}))
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(active['task'].stopped); self.assertIsNone(self.e.active)
        self.events.clear(); self.e.tick()
        after = self.job('fixture')
        self.assertIsNotNone(after.get('deleted_at')); self.assertFalse(after['queued'])
        self.assertEqual(after['notes_path'], job['notes_path']); self.assert_retained(files)
        self.assertFalse(any(e['kind'] == 'publication_ready' for e in self.events))

    def test_refresh_identical_pcm_preserves_outputs_without_repeating_models(self):
        job = self.ready(); files = self.retained_files(job)
        before = self.requests(job)
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])
        self.e.tick(); self.assertEqual(self.job('fixture')['phase'], 'snapshot')
        self.snapshot(self.job('fixture'))
        self.drain(lambda: self.job('fixture')['phase'] == 'publish')
        after = self.job('fixture')
        self.assertEqual(after['pcm_hash'], job['pcm_hash'])
        self.assertEqual(after['artifact_path'], job['artifact_path'])
        self.assertEqual(after['notes_path'], job['notes_path']); self.assert_retained(files)
        for op in ['asr', 'align', 'title', 'notes']:
            self.assertEqual(sum(r['op'] == op for r in self.requests(job)), sum(r['op'] == op for r in before), op)

    def test_refresh_changed_pcm_reprocesses_and_keeps_old_files(self):
        job = self.ready(); files = self.retained_files(job)
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])
        self.e.tick(); self.snapshot(self.job('fixture'), values=(1, 2, 3, 4, 5))
        self.drain(lambda: self.job('fixture').get('publication_id') is not None and self.job('fixture')['pcm_hash'] != job['pcm_hash'])
        after = self.job('fixture')
        self.assertGreater(after['revision'], job['revision'])
        self.assertEqual(sum(r['op'] == 'asr' for r in self.requests(job)), 2)
        self.assert_retained(files)

    def test_revision_during_active_notes_cannot_publish_before_source_recheck(self):
        job = self.ready(); old_receipt = self.receipt(job)
        self.send('generate_notes', {'job_id': job['id']})
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess): self.e.tick()
        active = self.e.active; self.assertEqual(active['op'], 'notes')
        notes = json.loads(pathlib.Path(job['notes_path']).read_text())
        self.send('discover', {'memos': [self.revision_memo(job, {'size': 80, 'mtime_ns': 2})]})
        active['output'].write_text(json.dumps({'notes_protocol': 'study_v1', 'notes': notes,
                                               'completed_units': None, 'total_units': None}))
        active['task'].proc.returncode = 0
        self.events.clear(); self.e.tick()
        self.assertFalse(self.send('publish_ack', old_receipt)['ok'])
        self.assertEqual(self.job('fixture')['phase'], 'snapshot')
        self.assertFalse(any(e['kind'] == 'publication_ready' for e in self.events))

    def test_refresh_invalidates_pending_publication_and_rejects_old_ack(self):
        job = self.ready(acknowledge=False); files = self.retained_files(job); receipt = self.receipt(job)
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])
        self.assertFalse(self.send('publish_ack', receipt)['ok'])
        self.events.clear(); self.e.last_replay.clear(); self.e.tick()
        self.assertEqual(self.job('fixture')['phase'], 'snapshot')
        self.assertFalse(any(e['kind'] == 'publication_ready' for e in self.events)); self.assert_retained(files)

    def test_lifecycle_commands_reject_invalid_job_ids_without_mutation(self):
        job = self.ready()
        for kind in ['trash_recording', 'restore_recording', 'refresh_recording']:
            for payload in [{}, {'job_id': None}, {'job_id': 1}, {'job_id': ''}, {'job_id': 'unknown'}]:
                before = self.job('fixture')
                with self.subTest(kind=kind, payload=payload):
                    self.assertFalse(self.send(kind, payload)['ok'])
                    self.assertEqual(self.job('fixture'), before)

    def metadata_memo(self, job, metadata_revision, duration=None):
        item = memo(job['uuid'], revision=job['media_revision'])
        item['duration_s'] = job['duration_s'] if duration is None else duration
        item['metadata_revision'] = metadata_revision
        return item

    def test_first_metadata_revision_on_legacy_job_preserves_completed_output(self):
        job = self.ready(); files = self.retained_files(job)
        job.pop('metadata_revision', None)
        with self.e.store.transaction(): self.e.store.put(job)
        self.events.clear()
        self.assertTrue(self.send('discover', {'memos': [self.metadata_memo(job, '17')]})['ok'])
        self.e.tick()
        after = self.job('fixture')
        self.assertEqual(after.get('metadata_revision'), '17')
        self.assertFalse(after.get('refresh_requested'))
        self.assertFalse(after['queued']); self.assertIsNone(self.e.active)
        self.assertEqual(after['publication_id'], job['publication_id'])
        self.assertFalse(any(e['kind'] == 'snapshot_requested' for e in self.events))
        self.assert_retained(files)

    def test_later_metadata_revision_change_requests_snapshot_with_unchanged_media_stat(self):
        job = self.ready()
        self.send('discover', {'memos': [self.metadata_memo(job, '17')]})
        self.e.tick()
        self.assertFalse(self.job('fixture').get('refresh_requested'))
        self.e.close(); self.engine()
        self.assertTrue(self.send('discover', {'memos': [self.metadata_memo(job, '18')]})['ok'])
        self.events.clear(); self.e.tick()
        self.assertEqual(self.job('fixture')['phase'], 'snapshot')
        self.assertTrue(any(e['kind'] == 'snapshot_requested' for e in self.events))

    def test_first_metadata_observation_does_not_mask_changed_source_duration(self):
        job = self.ready()
        item = self.metadata_memo(job, '17', duration=job['duration_s'] + 20)
        self.assertTrue(self.send('discover', {'memos': [item]})['ok'])
        self.events.clear(); self.e.tick()
        self.assertEqual(self.job('fixture')['phase'], 'snapshot')
        self.assertTrue(any(e['kind'] == 'snapshot_requested' for e in self.events))

    def test_metadata_revision_nonstring_rejected_without_mutating_saved_job(self):
        job = self.ready()
        for invalid in [17, True, [], {}]:
            with self.subTest(metadata_revision=invalid):
                before = self.job('fixture')
                self.assertFalse(self.send('discover', {'memos': [self.metadata_memo(job, invalid)]})['ok'])
                self.assertEqual(self.job('fixture'), before)

    def test_snapshot_token_before_trash_cannot_be_reused_after_restore(self):
        job = self.ready()
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])
        self.e.tick()
        old_token = self.job('fixture').get('snapshot_request_id')
        self.assertIsInstance(old_token, str)
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(self.send('restore_recording', {'job_id': job['id']})['ok'])
        self.e.tick()
        current = self.job('fixture')
        self.assertIsInstance(current.get('snapshot_request_id'), str)
        self.assertNotEqual(current['snapshot_request_id'], old_token)
        path = pathlib.Path(job['audio_path'])
        revision = {'size': path.stat().st_size, 'mtime_ns': path.stat().st_mtime_ns}
        payload = {'job_id': job['id'], 'owned_path': str(path), 'revision_before': revision, 'revision_after': revision}
        for extra in [{}, {'snapshot_request_id': old_token}, {'snapshot_request_id': 'unrelated'}]:
            with self.subTest(token=extra):
                before = self.job('fixture')
                self.assertFalse(self.send('snapshot_ready', {**payload, **extra})['ok'])
                self.assertEqual(self.job('fixture'), before)
        self.assertTrue(self.send('snapshot_ready', {**payload, 'snapshot_request_id': current['snapshot_request_id']})['ok'])
        self.assertEqual(self.job('fixture')['phase'], 'prepare')

    def test_new_source_revision_invalidates_inflight_snapshot_token(self):
        job = self.ready()
        self.send('discover', {'memos': [self.revision_memo(job, {'size': 80, 'mtime_ns': 2})]})
        self.e.tick()
        old_token = self.job('fixture').get('snapshot_request_id')
        self.assertIsInstance(old_token, str)
        self.send('discover', {'memos': [self.revision_memo(job, {'size': 120, 'mtime_ns': 3})]})
        self.e.tick()
        current = self.job('fixture')
        self.assertNotEqual(current.get('snapshot_request_id'), old_token)
        before = self.job('fixture')
        self.assertFalse(self.send('snapshot_ready', {'job_id': job['id'], 'snapshot_request_id': old_token,
                                                     'error': {'code': 'late_copy_error'}})['ok'])
        self.assertEqual(self.job('fixture'), before)
