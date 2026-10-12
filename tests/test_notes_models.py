"""Optional notes model must not gate transcription or replace unrelated models."""
import contextlib
import hashlib
import io
import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from voice_memo_transcriber import models, download_models


class NotesModelTests(unittest.TestCase):
    def fixture(self, root, names):
        entries = {}
        for name in names:
            directory = root / name
            directory.mkdir(parents=True)
            files = []
            for filename, data in [('config.json', b'{}'), ('tokenizer_config.json', b'{}'), ('model.safetensors', b'abc')]:
                (directory / filename).write_bytes(data)
                files.append({'path': filename, 'size': len(data), 'sha256': hashlib.sha256(data).hexdigest()})
            entries[name] = {**models.ALL_PINS[name], 'files': files}
        models.write_manifest(root, entries)
        return entries

    def test_missing_optional_notes_keeps_required_models_ready(self):
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            self.fixture(root, ('asr', 'aligner', 'title'))
            status = models.model_status(root)
            self.assertTrue(status['models_ready'])
            self.assertIs(status.get('notes_ready'), False)

    def test_corrupt_notes_does_not_invalidate_required_models(self):
        self.assertIn('notes', models.OPTIONAL_PINS, 'optional notes model pin is not implemented')
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            self.fixture(root, ('asr', 'aligner', 'title', 'notes'))
            self.assertTrue(models.model_status(root)['notes_ready'])
            (root / 'notes' / 'model.safetensors').write_bytes(b'xyz')
            status = models.model_status(root)
            self.assertTrue(status['models_ready'])
            self.assertFalse(status['notes_ready'])

    def test_notes_only_download_preserves_required_manifest_and_files(self):
        self.assertIn('notes', models.OPTIONAL_PINS, 'optional notes model pin is not implemented')
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            before = self.fixture(root, ('asr', 'aligner', 'title'))
            before['future-entry'] = {'opaque': 'preserve'}
            models.write_manifest(root, before)
            file_bytes = {str(p.relative_to(root)): p.read_bytes() for p in root.glob('*/*')}
            payloads = {'config.json': b'{}', 'tokenizer_config.json': b'{}', 'model.safetensors': b'notes'}
            pin = models.OPTIONAL_PINS['notes']
            def info(repo_id, revision, files_metadata):
                self.assertEqual((repo_id, revision), (pin['repo_id'], pin['revision']))
                return types.SimpleNamespace(sha=revision, siblings=[types.SimpleNamespace(rfilename=name, size=len(data), lfs={'sha256': hashlib.sha256(data).hexdigest()}) for name, data in payloads.items()])
            def download(repo_id, revision, local_dir):
                self.assertEqual((repo_id, revision), (pin['repo_id'], pin['revision']))
                for name, data in payloads.items():
                    (pathlib.Path(local_dir) / name).write_bytes(data)
            hub = types.ModuleType('huggingface_hub')
            hub.HfApi = lambda: types.SimpleNamespace(model_info=info)
            hub.snapshot_download = download
            with patch.dict(sys.modules, {'huggingface_hub': hub}), patch.object(sys, 'argv', ['download_models', '--model-root', td, '--notes-only']), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(download_models.main(), 0)
            after = json.loads((root / 'models.json').read_text())['models']
            for key, value in before.items():
                self.assertEqual(after[key], value)
            for filename, data in file_bytes.items():
                self.assertEqual((root / filename).read_bytes(), data)
            self.assertTrue(models.model_status(root)['notes_ready'])
