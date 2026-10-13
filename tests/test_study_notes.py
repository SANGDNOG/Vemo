"""Exercise study decoding and streaming boundaries without importing a GPU runtime."""
import hashlib
import json
import pathlib
import os
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from voice_memo_transcriber import study_notes as study, worker


SOURCE = '  첫 조건입니다.\nEnglish term\t끝의 정정입니다.  '


def response():
    return {'summary': [{'text': '핵심 결론', 'refs': [1]}],
            'topics': [{'keyword': '조건', 'summary': '조건과 예제를 자세히 설명합니다.\n정정도 보존합니다.', 'refs': [1]}]}


class StudyNotesTests(unittest.TestCase):
    def test_source_map_preserves_every_character_and_exact_identity(self):
        source = (SOURCE * 70) + '\n최종 조건은 22일입니다.\t '
        passages, messages = study.messages_for_source(source)
        mapping = json.loads(messages[1]['content'])
        self.assertEqual(''.join(mapping.values()), source)
        self.assertEqual(list(mapping), [str(i + 1) for i in range(len(mapping))])
        self.assertTrue(all(0 < len(p) <= 400 for p in passages))
        value = response(); value['topics'][0]['refs'] = [len(passages), 1]
        result = study.decode(json.dumps(value), passages, source)
        self.assertEqual(result['source_sha256'], hashlib.sha256(source.encode()).hexdigest())
        self.assertEqual(result['topics'][0]['quotes'], [passages[-1], passages[0]])
        self.assertEqual(result['topics'][0]['summary'], value['topics'][0]['summary'])

    def test_invalid_refs_reject_whole_response_not_partial_success(self):
        for refs in [[], [0], [-1], [2], [True], [1.0], ['1'], [1, 1], [1] * 65, '1']:
            value = response()
            value['topics'].append({'keyword': 'invalid', 'summary': 'must not publish', 'refs': refs})
            with self.subTest(refs=refs), self.assertRaises(ValueError):
                study.decode(json.dumps(value), [SOURCE], SOURCE)

    def test_strict_json_rejects_duplicate_keys_and_tail_repair(self):
        valid = json.dumps(response())
        duplicate = valid.replace('"refs": [1]', '"refs": [2], "refs": [1]', 1)
        for raw in [duplicate, valid + '}', valid[:-1], '```json\n' + valid + '\n```', valid + '{}']:
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                study.decode(raw, [SOURCE], SOURCE)

    def test_schema_bounds_and_unknown_fields_fail_atomically(self):
        invalid = []
        for key, count in [('summary', 17), ('topics', 17)]:
            value = response(); value[key] *= count; invalid.append(value)
        for group, field, length in [('summary', 'text', 401), ('topics', 'keyword', 81), ('topics', 'summary', 1001)]:
            value = response(); value[group][0][field] = 'x' * length; invalid.append(value)
        value = response(); value['topics'][0]['quotes'] = ['fabricated']; invalid.append(value)
        value = response(); value['summary'][0]['text'] = value['topics'][0]['summary']; invalid.append(value)
        for value in invalid:
            with self.subTest(value=value), self.assertRaises(ValueError):
                study.decode(json.dumps(value), [SOURCE], SOURCE)

    def test_detailed_schema_keeps_sixteen_topics_and_all_sixty_four_refs(self):
        passages = [f'{i:02d}번 원문의 독립 조건입니다.\n' for i in range(64)]
        source = ''.join(passages)
        value = response()
        value['summary'][0]['refs'] = list(range(1, 65))
        value['topics'] = [{'keyword': f'주제{i}', 'summary': f'조건{i}을 설명합니다.', 'refs': [i + 1]} for i in range(16)]
        result = study.decode(json.dumps(value), passages, source)
        self.assertEqual(len(result['topics']), 16)
        self.assertEqual(result['summary'][0]['quotes'], passages)
        self.assertEqual([item['summary'] for item in result['topics']], [item['summary'] for item in value['topics']])

    def runtime(self, prompt_tokens=100, finish='stop', generated=50, text=None, source=SOURCE):
        calls = []; seen = []; raw = json.dumps(response()) if text is None else text
        class Tokenizer:
            def apply_chat_template(self, messages, **kwargs):
                seen.append(messages)
                if kwargs.get('enable_thinking') is not False:
                    raise AssertionError('thinking must be explicitly disabled')
                return 'rendered full prompt'
            def encode(self, value, **kwargs):
                return [0] * (prompt_tokens if value == 'rendered full prompt' else len(value))
        mx = types.ModuleType('mlx.core'); mx.set_memory_limit = lambda n: None; mx.set_cache_limit = lambda n: None
        mlx = types.ModuleType('mlx'); mlx.core = mx
        lm = types.ModuleType('mlx_lm'); lm.load = lambda *args, **kwargs: (object(), Tokenizer())
        def stream(*args, **kwargs):
            calls.append(kwargs)
            yield types.SimpleNamespace(text=raw, finish_reason=finish, generation_tokens=generated)
        lm.stream_generate = stream
        sampling = types.ModuleType('mlx_lm.sample_utils'); sampling.make_sampler = lambda **kwargs: object()
        modules = {'mlx': mlx, 'mlx.core': mx, 'mlx_lm': lm, 'mlx_lm.sample_utils': sampling}
        with patch.dict(sys.modules, modules):
            try:
                result = study.run_study_notes(source, '/synthetic/notes-model')
            except study.StudyNotesError as error:
                return error.code, calls, seen
        return result, calls, seen

    def test_rendered_prompt_boundary_no_truncation_or_generation_when_oversize(self):
        result, calls, seen = self.runtime(prompt_tokens=16384)
        self.assertIsInstance(result, dict)
        self.assertEqual(''.join(json.loads(seen[0][1]['content']).values()), SOURCE)
        self.assertEqual(len(calls), 1)
        code, calls, _ = self.runtime(prompt_tokens=16385)
        self.assertEqual(code, 'notes_input_too_long'); self.assertEqual(calls, [])

    def test_complete_looking_json_at_generation_cap_is_rejected(self):
        for finish, count in [('length', 50), ('stop', 8192), ('stop', 9000), (None, 50)]:
            with self.subTest(finish=finish, count=count):
                code, _, _ = self.runtime(finish=finish, generated=count)
                self.assertEqual(code, 'notes_failed' if finish is None else 'notes_output_truncated')
        code, _, _ = self.runtime(text='{"summary":')
        self.assertEqual(code, 'notes_failed')

    def test_worker_routes_notes_model_and_uses_indeterminate_progress(self):
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td); model = root / 'models' / 'notes'; model.mkdir(parents=True)
            (model / 'config.json').write_text('{}'); (model / 'model.safetensors').write_bytes(b'fake')
            artifact = root / 'transcript.json'; artifact.write_text(json.dumps({'text': SOURCE}))
            before = artifact.read_bytes()
            request = {'state_root': td, 'model_root': str(root / 'models'), 'artifact_path': str(artifact)}
            def run(source, path):
                self.assertEqual(source, SOURCE); self.assertEqual(pathlib.Path(path).resolve(), model.resolve())
                return study.decode(json.dumps(response()), [SOURCE], SOURCE)
            with patch.object(study, 'run_study_notes', side_effect=run):
                result = worker.notes(request)
            self.assertEqual(result['notes_protocol'], 'study_v1')
            self.assertIsNone(result['completed_units']); self.assertIsNone(result['total_units'])
            self.assertEqual(artifact.read_bytes(), before)
            with self.assertRaisesRegex(ValueError, 'study_checkpoint_not_supported'):
                worker.notes({**request, 'notes_protocol': 'study_v1', 'notes_checkpoint': {'index': 1}})

    def test_worker_missing_optional_model_has_typed_error(self):
        with tempfile.TemporaryDirectory() as td:
            artifact = pathlib.Path(td) / 'transcript.json'; artifact.write_text(json.dumps({'text': SOURCE}))
            with self.assertRaises(study.StudyNotesError) as caught:
                worker.notes({'state_root': td, 'model_root': str(pathlib.Path(td) / 'models'), 'artifact_path': str(artifact)})
            self.assertEqual(caught.exception.code, 'notes_model_missing')

    def test_worker_process_writes_typed_missing_model_response_without_traceback(self):
        with tempfile.TemporaryDirectory() as td:
            root = pathlib.Path(td)
            artifact = root / 'transcript.json'; artifact.write_text(json.dumps({'text': SOURCE}))
            request = root / 'request.json'; output = root / 'result.json'
            request.write_text(json.dumps({'op': 'notes', 'notes_protocol': 'study_v1', 'state_root': td,
                                          'model_root': str(root / 'models'), 'artifact_path': str(artifact)}))
            env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1', PYTHONPATH=str(pathlib.Path(__file__).resolve().parents[1] / 'src'))
            process = subprocess.run([sys.executable, '-m', 'voice_memo_transcriber.worker', str(request), str(output)],
                                     env=env, capture_output=True, text=True, timeout=10)
            self.assertEqual(process.returncode, 0, process.stderr)
            self.assertEqual(json.loads(output.read_text()), {'notes_protocol': 'study_v1',
                'notes_error_code': 'notes_model_missing', 'completed_units': None, 'total_units': None})
            self.assertNotIn('Traceback', process.stderr)
