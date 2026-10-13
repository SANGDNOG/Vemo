"""Durable lifecycle and stale external-work regression tests; synthetic media only."""
import json
import pathlib
import struct
import types
import unittest
import wave
from unittest.mock import patch

import test_engine
from test_engine import memo


class HeldProcess:
    def __init__(self, command, **kwargs):
        self.proc = types.SimpleNamespace(returncode=None)
        self.timed_out = False
        self.stopped = False

    def poll(self):
        return self.proc.returncode

    def stop(self):
        self.stopped = True
        self.proc.returncode = -15


class RecordingLifecycleTests(unittest.TestCase):
    setUp = test_engine.EngineTests.setUp
    tearDown = test_engine.EngineTests.tearDown
    engine = test_engine.EngineTests.engine
    send = test_engine.EngineTests.send
    job = test_engine.EngineTests.job
    drain = test_engine.EngineTests.drain

    def snapshot(self, job, values=(1, 2, 3), token=None):
        path = self.root / (job['id'] + '.wav')
        with wave.open(str(path), 'wb') as f:
            f.setparams((1, 2, 16000, 0, 'NONE', 'not compressed'))
            f.writeframes(struct.pack('<' + str(len(values)) + 'h', *values))
        revision = {'size': path.stat().st_size, 'mtime_ns': path.stat().st_mtime_ns}
        payload = {'job_id': job['id'], 'owned_path': str(path),
                   'revision_before': revision, 'revision_after': revision}
        token = token or self.job(job['uuid']).get('snapshot_request_id')
        if token is not None: payload['snapshot_request_id'] = token
        return self.send('snapshot_ready', payload)

    def ready(self, acknowledge=True):
        self.engine()
        item=memo('fixture', revision={'size': 40, 'mtime_ns': 1});item['duration_s']=3/16000
        self.send('import', {'memo': item})
        self.e.tick(); self.assertTrue(self.snapshot(self.job('fixture'))['ok'])
        self.drain(lambda: self.job('fixture').get('publication_id') is not None)
        job = self.job('fixture')
        if acknowledge: self.assertTrue(self.send('publish_ack', self.receipt(job))['ok'])
        return self.job('fixture')

    def receipt(self, job):
        manifest = json.loads(pathlib.Path(job['manifest_path']).read_text())
        return {'job_id': job['id'], 'publication_id': job['publication_id'], 'receipt': {
            'publication_id': job['publication_id'], 'output_manifest_path': '/synthetic/manifest.json',
            'artifact_hashes': {f['name']: f['sha256'] for f in manifest['files']}}}

    def requests(self, job):
        return [json.loads(p.read_text()) for p in (self.root / 'work' / job['id'] / 'tasks').glob('*/request.json')]

    def test_trash_is_durable_and_discovery_never_requeues_it(self):
        job = self.ready(); old = {k: pathlib.Path(job[k]).read_bytes() for k in
                                 ('audio_path', 'pcm_path', 'artifact_path', 'notes_path', 'manifest_path')}
        self.assertTrue(self.send('trash_recording', {'job_id': job['id']})['ok'])
        deleted = self.e.view(self.job('fixture'))['deleted_at']
        self.assertIsInstance(deleted, str)
        self.e.close(); self.engine()
        self.send('discover', {'memos': [memo('fixture', revision={'size': 80, 'mtime_ns': 2})]})
        self.send('backfill', {'uuids': ['fixture']}); self.events.clear(); self.e.tick()
        self.assertEqual(self.job('fixture')['deleted_at'], deleted)
        self.assertFalse(self.job('fixture')['queued']); self.assertIsNone(self.e.active)
        self.assertFalse(any(e['kind'] in {'snapshot_requested', 'publication_ready'} for e in self.events))
        for kind in ('retry', 'refresh_recording', 'generate_notes', 'retitle'):
            self.assertFalse(self.send(kind, {'job_id': job['id'], 'topic': 'late'})['ok'])
        for k, data in old.items(): self.assertEqual(pathlib.Path(job[k]).read_bytes(), data)
        self.assertTrue(self.send('restore_recording', {'job_id': job['id']})['ok'])
        self.assertIsNone(self.job('fixture')['deleted_at'])
        self.assertEqual(self.job('fixture')['artifact_path'], job['artifact_path'])
        self.e.tick(); self.assertEqual(self.job('fixture')['phase'], 'snapshot')

    def test_restore_completed_without_source_change_does_not_repeat_work(self):
        job = self.ready(); count = len(self.requests(job))
        self.send('trash_recording', {'job_id': job['id']}); self.send('restore_recording', {'job_id': job['id']})
        self.e.tick(); after = self.job('fixture')
        self.assertEqual(after['status'], 'completed'); self.assertFalse(after['queued'])
        self.assertEqual(after['manifest_path'], job['manifest_path']); self.assertEqual(len(self.requests(job)), count)

    def test_snapshot_token_rejects_pre_refresh_and_pre_restart_callbacks(self):
        self.engine(); self.send('import', {'memo': memo('fixture')}); self.e.tick()
        job = self.job('fixture'); old = job.get('snapshot_request_id')
        self.assertIsInstance(old, str)
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick()
        new = self.job('fixture')['snapshot_request_id']; self.assertNotEqual(new, old)
        for token in (old, None):
            payload = {'job_id': job['id'], 'error': {'code': 'late'}}
            if token is not None: payload['snapshot_request_id'] = token
            self.assertFalse(self.send('snapshot_ready', payload)['ok'])
        self.e.close(); self.engine(); self.e.tick()
        self.assertNotEqual(self.job('fixture')['snapshot_request_id'], new)
        self.assertFalse(self.snapshot(self.job('fixture'), token=new)['ok'])
        self.assertTrue(self.snapshot(self.job('fixture'))['ok'])

    def test_manual_title_before_transcription_survives_prepare_and_changed_refresh(self):
        self.engine(); self.send('discover', {'memos': [memo('fixture')]})
        job = self.job('fixture')
        self.assertTrue(self.send('retitle', {'job_id': job['id'], 'topic': '내 제목'})['ok'])
        self.assertEqual(self.job('fixture')['status'], 'unprocessed'); self.assertFalse(self.job('fixture')['queued'])
        self.send('enqueue', {'uuids': ['fixture']}); self.e.tick(); self.assertTrue(self.snapshot(self.job('fixture'))['ok'])
        self.drain(lambda: self.job('fixture').get('publication_id') is not None)
        self.assertEqual(self.job('fixture')['topic'], '내 제목')
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick(); self.snapshot(self.job('fixture'), (4, 5, 6))
        self.drain(lambda: self.job('fixture').get('publication_id') is not None)
        self.assertEqual(self.job('fixture')['topic'], '내 제목'); self.assertEqual(self.job('fixture')['title_status'], 'manual')
        self.assertFalse(any(r['op'] == 'title' for r in self.requests(job)))
        self.assertEqual(sum(r['op'] == 'notes' for r in self.requests(job)), 2)

    def test_revision_none_gap_and_metadata_baseline_duration_detection(self):
        job = self.ready()
        self.send('discover', {'memos': [memo('fixture', revision=None)]})
        self.assertEqual(self.job('fixture')['media_revision'], {'size': 40, 'mtime_ns': 1})
        self.send('discover', {'memos': [memo('fixture', revision={'size': 80, 'mtime_ns': 2})]})
        self.assertTrue(self.job('fixture')['refresh_requested'])

    def test_first_metadata_revision_is_baseline_but_duration_and_later_metadata_refresh(self):
        job = self.ready()
        m = memo('fixture', revision={'size': 40, 'mtime_ns': 1}); m['metadata_revision'] = 'zopt:1'
        m['duration_s'] = job['duration_s']
        self.send('discover', {'memos': [m]}); self.assertFalse(self.job('fixture')['refresh_requested'])
        m['metadata_revision'] = None; self.send('discover', {'memos': [m]})
        m['metadata_revision'] = 'zopt:2'; self.send('discover', {'memos': [m]})
        self.assertTrue(self.job('fixture')['refresh_requested'])

    def test_legacy_duration_mismatch_checks_once_without_refresh_loop(self):
        job = self.ready(); m = memo('fixture', revision={'size': 40, 'mtime_ns': 1}); m['duration_s'] = 200
        self.send('discover', {'memos': [m]}); self.assertTrue(self.job('fixture')['refresh_requested'])
        self.e.tick(); self.snapshot(self.job('fixture')); self.drain(lambda: self.job('fixture')['phase'] == 'publish')
        self.send('discover', {'memos': [m]}); self.assertFalse(self.job('fixture')['refresh_requested'])
        m['duration_s'] = 250; self.send('discover', {'memos': [m]}); self.assertTrue(self.job('fixture')['refresh_requested'])

    def test_refresh_busy_discards_late_worker_and_coalesces_requests(self):
        job = self.ready(); self.send('generate_notes', {'job_id': job['id']})
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess): self.e.tick()
        stale = self.e.active; old_notes = pathlib.Path(job['notes_path']).read_bytes()
        result = {'notes_protocol': 'study_v1', 'notes': json.loads(old_notes), 'completed_units': None, 'total_units': None}
        result['notes']['summary'][0]['text'] = 'stale replacement'
        stale['output'].write_text(json.dumps(result))
        self.assertTrue(self.send('refresh_recording', {'job_id': job['id']})['ok'])
        self.assertTrue(stale['task'].stopped); self.assertIsNone(self.e.active)
        self.e.tick(); token = self.job('fixture')['snapshot_request_id']
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick()
        self.assertEqual(self.job('fixture')['snapshot_request_id'], token)
        stale['task'].proc.returncode = 0; self.e.active = stale
        self.e.tick(); self.assertIsNone(self.e.active)
        self.assertEqual(self.job('fixture')['notes_path'], job['notes_path'])
        self.assertTrue(self.snapshot(self.job('fixture'))['ok'])
        self.drain(lambda: self.job('fixture')['phase'] == 'publish')
        self.assertEqual(pathlib.Path(job['notes_path']).read_bytes(), old_notes)
        self.assertEqual(self.job('fixture')['notes_status'], 'completed')
        self.assertEqual(sum(r['op']=='notes' for r in self.requests(job)), 3)

    def test_failed_changed_refresh_keeps_previous_result_paths(self):
        job = self.ready(); paths = {k: job[k] for k in ('audio_path', 'pcm_path', 'artifact_path', 'notes_path', 'manifest_path')}
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick(); self.snapshot(self.job('fixture'), (4, 5))
        current = self.job('fixture'); current['uuid'] = 'fail-asr'
        with self.e.store.transaction(): self.e.store.put(current)
        self.drain(lambda: self.e.store.get(job['id'])['status'] == 'failed')
        failed = self.e.store.get(job['id'])
        for key, path in paths.items():
            self.assertEqual(failed[key], path, key); self.assertTrue(pathlib.Path(path).is_file())
        self.assertFalse(self.send('publish_ack', self.receipt(job))['ok'])

    def test_trash_active_worker_and_restore_resumes_uncommitted_phase(self):
        self.engine(); self.send('import', {'memo': memo('fixture')}); self.e.tick(); self.snapshot(self.job('fixture'))
        self.drain(lambda: self.e.active is not None and self.e.active['op'] == 'asr')
        active = self.e.active; self.send('trash_recording', {'job_id': active['job_id']})
        self.assertIsNone(self.e.active); self.e.close(); self.engine()
        self.send('restore_recording', {'job_id': active['job_id']})
        self.drain(lambda: self.job('fixture').get('publication_id') is not None)
        self.assertEqual(len(self.job('fixture')['asr_chunks']), 1)

    def test_auto_study_notes_waits_for_verification_and_failure_is_bounded(self):
        self.engine(); self.e.readiness = lambda: {'models_ready': True, 'notes_ready': False, 'model_status': {'notes': {'state': 'verifying'}}}
        self.e.models = self.e.readiness(); self.send('import', {'memo': memo('fixture')}); self.e.tick(); self.snapshot(self.job('fixture'))
        self.drain(lambda: self.job('fixture')['phase'] == 'notes' and self.e.active is None)
        self.e.tick(); job = self.job('fixture')
        self.assertEqual(job['notes_status'], 'pending'); self.assertIsNone(job['publication_id'])
        self.e.models = {'models_ready': True, 'notes_ready': True, 'model_status': {}}
        current = self.job('fixture'); current['uuid'] = 'fail-notes'
        with self.e.store.transaction(): self.e.store.put(current)
        self.drain(lambda: self.e.store.get(job['id']).get('publication_id') is not None)
        for _ in range(5): self.e.tick()
        self.assertEqual(self.e.store.get(job['id'])['notes_status'], 'failed')
        self.assertEqual(sum(r['op'] == 'notes' for r in self.requests(job)), 1)

    def test_retry_active_worker_stops_it_and_resumes_same_uncommitted_phase(self):
        self.engine(); self.send('import', {'memo': memo('fixture')}); self.e.tick(); self.snapshot(self.job('fixture'))
        self.drain(lambda: self.e.active is not None and self.e.active['op']=='asr')
        active=self.e.active
        self.assertTrue(self.send('retry', {'job_id': active['job_id']})['ok'])
        self.assertIsNotNone(active['task'].proc.returncode); self.assertIsNone(self.e.active)
        self.drain(lambda: self.job('fixture').get('publication_id') is not None)
        self.assertEqual(len(self.job('fixture')['asr_chunks']), 1)

    def test_manual_title_during_failed_refresh_survives_rollback(self):
        job=self.ready(); old_notes=job['notes_path']
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick()
        self.assertTrue(self.e.view(self.job('fixture'))['refresh_pending'])
        self.assertTrue(self.send('retitle', {'job_id': job['id'], 'topic': '최신 수동 제목'})['ok'])
        self.assertTrue(self.snapshot(self.job('fixture'), (4, 5))['ok'])
        current=self.job('fixture'); current['uuid']='fail-asr'
        with self.e.store.transaction(): self.e.store.put(current)
        self.drain(lambda: self.e.store.get(job['id'])['status']=='failed')
        after=self.e.store.get(job['id'])
        self.assertEqual(after['topic'], '최신 수동 제목'); self.assertEqual(after['title_status'], 'manual')
        self.assertEqual(after['notes_path'], old_notes)
        self.assertFalse(self.e.view(after)['refresh_pending'])

    def test_superseding_same_pcm_check_retains_rollback_until_new_asr_finishes(self):
        job=self.ready(); old=job['artifact_path']
        self.send('refresh_recording', {'job_id': job['id']}); self.e.tick(); self.snapshot(self.job('fixture'), (4, 5))
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess):
            self.drain(lambda: self.e.active is not None and self.e.active['op']=='prepare')
        # Complete only prepare with a real PCM candidate so ASR is held at its boundary.
        from voice_memo_transcriber.media import pcm_identity
        active=self.e.active; request=json.loads(active['output'].with_name('request.json').read_text())
        for key in ('audio_path', 'pcm_path'): pathlib.Path(request[key]).write_bytes(pathlib.Path(request['source']).read_bytes())
        h,d,n=pcm_identity(request['pcm_path'])
        active['output'].write_text(json.dumps({'pcm_hash':h,'duration_s':d,'frames':n,'audio_path':request['audio_path'],'pcm_path':request['pcm_path']}));active['task'].proc.returncode=0
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess): self.e.tick()
        m=memo('fixture', revision={'size':80,'mtime_ns':2});m['duration_s']=job['duration_s']
        self.send('discover', {'memos':[m]});self.e.tick();self.snapshot(self.job('fixture'), (4,5))
        current=self.job('fixture');current['uuid']='fail-asr'
        with self.e.store.transaction(): self.e.store.put(current)
        self.drain(lambda:self.e.store.get(job['id'])['status']=='failed')
        self.assertEqual(self.e.store.get(job['id'])['artifact_path'], old)

    def test_unprocessed_trash_does_not_become_automatic_work_on_restore(self):
        self.engine();self.send('discover', {'memos':[memo('fixture', revision={'size':40,'mtime_ns':1})]})
        job=self.job('fixture');self.send('trash_recording', {'job_id':job['id']})
        self.send('discover', {'memos':[memo('fixture', revision={'size':80,'mtime_ns':2})]})
        self.send('restore_recording', {'job_id':job['id']});self.events.clear();self.e.tick()
        self.assertEqual(self.job('fixture')['status'], 'unprocessed');self.assertFalse(self.job('fixture')['queued'])
        self.assertFalse(any(e['kind']=='snapshot_requested' for e in self.events))

    def test_legacy_duration_discovery_coalesces_while_snapshot_is_pending(self):
        job=self.ready();job.pop('source_duration_s',None);job.pop('checked_source_duration_s',None)
        with self.e.store.transaction():self.e.store.put(job)
        item=memo('fixture',revision=job['media_revision']);item['duration_s']=200
        self.send('discover', {'memos':[item]});self.e.tick();token=self.job('fixture')['snapshot_request_id']
        self.send('discover', {'memos':[item]});self.e.tick()
        self.assertEqual(self.job('fixture')['snapshot_request_id'],token)
