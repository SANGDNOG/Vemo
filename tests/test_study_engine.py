import json
import pathlib
import sys
import types
import unittest
from unittest.mock import patch

import test_notes_engine
from test_engine import memo


class StudyEngineTests(unittest.TestCase):
    setUp = test_notes_engine.NotesEngineTests.setUp
    tearDown = test_notes_engine.NotesEngineTests.tearDown
    engine = test_notes_engine.NotesEngineTests.engine
    send = test_notes_engine.NotesEngineTests.send
    job = test_notes_engine.NotesEngineTests.job
    drain = test_notes_engine.NotesEngineTests.drain
    snapshot = test_notes_engine.NotesEngineTests.snapshot
    ready = test_notes_engine.NotesEngineTests.ready
    ack = test_notes_engine.NotesEngineTests.ack

    def requests(self, job):
        return [json.loads(p.read_text()) for p in (self.root / 'work' / job['id'] / 'tasks').glob('*/request.json')]

    def test_null_protocol_defaults_to_study_and_pause_replays_only_uncommitted_notes(self):
        self.engine(); self.send('import', {'memo': memo('interrupted-notes')})
        job = self.job('interrupted-notes'); job['notes_protocol'] = None
        with self.e.store.transaction(): self.e.store.put(job)
        self.e.tick(); self.snapshot(self.job('interrupted-notes'))
        self.drain(lambda: (self.e.active is not None and self.e.active['op'] == 'notes') or self.job('interrupted-notes').get('notes_status') == 'failed')
        self.assertIsNotNone(self.e.active, self.job('interrupted-notes').get('notes_error'))
        active = self.job('interrupted-notes')
        self.assertEqual(active['notes_protocol'], 'study_v1')
        self.assertIsNone(active['completed_units']); self.assertIsNone(active['total_units'])
        self.send('pause'); self.drain(lambda: self.e.active is None)
        self.assertEqual(self.job('interrupted-notes')['status'], 'paused')
        self.assertNotEqual(self.job('interrupted-notes')['notes_status'], 'completed')
        transcript = pathlib.Path(active['artifact_path']).read_bytes()
        self.e.close(); self.engine(); self.send('resume')
        self.drain(lambda: self.job('interrupted-notes').get('publication_id') is not None)
        after = self.job('interrupted-notes')
        self.assertEqual(after['notes_status'], 'completed')
        self.assertEqual(pathlib.Path(after['artifact_path']).read_bytes(), transcript)
        requests = self.requests(after)
        self.assertEqual(sum(r['op'] == 'asr' for r in requests), 1)
        self.assertEqual(sum(r['op'] == 'notes' for r in requests), 2)

    def test_notes_inventory_verifying_waits_without_destroying_prior_notes(self):
        job = self.ready('verifying'); self.ack(job)
        old = pathlib.Path(job['notes_path']).read_bytes()
        self.e.readiness = lambda: {'models_ready': True, 'notes_ready': False, 'model_status': {'notes': {'state': 'verifying'}}}
        self.e.models = self.e.readiness()
        self.send('generate_notes', {'job_id': job['id']})
        self.e.tick()
        pending = self.job('verifying')
        self.assertEqual(pending['notes_status'], 'pending')
        self.assertIsNone(pending['notes_error'])
        self.assertEqual(pending['phase'], 'notes')
        self.assertIsNone(self.e.active)
        self.assertEqual(pathlib.Path(job['notes_path']).read_bytes(), old)
        self.e.readiness = lambda: {'models_ready': True, 'notes_ready': True, 'model_status': {}}
        self.e.models = self.e.readiness()
        self.drain(lambda: self.job('verifying')['notes_status'] == 'completed')
        self.assertEqual(sum(r['op'] == 'asr' for r in self.requests(job)), 1)

    def test_missing_notes_model_does_not_prevent_transcript_publication(self):
        self.engine()
        self.e.readiness = lambda: {'models_ready': True, 'notes_ready': False, 'model_status': {}}
        self.send('import', {'memo': memo('without-notes-model')}); self.e.tick(); self.snapshot(self.job('without-notes-model'))
        self.drain(lambda: self.job('without-notes-model').get('publication_id') is not None)
        job = self.job('without-notes-model')
        self.assertEqual(json.loads(pathlib.Path(job['artifact_path']).read_text())['text'], '한국어 English 42')
        self.assertEqual(job['notes_error']['code'], 'notes_model_missing')
        self.assertEqual(sum(r['op'] == 'asr' for r in self.requests(job)), 1)
        self.assertFalse(any(r['op'] == 'notes' for r in self.requests(job)))

    def test_typed_failures_preserve_prior_notes_transcript_and_publication(self):
        job = self.ready('study-errors'); self.ack(job)
        job = self.job('study-errors')
        paths = {key: (job[key], pathlib.Path(job[key]).read_bytes()) for key in ['notes_path', 'artifact_path', 'manifest_path']}
        for code in ['notes_input_too_long', 'notes_output_truncated', 'notes_model_missing']:
            with self.subTest(code=code):
                current = self.e.store.get(job['id']); current['uuid'] = 'typed-notes-' + code
                with self.e.store.transaction(): self.e.store.put(current)
                self.assertTrue(self.send('generate_notes', {'job_id': job['id']})['ok'])
                self.drain(lambda: self.e.store.get(job['id'])['notes_status'] == 'failed')
                after = self.e.store.get(job['id'])
                self.assertEqual(after['notes_error']['code'], code)
                self.assertEqual(after['status'], 'completed')
                for key, (path, data) in paths.items():
                    self.assertEqual(after[key], path); self.assertEqual(pathlib.Path(path).read_bytes(), data)
        self.assertEqual(sum(r['op'] == 'asr' for r in self.requests(job)), 1)

    def test_wrong_source_digest_cannot_replace_prior_notes(self):
        job = self.ready('identity'); self.ack(job); job = self.job('identity')
        before = pathlib.Path(job['notes_path']).read_bytes()
        job['uuid'] = 'wrong-source-notes'
        with self.e.store.transaction(): self.e.store.put(job)
        self.send('generate_notes', {'job_id': job['id']})
        self.drain(lambda: self.e.store.get(job['id'])['notes_status'] in {'completed', 'failed'})
        after = self.e.store.get(job['id'])
        self.assertEqual(after['notes_status'], 'failed')
        self.assertEqual(after['notes_path'], job['notes_path'])
        self.assertEqual(pathlib.Path(after['notes_path']).read_bytes(), before)

    def test_notes_only_regeneration_does_not_require_asr_models(self):
        job = self.ready('notes-without-asr-models'); self.ack(job)
        self.e.readiness = lambda: {'models_ready': False, 'notes_ready': True, 'model_status': {}}
        self.e.models = self.e.readiness()
        self.assertTrue(self.send('generate_notes', {'job_id': job['id']})['ok'])
        self.drain(lambda: self.job('notes-without-asr-models')['notes_status'] == 'completed')
        self.assertEqual(sum(r['op'] == 'asr' for r in self.requests(job)), 1)
        self.assertEqual(sum(r['op'] == 'notes' for r in self.requests(job)), 2)

    def test_notes_timeout_preserves_previous_artifacts_and_does_not_repeat_asr(self):
        job = self.ready('timeout-prior'); self.ack(job); job = self.job('timeout-prior')
        before = {key: (job[key], pathlib.Path(job[key]).read_bytes()) for key in ['notes_path', 'artifact_path', 'manifest_path']}
        job['uuid'] = 'interrupted-notes'
        with self.e.store.transaction(): self.e.store.put(job)
        self.e.worker_timeout = .01
        self.send('generate_notes', {'job_id': job['id']})
        self.drain(lambda: self.e.store.get(job['id'])['notes_status'] == 'failed')
        after = self.e.store.get(job['id'])
        self.assertEqual(after['status'], 'completed')
        self.assertIsNotNone(after['notes_error'])
        self.assertIsNone(self.e.active)
        for key, (path, data) in before.items():
            self.assertEqual(after[key], path); self.assertEqual(pathlib.Path(path).read_bytes(), data)
        requests = self.requests(job)
        self.assertEqual(sum(r['op'] == 'asr' for r in requests), 1)
        self.assertLessEqual(sum(r['op'] == 'notes' for r in requests), 3)

    def test_download_notes_only_validates_boolean_and_survives_restart(self):
        self.engine()
        for invalid in ['true', 1, None]:
            self.assertFalse(self.send('download_models', {'notes_only': invalid})['ok'])
        self.assertTrue(self.send('download_models', {'notes_only': True})['ok'])
        self.e.close(); self.engine()
        commands = []
        class Task:
            def __init__(self, command, **kwargs): commands.append(command)
            def stop(self): pass
            def poll(self): return None
        with patch('voice_memo_transcriber.engine.ManagedProcess', Task): self.e.poll_download()
        self.assertEqual(len(commands), 1)
        self.assertIn('--notes-only', commands[0])
        self.assertIn('voice_memo_transcriber.download_models', commands[0])
