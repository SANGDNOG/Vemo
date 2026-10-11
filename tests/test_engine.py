import unittest,tempfile,pathlib,sys,json,wave,struct,time,os
from unittest.mock import patch
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))

def memo(uid,day='2026-10-10T12:00:00+09:00',revision=None):
    return {'uuid':uid,'recorded_at':day,'recorded_at_source':'apple_db' if day else 'unknown','original_timezone':'Asia/Seoul','duration_s':1,'media_revision':revision,'completion_evidence':'finished','source_kind':'voice_memos','source_reference':'registry-'+uid,'original_title':'원본'}

class EngineTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.events=[]
    def tearDown(self):
        if hasattr(self,'e'):self.e.close()
        self.temp.cleanup()
    def engine(self,power=lambda:True):
        try:from voice_memo_transcriber.engine import Engine
        except ImportError:self.fail('durable Engine has not been implemented')
        self.e=Engine(self.root,self.root/'models',self.events.append,power_check=power,readiness=lambda:{'models_ready':True,'model_status':{}},worker_command=[sys.executable,str(pathlib.Path(__file__).parent/'fake_worker.py')])
        return self.e
    def send(self,kind,payload=None,rid=None):
        return self.e.command({'protocol_version':1,'request_id':rid or str(time.time_ns()),'kind':kind,'payload':payload or {}})
    def job(self,uid):return self.e.store.by_uuid(uid)
    def drain(self,condition,timeout=10):
        until=time.monotonic()+timeout
        while not condition() and time.monotonic()<until:self.e.tick();time.sleep(.01)
        self.assertTrue(condition(),'engine did not reach expected durable stage')
    def snapshot(self,job,values=(1,2,3)):
        p=self.root/(job['id']+'.wav')
        with wave.open(str(p),'wb') as f:f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(struct.pack('<'+str(len(values))+'h',*values))
        rev={'size':p.stat().st_size,'mtime_ns':p.stat().st_mtime_ns}
        self.send('snapshot_ready',{'job_id':job['id'],'owned_path':str(p),'revision_before':rev,'revision_after':rev})
    def test_baseline_new_import_and_dedupe(self):
        self.engine()
        with patch('voice_memo_transcriber.engine.time.time',return_value=1791687600.0):
            self.send('discover',{'memos':[memo('old')]},'baseline')
        self.assertEqual(self.job('old')['status'],'unprocessed')
        self.send('discover',{'memos':[memo('old'),memo('new','2026-10-11T12:00:01+09:00')]})
        self.assertTrue(self.job('new')['queued']);self.assertFalse(self.job('old')['queued'])
        self.send('import',{'memo':memo('explicit')},'import');self.send('import',{'memo':memo('explicit')},'import')
        self.assertEqual(len(self.e.store.jobs()),3);self.assertTrue(self.job('explicit')['queued'])
    def test_late_initial_cloud_sync_never_queues_history_and_cutoff_survives_restart(self):
        self.engine()
        with patch('voice_memo_transcriber.engine.time.time',return_value=1791687600.0):
            self.assertTrue(self.send('discover',{'memos':[]})['ok'])
        self.send('discover',{'memos':[memo('late-old'),memo('at-cutoff','2026-10-11T12:00:00+09:00'),memo('unknown',None),memo('new','2026-10-11T12:00:01+09:00')]})
        for uid in ('late-old','at-cutoff','unknown'):
            self.assertEqual(self.job(uid)['status'],'unprocessed');self.assertFalse(self.job(uid)['queued'])
        self.assertTrue(self.job('new')['queued'])
        self.e.close();self.engine()
        with patch('voice_memo_transcriber.engine.time.time',return_value=1791774000.0):
            self.send('discover',{'memos':[memo('after-restart','2026-10-11T12:00:02+09:00'),memo('another-late-old')]})
        self.assertTrue(self.job('after-restart')['queued']);self.assertFalse(self.job('another-late-old')['queued'])
        self.send('enqueue',{'uuids':['late-old','unknown']})
        self.assertTrue(self.job('late-old')['queued']);self.assertTrue(self.job('unknown')['queued'])
    def test_unstable_snapshot_rejected_without_audio_loss(self):
        self.engine();self.send('import',{'memo':memo('a')});self.e.tick();j=self.job('a')
        result=self.send('snapshot_ready',{'job_id':j['id'],'owned_path':str(self.root/'x'),'revision_before':{'size':1,'mtime_ns':1},'revision_after':{'size':2,'mtime_ns':2}})
        self.assertFalse(result['ok']);self.assertIsNone(self.job('a')['audio_path'])
    def test_prepared_pcm_is_exposed_to_snapshot_and_job_events(self):
        self.engine();self.send('import',{'memo':memo('playback')});self.e.tick();self.snapshot(self.job('playback'))
        self.drain(lambda:self.job('playback').get('pcm_path') is not None)
        job=self.job('playback');pcm=job['pcm_path']
        self.assertTrue(pathlib.Path(pcm).is_file())
        self.send('snapshot')
        public_job=next(e['payload']['jobs'][0] for e in reversed(self.events) if e['kind']=='snapshot')
        self.assertEqual(public_job.get('pcm_path'),pcm)
        changed=next(e['payload']['job'] for e in reversed(self.events) if e['kind']=='job_changed' and e['payload']['job']['id']==job['id'])
        self.assertEqual(changed.get('pcm_path'),pcm)
    def test_missing_date_preserves_transcript_and_holds_publication(self):
        self.engine();self.send('import',{'memo':memo('missing',None)});self.e.tick();self.snapshot(self.job('missing'))
        self.drain(lambda:self.job('missing')['status']=='needs_recording_date')
        job=self.job('missing');self.assertTrue(pathlib.Path(job['artifact_path']).is_file());self.assertIsNone(job['recording_date'])
        self.assertFalse(any(e['kind']=='publication_ready' for e in self.events))
        audio=job['audio_path'];transcript=pathlib.Path(job['artifact_path']).read_bytes()
        self.send('set_recording_date',{'job_id':job['id'],'recording_date':'2026-02-28'});self.drain(lambda:self.job('missing').get('publication_id') is not None)
        self.assertEqual(self.job('missing')['audio_path'],audio);self.assertEqual(pathlib.Path(job['artifact_path']).read_bytes(),transcript)
        self.assertEqual(self.job('missing')['date_provenance'],'user_supplied')
    def test_early_date_preserves_queue_state_and_survives_restart(self):
        initial_root=self.root
        for state in ('paused','models','ac_power','unprocessed'):
            with self.subTest(state=state):
                if hasattr(self,'e'):self.e.close()
                self.root=initial_root/state;self.root.mkdir()
                self.engine(lambda:state!='ac_power')
                self.e.readiness=lambda:{'models_ready':False,'model_status':{}}
                self.send('pause' if state=='paused' else 'resume')
                self.send('discover' if state=='unprocessed' else 'import',{'memos':[memo(state,None)]} if state=='unprocessed' else {'memo':memo(state,None)})
                self.e.tick();before=self.job(state)
                self.assertIsNone(before['artifact_path'])
                self.assertTrue(self.send('set_recording_date',{'job_id':before['id'],'recording_date':'2026-10-09'})['ok'])
                after=self.job(state)
                self.assertEqual(after,{**before,'recording_date':'2026-10-09','date_provenance':'user_supplied'})
                self.assertIsNone(self.e.active)
                self.assertFalse(any(e['kind']=='publication_ready' for e in self.events))
                self.assertFalse(self.send('set_recording_date',{'job_id':before['id'],'recording_date':'2026-02-30'})['ok'])
                self.assertEqual(self.job(state),after)
                self.assertFalse(self.send('retitle',{'job_id':before['id'],'generate':True})['ok'])
                self.e.close();self.engine(lambda:state!='ac_power')
                self.assertEqual(self.job(state),after)
    def test_publication_ack_loss_replay_validated_and_manual_retitle_no_asr(self):
        self.engine();self.send('import',{'memo':memo('a')});self.e.tick();self.snapshot(self.job('a'))
        self.drain(lambda:self.job('a').get('publication_id') is not None)
        j=self.job('a');pid=j['publication_id'];manifest=json.loads(pathlib.Path(j['manifest_path']).read_text());tr=pathlib.Path(j['artifact_path']).read_bytes()
        self.assertEqual(j['status'],'saving');self.e.close();del self.e;self.engine()
        self.e.tick();self.assertTrue(any(e['kind']=='publication_ready' and e['payload']['publication_id']==pid for e in self.events))
        wrong=self.send('publish_ack',{'job_id':j['id'],'publication_id':pid,'receipt':{'publication_id':pid,'output_manifest_path':'/out/manifest.json','artifact_hashes':{}}})
        self.assertFalse(wrong['ok']);self.assertEqual(self.job('a')['status'],'saving')
        receipt={'publication_id':pid,'output_manifest_path':'/out/manifest.json','artifact_hashes':{f['name']:f['sha256'] for f in manifest['files']}}
        self.send('publish_ack',{'job_id':j['id'],'publication_id':pid,'receipt':receipt})
        self.assertEqual(self.job('a')['status'],'completed')
        self.send('retitle',{'job_id':j['id'],'topic':'수정 제목'});self.drain(lambda:self.job('a')['publication_id']!=pid)
        self.assertEqual(pathlib.Path(self.job('a')['artifact_path']).read_bytes(),tr)
        self.assertEqual(self.job('a')['topic'],'수정 제목');self.assertEqual(self.job('a')['recording_date'],'2026-10-10')
    def test_policy_commands_emit_current_status_before_acknowledgement(self):
        self.engine()
        for kind,payload in [('pause',{}),('resume',{}),('set_policy',{'ac_only':False}),('set_policy',{'ac_only':True})]:
            with self.subTest(kind=kind,payload=payload):
                self.events.clear()
                result=self.send(kind,payload)
                self.assertTrue(result['ok'])
                self.assertEqual([event['kind'] for event in self.events],['engine_status','command_result'])
                self.assertEqual(self.events[0]['payload'],self.e.snapshot()['engine_status'])
                self.assertLess(self.events[0]['sequence'],self.events[1]['sequence'])

    def test_replayed_policy_command_reports_current_state_without_reapplying(self):
        self.engine();self.send('pause',rid='pause-once');self.send('resume')
        self.events.clear()
        self.assertTrue(self.send('pause',rid='pause-once')['ok'])
        self.assertFalse(self.e.store.get_meta('paused'))
        status=[event['payload'] for event in self.events if event['kind']=='engine_status']
        self.assertEqual(status,[self.e.snapshot()['engine_status']])
        self.assertFalse(status[0]['paused'])

    def test_rejected_policy_commands_do_not_emit_success_status_or_change_policy(self):
        self.engine();self.send('pause',rid='used-pause')
        before=self.e.snapshot()['engine_status']
        for kind,payload,rid in [('set_policy',{'ac_only':'false'},'invalid-policy'),('resume',{},'used-pause')]:
            with self.subTest(kind=kind):
                self.events.clear()
                self.assertFalse(self.send(kind,payload,rid)['ok'])
                self.assertEqual([event['kind'] for event in self.events],['command_result'])
                self.assertEqual(self.e.snapshot()['engine_status'],before)

    def test_pause_and_ac_gate_do_not_start_new_chunk(self):
        power=[False];self.engine(lambda:power[0]);self.send('import',{'memo':memo('a')});self.e.tick()
        self.assertEqual(self.job('a')['wait_reason'],'ac_power');self.assertFalse(any(e['kind']=='snapshot_requested' for e in self.events))
        power[0]=True;self.send('pause');self.e.tick();self.assertEqual(self.job('a')['status'],'paused')
        self.send('resume');self.e.last_power=float('-inf');self.e.tick();self.assertTrue(any(e['kind']=='snapshot_requested' for e in self.events))
    def test_policy_defaults_to_ac_and_false_allows_battery(self):
        self.engine(lambda:False);self.send('import',{'memo':memo('battery')});self.e.tick()
        self.assertEqual(self.job('battery')['wait_reason'],'ac_power')
        self.assertTrue(self.e.snapshot()['engine_status'].get('ac_only'),'default AC policy is missing')
        self.assertTrue(self.send('set_policy',{'ac_only':False})['ok']);self.e.tick()
        self.assertTrue(any(e['kind']=='snapshot_requested' for e in self.events))
        self.assertFalse(self.e.snapshot()['engine_status']['ac_only'])
        self.assertFalse(self.send('set_policy',{'ac_only':'false'})['ok'])
    def test_pcm_duplicate_does_not_reasr_but_changed_audio_revisions(self):
        self.engine();self.send('import',{'memo':memo('a',revision={'size':1,'mtime_ns':1})});self.e.tick();self.snapshot(self.job('a'))
        self.drain(lambda:self.job('a').get('publication_id') is not None)
        old=self.job('a');old_chunks=old['asr_chunks'];old_hash=old['pcm_hash']
        self.send('discover',{'memos':[memo('a',revision={'size':2,'mtime_ns':2})]});self.e.tick();self.snapshot(self.job('a'))
        self.drain(lambda:self.job('a')['phase']=='publish')
        self.assertEqual(self.job('a')['asr_chunks'],old_chunks);self.assertEqual(self.job('a')['pcm_hash'],old_hash)
        self.send('discover',{'memos':[memo('a',revision={'size':3,'mtime_ns':3})]});self.e.tick();self.snapshot(self.job('a'),(1,2,4))
        self.drain(lambda:self.job('a')['phase']=='publish')
        self.assertNotEqual(self.job('a')['pcm_hash'],old_hash);self.assertGreater(self.job('a')['revision'],old['revision'])
    def test_alignment_failure_keeps_text_and_title_failure_falls_back(self):
        self.engine();m=memo('fail-align-title');self.send('import',{'memo':m});self.e.tick();self.snapshot(self.job(m['uuid']))
        self.drain(lambda:self.job(m['uuid']).get('publication_id') is not None)
        j=self.job(m['uuid']);tr=json.loads(pathlib.Path(j['artifact_path']).read_text());self.assertEqual(tr['text'],'한국어 English 42')
        self.assertEqual(j['alignment_status'],'failed');self.assertEqual(j['title_status'],'fallback');self.assertEqual(j['topic'],'음성 녹음')
        manifest=json.loads(pathlib.Path(j['manifest_path']).read_text());self.assertEqual(len(manifest['files']),4)
        self.assertEqual({pathlib.Path(f['name']).suffix for f in manifest['files']},{'.txt','.md','.json'})
        self.assertEqual(j['notes_status'],'completed')
    def test_pause_mid_chunk_commits_once_and_restart_resumes_next(self):
        self.engine();self.send('import',{'memo':memo('long')});self.e.tick();self.snapshot(self.job('long'),[2]*16000*301)
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='asr')
        self.send('pause');self.drain(lambda:self.e.active is None)
        job=self.job('long');self.assertEqual(len(job['asr_chunks']),1);self.assertEqual(job['completed_units'],1);self.assertEqual(job['total_units'],2)
        self.e.tick();self.assertEqual(self.job('long')['status'],'paused')
        self.e.close();del self.e;self.engine();self.assertTrue(self.e.snapshot()['engine_status']['paused'])
        self.send('resume');self.drain(lambda:self.job('long').get('publication_id') is not None)
        job=self.job('long');self.assertEqual(len(job['asr_chunks']),2)
        segments=json.loads(pathlib.Path(job['artifact_path']).read_text())['segments']
        self.assertEqual([v['start_s'] for v in segments],[0,300]);self.assertEqual([v['end_s'] for v in segments],[300,301])
    def test_refresh_same_pcm_mid_alignment_preserves_resume_phase(self):
        self.engine();self.send('import',{'memo':memo('a',revision={'size':1,'mtime_ns':1})});self.e.tick();self.snapshot(self.job('a'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='align')
        self.send('discover',{'memos':[memo('a',revision={'size':2,'mtime_ns':2})]})
        self.drain(lambda:self.job('a')['phase']=='snapshot');self.snapshot(self.job('a'))
        self.drain(lambda:self.job('a').get('publication_id') is not None)
        self.assertEqual(len(self.job('a')['asr_chunks']),1);self.assertNotEqual(self.job('a')['status'],'failed')
    def test_discovered_date_correction_republishes_without_asr(self):
        self.engine();self.send('import',{'memo':memo('a')});self.e.tick();self.snapshot(self.job('a'));self.drain(lambda:self.job('a').get('publication_id') is not None)
        j=self.job('a');old_id=j['publication_id'];tr=pathlib.Path(j['artifact_path']).read_bytes()
        manifest=json.loads(pathlib.Path(j['manifest_path']).read_text());receipt={'publication_id':old_id,'output_manifest_path':'/out/manifest.json','artifact_hashes':{f['name']:f['sha256'] for f in manifest['files']}}
        self.send('publish_ack',{'job_id':j['id'],'publication_id':old_id,'receipt':receipt})
        self.send('discover',{'memos':[memo('a','2026-10-09T12:00:00+09:00')]});self.drain(lambda:self.job('a')['publication_id'] not in (None,old_id))
        self.assertEqual(self.job('a')['recording_date'],'2026-10-09');self.assertEqual(pathlib.Path(j['artifact_path']).read_bytes(),tr)
    def test_stale_ack_cannot_discard_pending_audio_refresh(self):
        self.engine();self.send('import',{'memo':memo('a',revision={'size':1,'mtime_ns':1})});self.e.tick();self.snapshot(self.job('a'));self.drain(lambda:self.job('a').get('publication_id') is not None)
        j=self.job('a');manifest=json.loads(pathlib.Path(j['manifest_path']).read_text());receipt={'publication_id':j['publication_id'],'output_manifest_path':'/out/manifest.json','artifact_hashes':{f['name']:f['sha256'] for f in manifest['files']}}
        self.send('discover',{'memos':[memo('a',revision={'size':2,'mtime_ns':2})]})
        result=self.send('publish_ack',{'job_id':j['id'],'publication_id':j['publication_id'],'receipt':receipt})
        self.assertFalse(result['ok']);self.assertTrue(self.job('a')['queued'])
    def test_title_hint_pause_and_restart_preserve_committed_checkpoint(self):
        self.engine();self.send('import',{'memo':memo('title-checkpoint')});self.e.tick();self.snapshot(self.job('title-checkpoint'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='title')
        self.send('pause');self.drain(lambda:self.e.active is None)
        job=self.job('title-checkpoint');self.assertEqual(job['phase'],'title');self.assertEqual(job['completed_units'],1)
        self.assertTrue(pathlib.Path(job.get('title_checkpoint','missing')).is_file())
        checkpoint=pathlib.Path(job['title_checkpoint']).read_bytes()
        self.e.close();del self.e;self.engine();self.e.tick();self.assertIsNone(self.e.active)
        self.send('resume');self.drain(lambda:self.job('title-checkpoint').get('publication_id') is not None)
        self.assertEqual(self.job('title-checkpoint')['topic'],'마지막 구간 주제');self.assertEqual(pathlib.Path(job['title_checkpoint']).read_bytes(),checkpoint)
    def test_shutdown_during_spawn_cleans_new_worker(self):
        import threading
        from unittest.mock import patch
        import voice_memo_transcriber.engine as engine_module
        self.engine();self.send('import',{'memo':memo('spawn-race')});self.e.tick();self.snapshot(self.job('spawn-race'))
        original=engine_module.ManagedProcess;entered=threading.Event();stop_finished=threading.Event()
        tasks=[]
        def delayed(command,**kwargs):
            entered.set();stop_finished.wait(.3)
            task=original(command,**kwargs);tasks.append(task);return task
        def stop():
            entered.wait();self.e.request_stop();stop_finished.set()
        thread=threading.Thread(target=stop);thread.start()
        with patch.object(engine_module,'ManagedProcess',delayed):self.e.tick()
        thread.join(timeout=3);self.assertFalse(thread.is_alive())
        self.assertTrue(all(task.proc.poll() is not None for task in tasks),'worker spawned after shutdown and survived')
    def test_completed_publication_ignores_late_error_ack(self):
        self.engine();self.send('import',{'memo':memo('late-ack')});self.e.tick();self.snapshot(self.job('late-ack'));self.drain(lambda:self.job('late-ack').get('publication_id') is not None)
        j=self.job('late-ack');manifest=json.loads(pathlib.Path(j['manifest_path']).read_text());receipt={'publication_id':j['publication_id'],'output_manifest_path':'/out/manifest.json','artifact_hashes':{f['name']:f['sha256'] for f in manifest['files']}}
        self.assertTrue(self.send('publish_ack',{'job_id':j['id'],'publication_id':j['publication_id'],'receipt':receipt})['ok'])
        self.send('publish_ack',{'job_id':j['id'],'publication_id':j['publication_id'],'error':{'code':'broker_busy','message':'delayed error','recoverable':True}})
        self.assertEqual(self.job('late-ack')['status'],'completed');self.assertEqual(self.job('late-ack')['receipt'],receipt)
    def test_date_edit_while_paused_after_asr_preserves_alignment(self):
        self.engine();self.send('import',{'memo':memo('date-during-align',None)});self.e.tick();self.snapshot(self.job('date-during-align'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='asr');self.send('pause');self.drain(lambda:self.e.active is None)
        self.assertEqual(self.job('date-during-align')['phase'],'align')
        self.assertTrue(self.send('set_recording_date',{'job_id':self.job('date-during-align')['id'],'recording_date':'2026-02-28'})['ok'])
        self.assertEqual(self.job('date-during-align')['phase'],'align')
        self.send('resume');self.drain(lambda:self.job('date-during-align').get('publication_id') is not None)
        self.assertEqual(self.job('date-during-align')['alignment_status'],'completed');self.assertEqual(len(self.job('date-during-align')['asr_chunks']),1)
    def test_manual_topic_before_alignment_keeps_timestamps_and_manual_topic(self):
        self.engine();self.send('import',{'memo':memo('manual-during-align')});self.e.tick();self.snapshot(self.job('manual-during-align'))
        self.drain(lambda:self.e.active is not None and self.e.active['op']=='asr');self.send('pause');self.drain(lambda:self.e.active is None)
        self.send('retitle',{'job_id':self.job('manual-during-align')['id'],'topic':'수동 주제'})
        self.assertEqual(self.job('manual-during-align')['phase'],'align')
        self.send('resume');self.drain(lambda:self.job('manual-during-align').get('publication_id') is not None)
        self.assertEqual(self.job('manual-during-align')['alignment_status'],'completed');self.assertEqual(self.job('manual-during-align')['topic'],'수동 주제')
    def test_failed_job_does_not_block_later_job(self):
        self.engine();self.send('import',{'memo':memo('fail-asr')});self.send('import',{'memo':memo('good')});self.e.tick();self.snapshot(self.job('fail-asr'))
        self.drain(lambda:self.job('fail-asr')['status']=='failed')
        self.e.tick();self.snapshot(self.job('good'));self.drain(lambda:self.job('good').get('publication_id') is not None)
        self.assertEqual(self.job('good')['status'],'saving')

if __name__=='__main__':unittest.main()
