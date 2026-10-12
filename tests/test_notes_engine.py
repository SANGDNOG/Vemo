import json,pathlib,unittest
import test_engine
from test_engine import memo

class NotesEngineTests(unittest.TestCase):
    setUp=test_engine.EngineTests.setUp
    tearDown=test_engine.EngineTests.tearDown
    engine=test_engine.EngineTests.engine
    send=test_engine.EngineTests.send
    job=test_engine.EngineTests.job
    drain=test_engine.EngineTests.drain
    snapshot=test_engine.EngineTests.snapshot
    def ready(self,uid='notes',day='2026-10-10T12:00:00+09:00'):
        self.engine();self.send('import',{'memo':memo(uid,day)});self.e.tick();self.snapshot(self.job(uid))
        self.drain(lambda:self.job(uid).get('publication_id') is not None if day else self.job(uid)['status']=='needs_recording_date')
        return self.job(uid)
    def ack(self,job):
        manifest=json.loads(pathlib.Path(job['manifest_path']).read_text())
        self.assertTrue(self.send('publish_ack',{'job_id':job['id'],'publication_id':job['publication_id'],'receipt':{'publication_id':job['publication_id'],'output_manifest_path':'/synthetic/manifest.json','artifact_hashes':{f['name']:f['sha256'] for f in manifest['files']}}})['ok'])
    def test_new_transcript_automatically_gets_notes_and_wire_fields(self):
        job=self.ready()
        self.assertEqual(job.get('notes_status'),'completed');self.assertTrue(pathlib.Path(job['notes_path']).is_file())
        self.assertTrue(any(e['kind']=='job_changed' and e['payload']['job']['status']=='noting' for e in self.events))
        view=self.e.view(job);self.assertEqual(view['notes_path'],job['notes_path']);self.assertIsNone(view['notes_error'])
    def test_legacy_completed_note_only_no_asr_and_old_publication_preserved(self):
        job=self.ready();self.ack(job);job=self.job('notes');old=pathlib.Path(job['manifest_path']);before=old.read_bytes();tr=pathlib.Path(job['artifact_path']).read_bytes()
        for key in ('notes_status','notes_path','notes_error'):job.pop(key,None)
        with self.e.store.transaction():self.e.store.put(job)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.assertEqual(self.job('notes')['phase'],'notes');self.assertEqual(self.job('notes')['notes_status'],'pending')
        self.drain(lambda:self.job('notes')['notes_status']=='completed' and self.job('notes')['phase']=='publish')
        self.drain(lambda:self.job('notes')['publication_id'] is not None)
        after=self.job('notes');self.assertEqual(pathlib.Path(after['artifact_path']).read_bytes(),tr);self.assertEqual(after['asr_chunks'],job['asr_chunks']);self.assertEqual(old.read_bytes(),before)
        ops=[json.loads(p.read_text())['op'] for p in (self.root/'work'/job['id']/'tasks').glob('*/request.json')]
        self.assertEqual(ops.count('asr'),1);self.assertEqual(ops.count('notes'),2)
    def test_missing_date_allows_notes_only_without_asr(self):
        job=self.ready(day=None);tr=pathlib.Path(job['artifact_path']).read_bytes()
        for key in ('notes_status','notes_path','notes_error'):job.pop(key,None)
        with self.e.store.transaction():self.e.store.put(job)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.job('notes')['status']=='needs_recording_date')
        self.assertEqual(self.job('notes')['notes_status'],'completed');self.assertEqual(pathlib.Path(job['artifact_path']).read_bytes(),tr)
    def test_notes_failure_preserves_transcript_and_supports_retry(self):
        job=self.ready('fail-notes');self.assertEqual(job.get('notes_status'),'failed');self.assertEqual(job['status'],'saving');self.ack(job)
        tr=pathlib.Path(job['artifact_path']).read_bytes();manifest=pathlib.Path(job['manifest_path']).read_bytes()
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.e.store.get(job['id'])['notes_status']=='failed')
        self.assertEqual(self.e.store.get(job['id'])['status'],'completed');self.assertEqual(pathlib.Path(job['manifest_path']).read_bytes(),manifest)
        self.assertEqual(pathlib.Path(job['artifact_path']).read_bytes(),tr);self.assertIsNotNone(self.e.store.get(job['id'])['notes_error'])
        current=self.e.store.get(job['id']);current['uuid']='recovered-notes'
        with self.e.store.transaction():self.e.store.put(current)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.e.store.get(job['id'])['notes_status']=='completed')
    def test_notes_pause_restart_checkpoint_does_not_replay_committed_chunk(self):
        self.engine();self.send('import',{'memo':memo('checkpoint-notes')})
        job=self.job('checkpoint-notes');job['notes_protocol']='legacy'
        with self.e.store.transaction():self.e.store.put(job)
        self.e.tick();self.snapshot(self.job('checkpoint-notes'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='notes')
        self.send('pause');self.drain(lambda:self.e.active is None)
        job=self.job('checkpoint-notes');self.assertEqual(job['phase'],'notes');self.assertEqual(job['completed_units'],1)
        checkpoint_path=pathlib.Path(job['notes_checkpoint']);checkpoint=checkpoint_path.read_bytes()
        # A pre-study database has a committed checkpoint but no protocol field.
        job.pop('notes_protocol',None)
        with self.e.store.transaction():self.e.store.put(job)
        self.e.close();self.engine();self.e.tick();self.assertIsNone(self.e.active)
        self.send('resume');self.drain(lambda:self.job('checkpoint-notes').get('publication_id') is not None)
        job=self.job('checkpoint-notes');self.assertEqual(job['notes_status'],'completed');self.assertEqual(checkpoint_path.read_bytes(),checkpoint)
        requests=[json.loads(p.read_text()) for p in (self.root/'work'/job['id']/'tasks').glob('*/request.json')]
        self.assertEqual(sum(r['op']=='asr' for r in requests),1);self.assertEqual(sum(r['op']=='notes' for r in requests),2)
    def test_generate_notes_rejects_unready_or_busy_jobs(self):
        self.engine();self.send('import',{'memo':memo('unready')})
        self.assertFalse(self.send('generate_notes',{'job_id':self.job('unready')['id']})['ok'])

    def test_malformed_worker_result_isolated_from_successful_transcript(self):
        job=self.ready('malformed-notes')
        self.assertEqual(job['notes_status'],'failed')
        self.assertEqual(json.loads(pathlib.Path(job['artifact_path']).read_text())['text'],'한국어 English 42')
        self.assertEqual(len(json.loads(pathlib.Path(job['manifest_path']).read_text())['files']),4)
    def test_failed_regeneration_retains_prior_notes_artifact_and_warning(self):
        job=self.ready('good-notes');self.ack(job);job=self.job('good-notes')
        notes_path=job['notes_path'];before=pathlib.Path(notes_path).read_bytes();old_manifest=job['manifest_path'];pid=job['publication_id']
        job['uuid']='fail-notes'
        with self.e.store.transaction():self.e.store.put(job)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.e.store.get(job['id'])['notes_status']=='failed')
        after=self.e.store.get(job['id']);self.assertEqual(after['notes_path'],notes_path);self.assertEqual(pathlib.Path(notes_path).read_bytes(),before)
        self.assertEqual(after['manifest_path'],old_manifest);self.assertEqual(after['publication_id'],pid);self.assertEqual(after['status'],'completed');self.assertEqual(after['stage'],'saving');self.assertIsNotNone(after['notes_error'])
    def test_failed_job_with_transcript_can_generate_notes_without_audio(self):
        job=self.ready('failed-recovery');job.update(status='failed',phase='align',queued=False,topic=None,pcm_path=None,audio_path=None)
        with self.e.store.transaction():self.e.store.put(job)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.job('failed-recovery')['notes_status']=='completed' and self.job('failed-recovery')['phase']=='publish')
        requests=[json.loads(p.read_text())['op'] for p in (self.root/'work'/job['id']/'tasks').glob('*/request.json')]
        self.assertEqual(requests.count('asr'),1);self.assertEqual(requests.count('notes'),2)
    def test_stale_publication_ack_cannot_complete_pending_notes(self):
        job=self.ready();self.ack(job)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.assertFalse(self.send('publish_ack',{'job_id':job['id'],'publication_id':job['publication_id'],'error':{'code':'late'}})['ok'])
        self.assertEqual(self.job('notes')['phase'],'notes')

    def test_restart_interrupting_notes_replays_only_uncommitted_notes_work(self):
        self.engine();self.send('import',{'memo':memo('interrupted-notes')});self.e.tick();self.snapshot(self.job('interrupted-notes'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='notes')
        job=self.job('interrupted-notes');transcript=pathlib.Path(job['artifact_path']).read_bytes()
        self.e.close();self.engine()
        self.drain(lambda:self.job('interrupted-notes').get('publication_id') is not None)
        after=self.job('interrupted-notes');self.assertEqual(after['notes_status'],'completed');self.assertEqual(pathlib.Path(after['artifact_path']).read_bytes(),transcript)
        requests=[json.loads(p.read_text())['op'] for p in (self.root/'work'/job['id']/'tasks').glob('*/request.json')]
        self.assertEqual(requests.count('asr'),1);self.assertEqual(requests.count('notes'),2)

    def test_malformed_checkpoint_isolated_and_generate_notes_recovers(self):
        from voice_memo_transcriber.artifacts import atomic_json
        from voice_memo_transcriber.notes import source_digest
        job=self.ready('checkpoint-notes');self.ack(job)
        original=pathlib.Path(job['artifact_path']).read_bytes();prior_notes=job['notes_path'];prior_manifest=job['manifest_path']
        source=json.loads(original)['text']
        for index,bad in enumerate(([],None,1,'invalid',{}, {'index':'1','total_units':2,'source_sha256':source_digest(source),'notes':[]}, {'index':True,'total_units':True,'source_sha256':source_digest(source),'notes':{}}, {'index':1,'total_units':2,'source_sha256':'0'*64,'notes':{}}, {'index':1,'total_units':2,'source_sha256':source_digest(source),'notes':[]}, {'index':1,'total_units':2,'source_sha256':source_digest(source),'notes':{'summary':'bad','highlights':[],'topics':[]}})):
            with self.subTest(checkpoint=bad):
                self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
                path=self.root/f'bad-checkpoint-{index}.json';atomic_json(path,bad)
                current=self.job('checkpoint-notes');current['notes_checkpoint']=str(path);current['notes_protocol']='legacy'
                with self.e.store.transaction():self.e.store.put(current)
                self.e.tick();after=self.job('checkpoint-notes')
                self.assertIsNone(self.e.active);self.assertEqual(after['notes_status'],'failed');self.assertEqual(after['status'],'completed')
                self.assertEqual(after['notes_path'],prior_notes);self.assertEqual(after['manifest_path'],prior_manifest)
                self.assertEqual(pathlib.Path(after['artifact_path']).read_bytes(),original)
        self.assertTrue(self.send('generate_notes',{'job_id':job['id']})['ok'])
        self.drain(lambda:self.job('checkpoint-notes')['notes_status']=='completed')
