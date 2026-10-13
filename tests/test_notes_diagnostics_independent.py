"""Independent durable engine-boundary checks; synthetic artifacts, no inference."""
import json
import pathlib
import types
import unittest
from unittest.mock import patch

import test_notes_engine


class HeldProcess:
    def __init__(self, *args, **kwargs):
        self.proc = types.SimpleNamespace(returncode=None)
        self.timed_out = False

    def poll(self):
        return self.proc.returncode

    def stop(self):
        self.proc.returncode = -15


class NotesDiagnosticsIndependentTests(unittest.TestCase):
    setUp = test_notes_engine.NotesEngineTests.setUp
    tearDown = test_notes_engine.NotesEngineTests.tearDown
    engine = test_notes_engine.NotesEngineTests.engine
    send = test_notes_engine.NotesEngineTests.send
    job = test_notes_engine.NotesEngineTests.job
    drain = test_notes_engine.NotesEngineTests.drain
    snapshot = test_notes_engine.NotesEngineTests.snapshot
    ready = test_notes_engine.NotesEngineTests.ready
    ack = test_notes_engine.NotesEngineTests.ack

    def committed_job(self):
        job = self.ready('independent-diagnostics')
        self.ack(job)
        job = self.e.store.get(job['id'])
        self.assertEqual(job['status'], 'completed')
        self.before = job
        self.artifacts = {
            key: (job[key], pathlib.Path(job[key]).read_bytes())
            for key in ('artifact_path', 'notes_path', 'manifest_path', 'audio_path', 'pcm_path')
        }
        return job

    def begin_regeneration(self, job):
        self.assertTrue(self.send('generate_notes', {'job_id': job['id']})['ok'])
        with patch('voice_memo_transcriber.engine.ManagedProcess', HeldProcess):
            self.e.tick()
        self.assertIsNotNone(self.e.active)
        self.assertEqual(self.e.active['op'], 'notes')
        return self.e.active

    def deliver(self, active, diagnostics):
        active['output'].write_text(json.dumps({
            'notes_protocol': 'study_v1', 'notes_error_code': 'notes_failed',
            'completed_units': None, 'total_units': None,
            'notes_diagnostics': diagnostics,
        }))
        active['task'].proc.returncode = 0
        self.e.tick()

    def assert_artifacts_unchanged(self, job):
        for key, (path, data) in self.artifacts.items():
            self.assertEqual(job[key], path, key)
            self.assertEqual(pathlib.Path(path).read_bytes(), data, key)
        for key in ('publication_id', 'receipt', 'asr_chunks', 'aligned_chunks'):
            self.assertEqual(job[key], self.before[key], key)
        requests = [json.loads(p.read_text()) for p in
                    (self.root / 'work' / job['id'] / 'tasks').glob('*/request.json')]
        self.assertEqual(sum(r['op'] == 'asr' for r in requests), 1)
        self.assertEqual(sum(r['op'] == 'notes' for r in requests), 2)

    def assert_public_diagnostic(self, job_id, expected):
        self.assertTrue(self.send('snapshot')['ok'])
        snapshot = next(e['payload'] for e in reversed(self.events) if e['kind'] == 'snapshot')
        public = next(j for j in snapshot['jobs'] if j['id'] == job_id)
        self.assertEqual(public['notes_error']['diagnostics'], expected)
        changed = next(e['payload']['job'] for e in reversed(self.events)
                       if e['kind'] == 'job_changed' and e['payload']['job']['id'] == job_id)
        self.assertEqual(changed['notes_error']['diagnostics'], expected)

    def test_worker_subtype_survives_completion_events_database_and_restart(self):
        job = self.committed_job()
        diagnostic = {'stage': 'decode', 'reason': 'invalid_canonical_integer_refs',
                      'prompt_tokens': 731, 'generation_tokens': 219, 'finish_reason': 'stop'}
        self.deliver(self.begin_regeneration(job), diagnostic)
        after = self.e.store.get(job['id'])
        self.assertEqual(after['notes_error']['code'], 'notes_failed')
        self.assertEqual(after['notes_error']['diagnostics'], diagnostic)
        self.assertEqual(after['notes_status'], 'failed')
        self.assertEqual(after['status'], 'completed')
        self.assertFalse(after['queued'])
        self.assertIsNone(after['error'])
        self.assertIsNone(self.e.active)
        self.assert_artifacts_unchanged(after)
        self.assert_public_diagnostic(job['id'], diagnostic)
        self.e.close()
        self.engine()
        after_restart = self.e.store.get(job['id'])
        self.assertEqual(after_restart['notes_error'], after['notes_error'])
        self.assert_artifacts_unchanged(after_restart)
        self.assert_public_diagnostic(job['id'], diagnostic)

    def test_untrusted_worker_metadata_cannot_leak_through_persistence_or_events(self):
        job = self.committed_job()
        marker = 'SYNTHETIC_PRIVATE_EXCEPTION_AND_MODEL_RESPONSE_7ac3'
        source = json.loads(pathlib.Path(job['artifact_path']).read_text())['text']
        diagnostic = {'stage': 'decode', 'reason': 'invalid_text_bounds',
                      'prompt_tokens': 731, 'generation_tokens': 219,
                      'finish_reason': marker, 'exception': marker, 'response': marker,
                      'source': source, 'nested': {'raw': marker}}
        expected = {'stage': 'decode', 'reason': 'invalid_text_bounds',
                    'prompt_tokens': 731, 'generation_tokens': 219}
        self.events.clear()
        self.deliver(self.begin_regeneration(job), diagnostic)
        after = self.e.store.get(job['id'])
        self.assertEqual(after['notes_error']['diagnostics'], expected)
        self.assert_artifacts_unchanged(after)
        self.assert_public_diagnostic(job['id'], expected)
        self.e.close()
        self.engine()
        self.assert_public_diagnostic(job['id'], expected)
        durable = self.e.store.get(job['id'])
        for serialized in (json.dumps(durable), json.dumps(self.events)):
            self.assertNotIn(marker, serialized)
        # Internal ASR chunks legitimately retain source; diagnostic/public event
        # surfaces must not add transcript text to the failure report.
        for serialized in (json.dumps(durable['notes_error'], ensure_ascii=False),
                           json.dumps(self.events, ensure_ascii=False)):
            self.assertNotIn(source, serialized)
        self.assert_artifacts_unchanged(durable)

    def test_late_failure_cannot_attach_diagnostics_after_generation_invalidation(self):
        job = self.committed_job()
        active = self.begin_regeneration(job)
        # Represent an already committed newer generation; the old worker is late.
        newer = dict(self.before, generation=self.before.get('generation', 0) + 1)
        with self.e.store.transaction():
            self.e.store.put(newer)
        self.events.clear()
        self.deliver(active, {'stage': 'decode', 'reason': 'malformed_json'})
        after = self.e.store.get(job['id'])
        self.assertIsNone(after['notes_error'])
        self.assertEqual(after['notes_status'], 'completed')
        self.assertEqual(after['generation'], newer['generation'])
        self.assert_artifacts_unchanged(after)
        self.assertNotIn('malformed_json', json.dumps(self.events))


if __name__ == '__main__':
    unittest.main()
