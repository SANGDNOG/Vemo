import unittest,pathlib,tempfile,subprocess,sys,os,json,time,select,signal,sqlite3
ROOT=pathlib.Path(__file__).resolve().parents[1]

class ServiceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name);self.procs=[];self.buffer=b''
        self.env={**os.environ,'PYTHONPATH':str(ROOT/'src')}
    def tearDown(self):
        for p in self.procs:
            if p.poll() is None:p.kill();p.wait(timeout=5)
            for stream in (p.stdin,p.stdout,p.stderr):
                if stream and not stream.closed:stream.close()
        self.temp.cleanup()
    def start(self):
        self.assertTrue((ROOT/'src/voice_memo_transcriber/service.py').exists(),'JSONL service missing')
        p=subprocess.Popen([sys.executable,'-m','voice_memo_transcriber.service','--state-root',str(self.root),'--model-root',str(self.root/'models')],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=self.env)
        self.procs.append(p);return p
    def send(self,p,kind,rid,payload=None,version=1):
        p.stdin.write(json.dumps({'kind':kind,'request_id':rid,'protocol_version':version,'payload':payload or {}}).encode()+b'\n');p.stdin.flush()
    def events(self,p,predicate,timeout=5):
        events=[];end=time.monotonic()+timeout
        while time.monotonic()<end:
            ready,_,_=select.select([p.stdout],[],[],.1)
            if ready:
                block=os.read(p.stdout.fileno(),65536)
                if not block:break
                self.buffer+=block
                while b'\n' in self.buffer:
                    line,self.buffer=self.buffer.split(b'\n',1);event=json.loads(line);events.append(event)
                    if predicate(event):return events
        self.fail('expected JSONL event missing; exit='+str(p.poll()))
    def test_real_jsonl_version_unknown_idempotency_restart(self):
        p=self.start();self.send(p,'hello','h');events=self.events(p,lambda e:e['kind']=='command_result')
        self.assertTrue(any(e['kind']=='snapshot' for e in events));self.assertEqual([e['sequence'] for e in events],sorted({e['sequence'] for e in events}))
        self.send(p,'pause','bad-version',version=2);self.assertFalse(self.events(p,lambda e:e['kind']=='command_result')[-1]['payload']['ok'])
        self.send(p,'shell','bad-kind');self.assertFalse(self.events(p,lambda e:e['kind']=='command_result')[-1]['payload']['ok'])
        self.send(p,'pause','p');self.events(p,lambda e:e['kind']=='command_result');self.send(p,'pause','p');self.assertTrue(self.events(p,lambda e:e['kind']=='command_result')[-1]['payload']['ok'])
        p.stdin.close();self.assertEqual(p.wait(timeout=5),0)
        self.buffer=b'';q=self.start();self.send(q,'snapshot','s');events=self.events(q,lambda e:e['kind']=='command_result');self.assertTrue(next(e for e in events if e['kind']=='snapshot')['payload']['engine_status']['paused'])
    def test_second_service_cannot_write_and_shutdown_releases_lock(self):
        p=self.start();self.send(p,'hello','h');self.events(p,lambda e:e['kind']=='command_result')
        q=self.start();self.assertNotEqual(q.wait(timeout=5),0)
        self.send(p,'shutdown','bye');self.assertEqual(p.wait(timeout=5),0)
        self.buffer=b'';r=self.start();self.send(r,'hello','new');self.events(r,lambda e:e['kind']=='command_result')
    def test_oversize_line_rejects_without_consuming_next_command(self):
        p=self.start();p.stdin.write(b'x'*(8*1024*1024+1)+b'\n');p.stdin.flush()
        self.assertFalse(self.events(p,lambda e:e['kind']=='command_result',10)[-1]['payload']['ok'])
        self.send(p,'hello','h');self.assertTrue(self.events(p,lambda e:e['kind']=='command_result')[-1]['payload']['ok'])
    def test_restored_queue_waits_for_native_handshake_and_current_policy(self):
        launcher=ROOT/'tests/policy_launcher.py'
        self.assertTrue(launcher.exists(),'policy lifecycle fixture missing')
        p=subprocess.Popen([sys.executable,str(launcher),str(self.root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=self.env);self.procs.append(p)
        # No stale persisted battery policy may start work before handshake.
        ready,_,_=select.select([p.stdout],[],[],.3)
        self.assertFalse(ready,'queued work started before native handshake')
        self.send(p,'set_policy','initial-policy',{'ac_only':True})
        self.events(p,lambda e:e['kind']=='command_result')
        self.send(p,'snapshot','state');events=self.events(p,lambda e:e['kind']=='command_result')
        snapshot=next(e for e in events if e['kind']=='snapshot')['payload']
        self.assertEqual(snapshot['jobs'][0]['wait_reason'],'ac_power')
        self.send(p,'set_policy','allow-battery',{'ac_only':False})
        self.events(p,lambda e:e['kind']=='snapshot_requested')
    def test_slow_integrity_scan_does_not_block_hello_or_eof_cleanup(self):
        p=subprocess.Popen([sys.executable,str(ROOT/'tests/slow_inventory_launcher.py'),str(self.root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=self.env);self.procs.append(p)
        self.send(p,'hello','hello-during-hash')
        events=self.events(p,lambda e:e['kind']=='command_result',timeout=2)
        self.assertFalse(next(e for e in events if e['kind']=='snapshot')['payload']['engine_status']['models_ready'])
        p.stdin.close();self.assertEqual(p.wait(timeout=3),0)
    def test_eof_cleanup_during_blocking_model_does_not_wait_for_worker(self):
        # Production Engine/ManagedProcess; injected deterministic external model only.
        self.assertTrue((ROOT/'src/voice_memo_transcriber/service.py').exists(),'JSONL service missing')
        launcher=ROOT/'tests/lifecycle_launcher.py'
        p=subprocess.Popen([sys.executable,str(launcher),str(self.root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=self.env);self.procs.append(p)
        self.events(p,lambda e:e['kind']=='model_started')
        pid=int((self.root/'model.pid').read_text());p.stdin.close();self.assertEqual(p.wait(timeout=5),0)
        self.assertRaises(ProcessLookupError,os.kill,pid,0)
    def test_parent_loss_and_engine_sigkill_clean_model_descendants(self):
        self.assertTrue((ROOT/'src/voice_memo_transcriber/service.py').exists(),'JSONL service missing')
        p=subprocess.Popen([sys.executable,str(ROOT/'tests/lifecycle_launcher.py'),str(self.root)],stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,env=self.env);self.procs.append(p)
        self.events(p,lambda e:e['kind']=='model_started');pid=int((self.root/'model.pid').read_text());p.kill();p.wait(timeout=5)
        end=time.monotonic()+5
        while time.monotonic()<end:
            try:os.kill(pid,0)
            except ProcessLookupError:break
            time.sleep(.05)
        else:self.fail('model survived abrupt engine death')
    def test_parent_reparenting_triggers_supervisor_even_if_stdin_inherited(self):
        self.assertTrue((ROOT/'src/voice_memo_transcriber/service.py').exists(),'JSONL service missing')
        wrapper="import subprocess,sys,time,pathlib; p=subprocess.Popen([sys.executable,sys.argv[1],sys.argv[2]],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL);pathlib.Path(sys.argv[2]+'/engine.pid').write_text(str(p.pid));time.sleep(60)"
        parent=subprocess.Popen([sys.executable,'-c',wrapper,str(ROOT/'tests/lifecycle_launcher.py'),str(self.root)],env=self.env);self.procs.append(parent)
        end=time.monotonic()+5
        while not (self.root/'model.pid').exists() and time.monotonic()<end:time.sleep(.02)
        self.assertTrue((self.root/'model.pid').exists());pid=int((self.root/'model.pid').read_text());parent.kill();parent.wait(timeout=5)
        end=time.monotonic()+5
        while time.monotonic()<end:
            try:os.kill(pid,0)
            except ProcessLookupError:break
            time.sleep(.05)
        else:self.fail('model survived parent app death')

if __name__=='__main__':unittest.main()
