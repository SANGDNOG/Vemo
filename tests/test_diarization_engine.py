"""Synthetic store/process fixtures only; no ML, decoders, network or subprocesses."""
import json
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch
from voice_memo_transcriber.engine import Engine
from voice_memo_transcriber.diarization import build_document
from voice_memo_transcriber.diarization_io import sources, commit
from test_engine import memo


class FakeProcess:
    instances = []
    def __init__(self, command, **kwargs):
        self.command = command; self.proc = SimpleNamespace(returncode=None)
        self.timed_out = False; self.stopped = False; self.instances.append(self)
    def poll(self): return self.proc.returncode
    def stop(self): self.stopped = True; self.proc.returncode = -15


class SpeakerEngineTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(); self.root = Path(self.temp.name)
        FakeProcess.instances = []
        self.process = patch('voice_memo_transcriber.engine.ManagedProcess', FakeProcess); self.process.start()
        self.e = Engine(self.root, self.root/'models', lambda _: None, power_check=lambda: True,
                        readiness=lambda: {'models_ready': True, 'notes_ready': True, 'model_status': {}})
        self.send('import', {'memo': memo('speaker')})
        job = self.e.store.by_uuid('speaker'); artifact = self.root/'artifacts'/job['id']/'transcript.json'
        artifact.parent.mkdir(parents=True)
        artifact.write_text(json.dumps({'text': '안녕 세상', 'segments': [{'start_s': 0, 'end_s': 1, 'text': '안녕'}, {'start_s': 1, 'end_s': 2, 'text': '세상'}], 'alignment_status': 'completed'}))
        audio = self.root/'audio.wav'; audio.write_bytes(b'synthetic owned original audio')
        job.update(status='completed', phase='publish', queued=False, artifact_path=str(artifact), audio_path=str(audio), pcm_path=str(audio), duration_s=2, pcm_hash='a'*64, notes_path='preserved-notes', publication_id='preserved-publication')
        with self.e.store.transaction(): self.e.store.put(job)
        self.jid = job['id']; self.sequence = 0
    def tearDown(self): self.e.close(); self.process.stop(); self.temp.cleanup()
    def send(self, kind, payload):
        return self.e.command({'protocol_version': 1, 'request_id': str(__import__('time').time_ns()), 'kind': kind, 'payload': payload})
    def job(self): return self.e.store.get(self.jid)
    def output(self, value):
        Path(self.e.active['output']).write_text(json.dumps(value)); self.e.active['task'].proc.returncode = 0
    def doc(self):
        j = self.job(); h, transcript = sources(self.root, j['audio_path'], j['artifact_path'])
        return build_document(h, transcript['text'], transcript['segments'], [{'start_s': 0, 'end_s': 2, 'speaker': 0}])
    def old_result(self):
        j = self.job(); path = commit(self.root, j['artifact_path'], self.doc()); j['speakers_path'] = path
        with self.e.store.transaction(): self.e.store.put(j)
        return path, Path(path).read_bytes()
    def analyze(self):
        self.assertTrue(self.send('analyze_speakers', {'job_id': self.jid})['ok']); self.e.tick()
        self.assertEqual(self.e.active['op'], 'diarize')
    def test_commit_optional_result_without_changing_originals(self):
        before = self.job(); raw = Path(before['artifact_path']).read_bytes()
        self.analyze(); self.output({'speakers': self.doc()}); self.e.tick(); after = self.job()
        self.assertEqual(after['speakers_status'], 'completed')
        self.assertEqual(Path(after['artifact_path']).read_bytes(), raw)
        for key in ('status', 'phase', 'notes_path', 'publication_id'): self.assertEqual(after[key], before[key])
        self.assertEqual(Path(after['speakers_path']).name, 'speakers.json')
    def test_cancel_preserves_old_result_and_stops_helper(self):
        path, raw = self.old_result(); self.analyze(); task = self.e.active['task']
        self.assertTrue(self.send('cancel_speakers', {'job_id': self.jid})['ok'])
        self.assertTrue(task.stopped); self.assertIsNone(self.e.active); self.e.tick()
        self.assertEqual(self.job()['speakers_status'], 'cancelled'); self.assertEqual(self.job()['speakers_path'], path)
        self.assertEqual(Path(path).read_bytes(), raw); self.assertEqual(self.job()['status'], 'completed')
    def test_missing_runtime_and_timeout_preserve_old_then_explicit_retry(self):
        path, raw = self.old_result()
        for outcome in ('missing', 'timeout'):
            self.analyze()
            if outcome == 'missing': self.output({'speaker_error': 'diarization_runtime_missing'})
            else: self.e.active['task'].timed_out = True; self.e.active['task'].proc.returncode = -9
            self.e.tick(); self.assertEqual(self.job()['speakers_status'], 'failed')
            self.assertEqual(self.job()['status'], 'completed'); self.assertEqual(self.job()['speakers_path'], path)
            self.assertEqual(Path(path).read_bytes(), raw)
        self.analyze(); self.output({'speakers': self.doc()}); self.e.tick()
        self.assertEqual(self.job()['speakers_status'], 'completed')
    def test_stale_source_and_duplicate_output_rejected(self):
        path, _ = self.old_result(); self.analyze(); doc = self.doc()
        Path(self.job()['audio_path']).write_bytes(b'changed'); self.output({'speakers': doc}); self.e.tick()
        self.assertEqual(self.job()['speakers_status'], 'failed'); self.assertEqual(self.job()['speakers_path'], path)
        self.analyze(); Path(self.e.active['output']).write_text('{"speakers":{},"speakers":{}}')
        self.e.active['task'].proc.returncode = 0; self.e.tick(); self.assertEqual(self.job()['speakers_status'], 'failed')
    def test_rename_requires_generation_and_spawns_no_process(self):
        self.old_result(); doc = self.doc(); j = self.job()
        stored = json.loads(Path(j['speakers_path']).read_bytes())
        payload = dict(job_id=self.jid, speaker_id='speaker_1', name=' 민수 ', generation=stored['generation'])
        self.assertTrue(self.send('rename_speaker', payload)['ok'])
        self.assertEqual(FakeProcess.instances, [])
        self.assertEqual(json.loads(Path(self.job()['speakers_path']).read_bytes())['speakers'][0]['name'], '민수')
        payload['generation'] = doc['generation']; self.assertFalse(self.send('rename_speaker', payload)['ok'])
    def test_single_slot_blocks_overlap_with_asr_or_notes(self):
        self.send('analyze_speakers', {'job_id': self.jid})
        task = FakeProcess([]); self.e.active = {'job_id': 'other-job', 'op': 'notes', 'task': task}
        self.e.tick(); self.assertEqual(len(FakeProcess.instances), 1); self.assertEqual(self.job()['speakers_status'], 'queued')
        self.e.active = None; self.e.tick(); self.assertEqual(self.e.active['op'], 'diarize')
        self.assertFalse(self.send('generate_notes', {'job_id': self.jid})['ok'])
    def test_restart_cancels_optional_intent_without_changing_completed_job(self):
        path, raw = self.old_result(); self.analyze(); self.e.close()
        self.e = Engine(self.root, self.root/'models', lambda _: None, readiness=lambda: {'models_ready': True})
        self.assertEqual(self.job()['status'], 'completed'); self.assertEqual(self.job()['speakers_status'], 'cancelled')
        self.assertEqual(self.job()['speakers_path'], path); self.assertEqual(Path(path).read_bytes(), raw)

    def test_explicit_model_setup_uses_download_only_helper(self):
        result = self.send('download_models', {'speakers_only': True})
        self.assertTrue(result['ok'])
        self.e.tick()
        self.assertIsNone(self.e.active)
        self.assertTrue(self.e.download['speakers_only'])
        self.assertIn('voice_memo_transcriber.diarization_models', self.e.download['task'].command)
        self.assertEqual(self.job()['status'], 'completed')
        self.e.download['task'].proc.returncode = 0
        self.e.tick()
        self.assertIsNone(self.e.download)

    def test_model_setup_rejects_invalid_or_conflicting_scope(self):
        for payload in ({'speakers_only': 'yes'}, {'speakers_only': True, 'notes_only': True}):
            self.assertFalse(self.send('download_models', payload)['ok'])
        self.assertFalse(FakeProcess.instances)

    def test_known_speaker_count_is_snapshotted_into_helper_request(self):
        self.assertTrue(self.send('analyze_speakers', {'job_id': self.jid, 'num_speakers': 2})['ok'])
        self.e.tick()
        request = Path(self.e.active['task'].command[-2])
        self.assertEqual(json.loads(request.read_bytes())['num_speakers'], 2)
        self.send('cancel_speakers', {'job_id': self.jid})
        self.assertTrue(self.send('analyze_speakers', {'job_id': self.jid})['ok'])
        self.e.tick()
        request = Path(self.e.active['task'].command[-2])
        self.assertIsNone(json.loads(request.read_bytes())['num_speakers'])

    def test_invalid_known_speaker_count_never_queues_analysis(self):
        for count in (0, -1, 21, True, 2.0, '2'):
            self.assertFalse(self.send('analyze_speakers', {'job_id': self.jid, 'num_speakers': count})['ok'])
        self.assertNotIn(self.job().get('speakers_status'), ('queued', 'analyzing'))
        self.assertFalse(FakeProcess.instances)

    def test_excess_automatic_clusters_preserve_prior_output_with_actionable_error(self):
        path, raw = self.old_result()
        self.analyze()
        self.output({'speaker_error': 'diarization_too_many_speakers'})
        self.e.tick()
        after = self.job()
        self.assertEqual(after['status'], 'completed')
        self.assertEqual(after['speakers_status'], 'failed')
        self.assertEqual(after['speakers_error']['code'], 'diarization_too_many_speakers')
        self.assertIn('화자 수를 지정해 다시 분석', after['speakers_error']['message'])
        self.assertEqual(after['speakers_path'], path)
        self.assertEqual(Path(path).read_bytes(), raw)
