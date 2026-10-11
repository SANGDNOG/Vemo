import unittest,tempfile,pathlib,sys,json,wave,struct,subprocess,os
from unittest.mock import patch
from types import SimpleNamespace
sys.path.insert(0,str(pathlib.Path(__file__).resolve().parents[1]/'src'))
class WorkerTests(unittest.TestCase):
    def worker(self):
        try:from voice_memo_transcriber import worker
        except ImportError:self.fail('local inference worker missing')
        return worker
    def test_actual_ffmpeg_prepare_decodes_owned_audio_and_preserves_source(self):
        worker=self.worker();ffmpeg=pathlib.Path('/opt/homebrew/bin/ffmpeg')
        if not ffmpeg.exists():self.skipTest('root has not installed ffmpeg')
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);source=root/'source.wav'
            with wave.open(str(source),'wb') as f:f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(struct.pack('<1600h',*([100]*1600)))
            before=source.read_bytes();rev={'size':source.stat().st_size,'mtime_ns':source.stat().st_mtime_ns};dest=root/'owned.wav';pcm=root/'pcm.wav'
            result=worker.prepare({'state_root':str(root),'source':str(source),'audio_path':str(dest),'pcm_path':str(pcm),'revision_before':rev,'revision_after':rev,'ffmpeg_path':str(ffmpeg)})
            self.assertEqual(result['frames'],1600);self.assertEqual(result['duration_s'],.1);self.assertEqual(source.read_bytes(),before);self.assertEqual(dest.read_bytes(),before)
    def test_actual_native_prepare_standard_wave_and_failure_preserves_pcm(self):
        decoder=os.environ.get('VEMO_AUDIO_PATH')
        if not decoder:self.skipTest('set VEMO_AUDIO_PATH to the built native helper')
        worker=self.worker()
        with tempfile.TemporaryDirectory(prefix='Vemo actual helper spaces ') as td:
            root=pathlib.Path(td);source=root/'stereo source.wav';pcm=root/'result.wav'
            with wave.open(str(source),'wb') as f:
                f.setparams((2,2,48000,0,'NONE','not compressed'))
                f.writeframes(struct.pack('<hh',12000,6000)*144000)
            before=source.read_bytes();rev={'size':source.stat().st_size,'mtime_ns':source.stat().st_mtime_ns}
            request={'state_root':str(root),'source':str(source),'audio_path':str(root/'owned.wav'),'pcm_path':str(pcm),'revision_before':rev,'revision_after':rev,'ffmpeg_path':decoder}
            result=worker.prepare(request)
            self.assertEqual((result['frames'],result['duration_s']),(48000,3.0))
            with wave.open(str(pcm),'rb') as f:self.assertEqual((f.getnchannels(),f.getsampwidth(),f.getframerate(),f.getcomptype()),(1,2,16000,'NONE'))
            self.assertEqual(source.read_bytes(),before);good=pcm.read_bytes()
            source.write_bytes(before[:-100])
            rev={'size':source.stat().st_size,'mtime_ns':source.stat().st_mtime_ns}
            request.update(revision_before=rev,revision_after=rev)
            with self.assertRaises(subprocess.CalledProcessError):worker.prepare(request)
            self.assertEqual(pcm.read_bytes(),good)
            self.assertFalse((root/'result.partial.wav').exists())

    def test_native_prepare_uses_explicit_contract_and_atomic_cleanup(self):
        worker=self.worker()
        with tempfile.TemporaryDirectory(prefix='Vemo worker spaces ') as td:
            root=pathlib.Path(td);source=root/'source input.wav'
            with wave.open(str(source),'wb') as f:
                f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(struct.pack('<160h',*([100]*160)))
            before=source.read_bytes();rev={'size':source.stat().st_size,'mtime_ns':source.stat().st_mtime_ns}
            decoder=root/'VemoAudio'
            # Real child process checks argv and copies synthetic PCM; no model/GPU.
            decoder.write_text('#!'+sys.executable+'\nimport sys,shutil\nassert len(sys.argv)==4 and sys.argv[1]=="normalize"\nshutil.copyfile(sys.argv[2],sys.argv[3])\n')
            decoder.chmod(0o755);pcm=root/'pcm output.wav'
            request={'state_root':str(root),'source':str(source),'audio_path':str(root/'owned input.wav'),'pcm_path':str(pcm),'revision_before':rev,'revision_after':rev,'ffmpeg_path':str(decoder)}
            result=worker.prepare(request)
            self.assertEqual(result['frames'],160);self.assertEqual(result['duration_s'],.01)
            self.assertEqual(source.read_bytes(),before);self.assertEqual(pcm.read_bytes(),before)
            self.assertFalse((root/'pcm output.partial.wav').exists())
            decoder.write_text('#!'+sys.executable+'\nimport sys,pathlib\npathlib.Path(sys.argv[3]).write_bytes(b"broken")\nsys.exit(1)\n')
            with self.assertRaises(subprocess.CalledProcessError):worker.prepare(request)
            self.assertEqual(pcm.read_bytes(),before)
            self.assertFalse((root/'pcm output.partial.wav').exists())

    def test_no_network_socket_is_allowed_in_worker(self):
        worker=self.worker()
        with self.assertRaises(PermissionError):worker.check_network_event('socket.connect',(None,('example.com',443)))
        worker.check_network_event('open',('local',))
    def test_asr_uses_requested_korean_priority_and_preserves_english_text(self):
        # Actual mixed-audio regression: autodetection omitted the Korean part.
        # Mock only the heavyweight model call; real WAV/model-path handling runs.
        worker=self.worker()
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);model=root/'asr';model.mkdir()
            (model/'config.json').write_text('{}');(model/'model.safetensors').write_bytes(b'fixture')
            audio=root/'mixed.wav'
            with wave.open(str(audio),'wb') as f:
                f.setparams((1,2,16000,0,'NONE','not compressed'));f.writeframes(struct.pack('<160h',*([1]*160)))
            def inference(samples,**kwargs):
                self.assertEqual(kwargs['language'],'Korean')
                self.assertTrue(pathlib.Path(kwargs['model']).samefile(model))
                self.assertEqual(len(samples),160)
                return SimpleNamespace(text='한국어 프로젝트. The API starts on Monday.',language='Korean',truncated=False)
            with patch('mlx_qwen3_asr.transcribe',side_effect=inference):
                result=worker.asr({'audio_path':str(audio),'model_root':str(root)})
            self.assertEqual(result,{'text':'한국어 프로젝트. The API starts on Monday.','language':'Korean','truncated':False})
    def test_title_unit_checkpoint_consumes_late_chunks_before_final(self):
        worker=self.worker();self.assertTrue(hasattr(worker,'title_unit'),'title chunk checkpoint missing')
        budgets=[]
        def hint(chunk,budget):budgets.append(budget);return str(chunk[-1])
        def final(chunk,budget):budgets.append(budget);self.assertIn(6999,chunk);return '완성 제목'
        def encode(text):return [int(t) for t in text.split()]
        state={'round':0,'chunks':[list(range(3072)),list(range(3072,6144)),list(range(6144,7000))],'hints':[],'index':0}
        one=worker.title_unit(state,hint,final,encode)
        self.assertEqual(one['title_checkpoint']['index'],1);self.assertNotIn('topic',one)
        two=worker.title_unit(one['title_checkpoint'],hint,final,encode)
        three=worker.title_unit(two['title_checkpoint'],hint,final,encode)
        self.assertEqual(three['title_checkpoint']['round'],1)
        result=worker.title_unit(three['title_checkpoint'],hint,final,encode)
        self.assertEqual(result['topic'],'완성 제목');self.assertEqual(budgets,[96,96,96,64])
    def test_worker_unknown_operation_nonzero_without_leaking_content(self):
        self.worker()
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);req=root/'request.json';req.write_text(json.dumps({'op':'unknown','recording_content':'SECRET_RECORDING'}));out=root/'out'
            r=subprocess.run([sys.executable,'-m','voice_memo_transcriber.worker',str(req),str(out)],env={**os.environ,'PYTHONPATH':str(pathlib.Path(__file__).resolve().parents[1]/'src')},capture_output=True)
            self.assertNotEqual(r.returncode,0);self.assertFalse(out.exists());self.assertNotIn(b'SECRET_RECORDING',r.stdout+r.stderr)

if __name__=='__main__':unittest.main()
