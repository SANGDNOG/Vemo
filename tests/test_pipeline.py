import unittest, tempfile, pathlib, sys, wave, struct, json, os, subprocess, time, importlib.util
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
class PipelineTests(unittest.TestCase):
    def modules(self):
        try:
            from voice_memo_transcriber import media, models, process
            return media,models,process
        except ImportError: self.fail('media/model/process pipeline not implemented')
    def test_pcm_identity_ignores_metadata_detects_audio_edit(self):
        m,_,_=self.modules()
        with tempfile.TemporaryDirectory() as td:
            def wav(name,values,metadata=False):
                p=pathlib.Path(td)/name
                with wave.open(str(p),'wb') as f:
                    f.setparams((1,2,16000,0,'NONE','not compressed')); f.writeframes(struct.pack('<'+str(len(values))+'h',*values))
                if metadata:
                    with open(p,'ab') as f:f.write(b'JUNK\x04\x00\x00\x00abcd')
                return p
            a=wav('a.wav',[1,2,3]); b=wav('b.wav',[1,2,3],True); c=wav('c.wav',[1,2,4])
            self.assertEqual(m.pcm_identity(a)[0],m.pcm_identity(b)[0]); self.assertNotEqual(m.pcm_identity(a)[0],m.pcm_identity(c)[0])
    def test_chunks_cover_all_samples_with_bounded_global_offsets(self):
        m,_,_=self.modules()
        self.assertEqual(m.chunk_ranges(16000*601,16000,300),[(0,4800000),(4800000,9600000),(9600000,9616000)])
        got=m.global_segments([{'start_s':0.1,'end_s':0.8,'text':'끝'}],600,1)
        self.assertEqual(got,[{'start_s':600.1,'end_s':600.8,'text':'끝'}])
        self.assertRaises(ValueError,m.global_segments,[{'start_s':-1,'end_s':5,'text':'x'}],600,1)
    def test_title_late_chunk_participates_and_output_budgets(self):
        from voice_memo_transcriber.titles import hierarchical_topic
        tokens=list(range(7000)); seen=[]
        def hint(chunk,budget):
            self.assertEqual(budget,96); seen.extend(chunk); return str(chunk[-1])
        def encode(text):return [int(v) for v in text.split()]
        def final(chunk,budget):
            self.assertEqual(budget,64); self.assertIn(6999,chunk); return 'Late topic'
        self.assertEqual(hierarchical_topic(tokens,hint,final,encode),'Late topic');self.assertEqual(seen,tokens)
    def test_owned_paths_reject_symlinks_and_outside(self):
        m,_,_=self.modules()
        with tempfile.TemporaryDirectory() as td, tempfile.TemporaryDirectory() as other:
            root=pathlib.Path(td); outside=pathlib.Path(other)/'audio';outside.write_bytes(b'x')
            self.assertRaises(ValueError,m.validate_owned,root,outside)
            (root/'link').symlink_to(outside)
            self.assertRaises(ValueError,m.validate_owned,root,root/'link')
    def test_model_readiness_requires_pins_and_file_integrity(self):
        _,models,_=self.modules()
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td)
            self.assertFalse(models.model_status(root)['models_ready'])
            models.write_manifest(root,{})
            self.assertFalse(models.model_status(root)['models_ready'])
    def test_model_corruption_after_verification_invalidates_readiness(self):
        _,models,_=self.modules()
        import hashlib
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);entries={}
            for name,pin in models.PINS.items():
                directory=root/name;directory.mkdir();files=[]
                for filename,data in [('config.json',b'{}'),('tokenizer_config.json',b'{}'),('model.safetensors',b'abc')]:
                    (directory/filename).write_bytes(data);files.append({'path':filename,'size':len(data),'sha256':hashlib.sha256(data).hexdigest()})
                entries[name]={**pin,'files':files}
            models.write_manifest(root,entries)
            self.assertTrue(models.model_status(root)['models_ready'])
            (root/'asr'/'model.safetensors').write_bytes(b'xyz')
            self.assertFalse(models.model_status(root)['models_ready'])
    def test_process_timeout_terminates_group(self):
        _,_,p=self.modules()
        with tempfile.TemporaryDirectory() as td:
            pidfile=pathlib.Path(td)/'pid'
            task=p.ManagedProcess([sys.executable,'-c','import os,time;open('+repr(str(pidfile))+',"w").write(str(os.getpid()));time.sleep(60)'],timeout=.4)
            deadline=time.monotonic()+5
            while task.poll() is None and time.monotonic()<deadline:time.sleep(.02)
            self.assertIsNotNone(task.poll());self.assertTrue(task.timed_out)
            if pidfile.exists():self.assertRaises(ProcessLookupError,os.kill,int(pidfile.read_text()),0)
    def test_guardian_cleans_term_ignoring_grandchild_after_command_exit(self):
        _,_,process=self.modules()
        with tempfile.TemporaryDirectory() as td:
            pidfile=pathlib.Path(td)/'grandchild.pid'
            child_code='import signal,time,os;signal.signal(signal.SIGTERM,signal.SIG_IGN);open('+repr(str(pidfile))+',"w").write(str(os.getpid()));time.sleep(60)'
            parent_code='import subprocess,sys,pathlib,time;p=subprocess.Popen([sys.executable,"-c",'+repr(child_code)+']);f=pathlib.Path('+repr(str(pidfile))+');\nwhile not f.exists():time.sleep(.01)'
            task=process.ManagedProcess([sys.executable,'-c',parent_code],timeout=5)
            deadline=time.monotonic()+6
            while task.poll() is None and time.monotonic()<deadline:time.sleep(.02)
            self.assertTrue(pidfile.exists());pid=int(pidfile.read_text())
            try:
                deadline=time.monotonic()+2
                while time.monotonic()<deadline:
                    try:os.kill(pid,0)
                    except ProcessLookupError:break
                    time.sleep(.02)
                else:self.fail('TERM-ignoring grandchild survived guardian completion')
            finally:
                try:os.kill(pid,9)
                except ProcessLookupError:pass

class BootstrapTests(unittest.TestCase):
    def test_downloader_pins_exact_sha_and_reports_observed_bytes(self):
        path=pathlib.Path(__file__).resolve().parents[1]/'scripts/download-models.py'
        self.assertTrue(path.exists(),'download entrypoint is missing')
        spec=importlib.util.spec_from_file_location('download_models',path); module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        self.assertEqual(module.PINS['asr']['revision'],'7278e1e70fe206f11671096ffdd38061171dd6e5')
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td); (root/'x').write_bytes(b'abc'); (root/'.cache').mkdir();(root/'.cache'/'partial.incomplete').write_bytes(b'ab')
            self.assertEqual(module.observed_bytes(root),5)
    def test_download_verification_rejects_same_size_wrong_upstream_lfs_hash(self):
        from types import SimpleNamespace
        path=pathlib.Path(__file__).resolve().parents[1]/'scripts/download-models.py'
        spec=importlib.util.spec_from_file_location('download_integrity',path);module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)
        self.assertTrue(hasattr(module,'verified_files'),'official LFS verification is absent')
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);(root/'weights.safetensors').write_bytes(b'abc')
            sibling=SimpleNamespace(rfilename='weights.safetensors',size=3,lfs=SimpleNamespace(sha256='0'*64))
            self.assertRaises(ValueError,module.verified_files,root,[sibling])
            sibling.lfs.sha256='ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
            files=module.verified_files(root,[sibling]);self.assertEqual(files[0]['sha256'],sibling.lfs.sha256)

if __name__=='__main__':unittest.main()
