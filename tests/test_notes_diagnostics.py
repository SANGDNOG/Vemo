"""Synthetic failure diagnostics: no GPU, private source, or external services."""
import json
import pathlib
import sys
import tempfile
import types
import unittest
from unittest.mock import patch
from voice_memo_transcriber import study_notes as study, worker
from voice_memo_transcriber.engine import Engine

SOURCE = 'Synthetic condition.'
SECRET = 'PRIVATE_RESPONSE_MUST_NOT_LEAK'

class NotesDiagnosticsTests(unittest.TestCase):
    def run_failure(self, raw='{', finish='stop', count=23, fail_at=None):
        class Tokenizer:
            def apply_chat_template(self, *args, **kwargs): return 'prompt'
            def encode(self, value, **kwargs): return [0] * 7
        mx=types.ModuleType('mlx.core');mx.set_memory_limit=lambda n:None;mx.set_cache_limit=lambda n:None
        mlx=types.ModuleType('mlx');mlx.core=mx
        lm=types.ModuleType('mlx_lm')
        def load(*args, **kwargs):
            if fail_at == 'load': raise RuntimeError(SECRET)
            return object(),Tokenizer()
        def stream(*args, **kwargs):
            yield types.SimpleNamespace(text=raw,finish_reason=finish,generation_tokens=count)
            if fail_at == 'stream': raise RuntimeError(SECRET)
        lm.load=load;lm.stream_generate=stream
        sample=types.ModuleType('mlx_lm.sample_utils');sample.make_sampler=lambda **kwargs:None
        with patch.dict(sys.modules,{'mlx':mlx,'mlx.core':mx,'mlx_lm':lm,'mlx_lm.sample_utils':sample}):
            with self.assertRaises(study.StudyNotesError) as caught:
                study.run_study_notes(SOURCE,'/synthetic/model')
        self.assertNotIn(SECRET,str(caught.exception))
        return caught.exception

    def test_decode_and_finish_subtypes_remain_distinct(self):
        valid={'summary':[{'text':'Core','refs':[1]}],'topics':[{'keyword':'Topic','summary':'Detail','refs':[1]}]}
        refs=json.loads(json.dumps(valid));refs['topics'][0]['refs']=[2]
        long=json.loads(json.dumps(valid));long['topics'][0]['summary']='x'*1001
        for raw,finish,reason,stage in [('{','stop','malformed_json','decode'),
                (json.dumps(refs),'stop','invalid_canonical_integer_refs','decode'),
                (json.dumps(long),'stop','invalid_text_bounds','decode'),
                (json.dumps(valid),SECRET,'unexpected_finish','finish')]:
            with self.subTest(reason=reason):
                error=self.run_failure(raw,finish)
                self.assertEqual(error.code,'notes_failed')
                self.assertEqual(error.diagnostics['reason'],reason)
                self.assertEqual(error.diagnostics['stage'],stage)
                self.assertEqual(error.diagnostics['generation_tokens'],23)
                self.assertEqual(error.diagnostics['prompt_tokens'],7)
                self.assertNotIn(SECRET,json.dumps(error.diagnostics))

    def test_runtime_stage_and_partial_generation_are_observable(self):
        for where,stage in [('load','model_load'),('stream','generation')]:
            with self.subTest(where=where):
                error=self.run_failure(fail_at=where)
                self.assertEqual(error.diagnostics['stage'],stage)
                self.assertEqual(error.diagnostics['reason'],'runtime_error')
                if where=='stream':self.assertEqual(error.diagnostics['generation_tokens'],23)

    def test_truncation_keeps_existing_code_and_numeric_limits(self):
        error=self.run_failure(finish='length',count=study.OUTPUT_LIMIT)
        self.assertEqual(error.code,'notes_output_truncated')
        self.assertEqual(error.diagnostics['reason'],'output_limit')
        self.assertEqual(error.diagnostics['finish_reason'],'length')

    def test_diagnostics_allowlist_rejects_private_and_unsafe_values(self):
        safe=study.safe_diagnostics({'stage':SECRET,'reason':SECRET,'finish_reason':SECRET,
            'generation_tokens':True,'prompt_tokens':-1,'response':SECRET,'exception':SECRET})
        self.assertEqual(safe,{'stage':'unknown','reason':'unknown'})
        self.assertEqual(study.safe_diagnostics({'generation_tokens':10**100}),{'stage':'unknown','reason':'unknown'})

    def test_worker_and_engine_keep_safe_metadata_and_previous_notes(self):
        error=self.run_failure()
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);request=root/'request.json';output=root/'output.json';artifact=root/'transcript.json'
            artifact.write_text(json.dumps({'text':SOURCE}))
            request.write_text(json.dumps({'op':'notes','notes_protocol':'study_v1'}))
            with patch.object(sys,'argv',['worker',str(request),str(output)]),patch.object(sys,'addaudithook'),patch.object(worker,'notes',side_effect=error):
                self.assertEqual(worker.main(),0)
            result=json.loads(output.read_text())
            self.assertEqual(result['notes_diagnostics'],error.diagnostics)
            result['notes_diagnostics']['response']=SECRET
            engine=Engine.__new__(Engine);engine.root=root
            job={'artifact_path':str(artifact),'notes_protocol':'study_v1','notes_path':'previous-good-notes',
                 'notes_only':True,'notes_prior_status':'completed','publication_id':'previous-publication'}
            engine.accept(job,'notes',result,0)
            self.assertEqual(job['notes_error']['diagnostics'],error.diagnostics)
            self.assertEqual(job['notes_path'],'previous-good-notes')
            self.assertEqual(job['status'],'completed')
            self.assertNotIn(SECRET,json.dumps(job))

if __name__=='__main__':unittest.main()
