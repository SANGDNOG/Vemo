import hashlib
import json
import pathlib
import tempfile
import unittest
from unittest.mock import patch

from voice_memo_transcriber import models, worker
from voice_memo_transcriber.model_catalog import DEFAULT_SELECTION, PROFILES, device_capabilities, validate_selection, recommendations
import test_engine
from test_engine import memo


class CatalogTests(unittest.TestCase):
    def test_small_notes_failed_quality_is_not_recommended(self):
        mac = device_capabilities(system='Darwin',machine='arm64',ram_bytes=16*1024**3)
        self.assertIsNone(recommendations(mac)['notes'])
        self.assertIn('failed',PROFILES['notes-qwen3-4b']['quality_status'])
        with self.assertRaisesRegex(ValueError,'model_profile_incompatible'):
            validate_selection({**DEFAULT_SELECTION,'notes':'notes-qwen3-4b'},mac)

    def test_profile_validation_and_device_support(self):
        selection = {'asr': 'asr-qwen3-0.6b', 'notes': DEFAULT_SELECTION['notes']}
        mac = device_capabilities(system='Darwin', machine='arm64', ram_bytes=16*1024**3)
        self.assertEqual(validate_selection(selection, mac), selection)
        for value, error in [({'asr': 'notes-qwen3-4b', 'notes': selection['notes']}, 'wrong_model_role'),
                             ({'asr': 'invented', 'notes': selection['notes']}, 'unknown_model_profile'),
                             ({'asr': selection['asr']}, 'invalid_model_selection')]:
            with self.assertRaisesRegex(ValueError, error): validate_selection(value, mac)
        with self.assertRaisesRegex(ValueError, 'unsupported_model_backend'):
            validate_selection(selection, device_capabilities(system='Windows', machine='AMD64', ram_bytes=64*1024**3))

    def test_legacy_install_and_selected_missing_profile(self):
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            entries = {}
            for name, pin in models.ALL_PINS.items():
                path = root/name; path.mkdir()
                files = []
                for filename, data in [('config.json', b'{}'), ('tokenizer_config.json', b'{}'), ('model.safetensors', b'abc')]:
                    (path/filename).write_bytes(data)
                    files.append({'path': filename, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
                entries[name] = {**pin, 'files': files}
            models.write_manifest(root, entries)
            self.assertTrue(models.model_status(root)['models_ready'])
            self.assertTrue(models.model_status(root)['notes_ready'])
            selection = {'asr': 'asr-qwen3-0.6b', 'notes': 'notes-qwen3-4b'}
            status = models.model_status(root, selection=selection)
            self.assertFalse(status['models_ready'])
            self.assertTrue(status['notes_ready'])
            self.assertEqual(worker.local_model({'model_root': td, 'model_selection': selection}, 'notes'), str(root.resolve()/'title'))
            with self.assertRaisesRegex(ValueError, 'local_model_missing'):
                worker.local_model({'model_root': td, 'model_selection': selection}, 'asr')

    def test_refresh_reuses_verified_hashes(self):
        from test_notes_models import NotesModelTests
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            NotesModelTests().fixture(root, models.ALL_PINS)
            models.model_status(root)
            with patch('voice_memo_transcriber.models.sha256', side_effect=AssertionError('unnecessary rehash')):
                models.model_status(root, selection={'asr': DEFAULT_SELECTION['asr'], 'notes': 'notes-qwen3-4b'})


class SelectionEngineTests(unittest.TestCase):
    setUp = test_engine.EngineTests.setUp
    tearDown = test_engine.EngineTests.tearDown
    engine = test_engine.EngineTests.engine
    send = test_engine.EngineTests.send
    job = test_engine.EngineTests.job
    def test_set_models_atomic_persist_and_completed_untouched(self):
        self.engine()
        self.send('import', {'memo': memo('existing')})
        job = self.job('existing'); job.update(status='completed', queued=False, phase='publish', notes_path='prior')
        with self.e.store.transaction(): self.e.store.put(job)
        before = self.e.store.jobs()
        selected = {'asr': 'asr-qwen3-0.6b', 'notes': DEFAULT_SELECTION['notes']}
        self.assertTrue(self.send('set_models', selected)['ok'])
        self.assertEqual(self.e.store.jobs(), before)
        self.assertFalse(self.send('set_models', {'asr': 'wrong', 'notes': selected['notes']})['ok'])
        self.assertEqual(self.e.engine_status()['model_selection']['asr'], selected['asr'])
        self.e.close(); self.engine()
        self.assertEqual(self.e.engine_status()['model_selection']['notes'], selected['notes'])
        self.assertEqual(self.e.store.jobs(), before)

    def test_mid_pipeline_selection_disabled_even_between_workers(self):
        self.engine(); self.send('import', {'memo': memo('checkpoint')})
        job = self.job('checkpoint'); job.update(phase='asr', status='paused', asr_chunks=[{'text':'old'}])
        with self.e.store.transaction(): self.e.store.put(job)
        selected = {'asr':'asr-qwen3-0.6b', 'notes':DEFAULT_SELECTION['notes']}
        result = self.send('set_models', selected)
        self.assertEqual(result['error']['code'], 'models_busy')
        self.assertEqual(self.e.engine_status()['model_selection']['disabled_reason'], 'processing')
        self.e.close(); self.engine()
        self.assertEqual(self.e.store.get(job['id'])['model_selection'], DEFAULT_SELECTION)
        self.assertEqual(self.send('set_models', selected)['error']['code'], 'models_busy')

    def test_download_freezes_selected_profiles_and_blocks_changes(self):
        self.engine()
        selected = {'asr':'asr-qwen3-0.6b', 'notes':DEFAULT_SELECTION['notes']}
        self.assertTrue(self.send('set_models', selected)['ok'])
        self.assertTrue(self.send('download_models', {'notes_only':True})['ok'])
        self.assertEqual(self.e.store.get_meta('download_model_selection'), selected)
        self.assertEqual(self.send('set_models', DEFAULT_SELECTION)['error']['code'], 'models_busy')
        self.assertEqual(self.e.engine_status()['model_selection']['disabled_reason'], 'download_active')

    def test_new_jobs_and_explicit_notes_regeneration_use_selection(self):
        self.engine()
        selected = {'asr':'asr-qwen3-0.6b', 'notes':DEFAULT_SELECTION['notes']}
        self.assertTrue(self.send('set_models', selected)['ok'])
        self.send('import', {'memo':memo('new-model-job')})
        self.e.tick()
        test_engine.EngineTests.snapshot(self, self.job('new-model-job'))
        test_engine.EngineTests.drain(self, lambda:self.job('new-model-job')['status']=='saving')
        job = self.job('new-model-job')
        self.assertEqual(job['model_selection'], selected)
        requests = [json.loads(p.read_text()) for p in (self.root/'work'/job['id']/'tasks').glob('*/request.json')]
        self.assertTrue(requests)
        self.assertTrue(all(r['model_selection']==selected for r in requests))
        self.assertTrue(self.send('set_models', DEFAULT_SELECTION)['ok'])
        self.assertEqual(self.job('new-model-job')['model_selection'], selected)
        self.assertTrue(self.send('generate_notes', {'job_id':job['id']})['ok'])
        self.assertEqual(self.job('new-model-job')['model_selection'], {**selected,'notes':DEFAULT_SELECTION['notes']})

    def test_purged_minimal_tombstone_survives_model_migration(self):
        self.engine()
        tombstone = {'id':'purged','uuid':'purged','deleted_at':'2026-10-17','purged_at':'2026-10-17','queued':False}
        with self.e.store.transaction(): self.e.store.put(tombstone)
        self.e.close(); self.engine()
        self.assertEqual(self.e.store.get('purged'),tombstone)
        self.assertTrue(self.e.engine_status()['model_selection']['can_change'])

    def test_job_readiness_uses_frozen_profiles_after_selection_change(self):
        self.engine()
        selected = {'asr':'asr-qwen3-0.6b','notes':DEFAULT_SELECTION['notes']}
        self.assertTrue(self.send('set_models', selected)['ok'])
        self.e.models = {'models_ready':False,'notes_ready':True,
                         'model_status':{'aligner':{'state':'ready'},'title':{'state':'ready'}},
                         'profile_status':{'asr-qwen3-1.7b':{'state':'ready'},'asr-qwen3-0.6b':{'state':'missing_or_invalid'},
                                           'notes-qwen3.5-27b':{'state':'ready'},'notes-qwen3-4b':{'state':'ready'}}}
        old_job = {'model_selection':dict(DEFAULT_SELECTION)}
        self.assertTrue(self.e.job_model_status(old_job)['models_ready'])
        self.assertFalse(self.e.job_model_status({})['models_ready'])
        self.assertTrue(self.e.job_model_status(old_job)['notes_ready'])
        self.send('import', {'memo':memo('old-title-job')})
        job = self.job('old-title-job')
        artifact = self.root/'old-transcript.json'
        artifact.write_text(json.dumps({'text':'synthetic transcript'}))
        job.update(phase='publish',status='completed',queued=False,
                   artifact_path=str(artifact),topic='old',notes_status='completed')
        with self.e.store.transaction(): self.e.store.put(job)
        self.assertTrue(self.send('retitle', {'job_id':job['id'],'generate':True})['ok'])
        self.e.last_models = __import__('time').monotonic()
        self.e.tick()
        self.assertIsNotNone(self.e.active)
        self.assertEqual(self.e.active['op'],'title')


class SelectedDownloadTests(unittest.TestCase):
    def test_alternative_download_keeps_original_models_and_skips_ready_notes(self):
        import contextlib
        import io
        import sys
        import types
        from voice_memo_transcriber import download_models
        from test_notes_models import NotesModelTests
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            before = NotesModelTests().fixture(root, models.ALL_PINS)
            before_bytes = {str(p.relative_to(root)):p.read_bytes() for p in root.glob('*/*')}
            selected = PROFILES['asr-qwen3-0.6b']
            payloads = {'config.json':b'{}', 'tokenizer_config.json':b'{}', 'model.safetensors':b'small-asr'}
            calls = []
            def info(repo_id, revision, files_metadata):
                self.assertEqual((repo_id,revision), (selected['repo_id'],selected['revision']))
                return types.SimpleNamespace(sha=revision,siblings=[types.SimpleNamespace(rfilename=n,size=len(d),lfs={'sha256':hashlib.sha256(d).hexdigest()}) for n,d in payloads.items()])
            def download(repo_id, revision, local_dir):
                calls.append(repo_id)
                for name,data in payloads.items(): (pathlib.Path(local_dir)/name).write_bytes(data)
            hub = types.ModuleType('huggingface_hub')
            hub.HfApi = lambda:types.SimpleNamespace(model_info=info)
            hub.snapshot_download = download
            args = ['download_models','--model-root',td,'--asr-profile','asr-qwen3-0.6b','--notes-profile',DEFAULT_SELECTION['notes']]
            with patch.dict(sys.modules, {'huggingface_hub':hub}),patch.object(sys,'argv',args),contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(download_models.main(),0)
            self.assertEqual(calls,[selected['repo_id']])
            after = json.loads((root/'models.json').read_text())['models']
            self.assertTrue(all(after[k]==v for k,v in before.items()))
            self.assertTrue(all((root/n).read_bytes()==data for n,data in before_bytes.items()))
            selection = {'asr':'asr-qwen3-0.6b','notes':'notes-qwen3-4b'}
            self.assertTrue(models.model_status(root,selection=selection)['models_ready'])
            self.assertTrue(models.model_status(root,selection=selection)['notes_ready'])
            self.assertEqual(worker.local_model({'model_root':td,'model_selection':selection},'asr'),str(root.resolve()/'asr-qwen3-0.6b'))
            with patch.dict(sys.modules, {'huggingface_hub':hub}),patch.object(sys,'argv',args+['--notes-only']),contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(download_models.main(),0)
            self.assertEqual(len(calls),1)
