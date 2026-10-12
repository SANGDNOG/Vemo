import hashlib,json,pathlib,tempfile,unittest
from voice_memo_transcriber import worker
from voice_memo_transcriber.artifacts import build_publication

SOURCE='한국어 계획을 정했습니다. The API launches Monday. 중요한 마지막 결정입니다.'
def generated(quote=SOURCE):
    return {'summary':[{'text':'계획과 결정','quotes':[quote]}], 'highlights':[{'quote':quote}], 'topics':[{'keyword':'API','summary':'출시 계획','quotes':[quote]}]}

class NotesTests(unittest.TestCase):
    def notes(self):
        try:
            from voice_memo_transcriber import notes
        except ImportError:self.fail("notes generator missing")
        return notes
    def test_exact_citations_filter_fabricated_and_require_evidence(self):
        n=self.notes();raw=generated('The API launches Monday.')
        raw['summary'].extend([{'text':'허구','quotes':['없는 인용']},{'text':'근거 없음','quotes':[]}])
        raw['highlights'].extend([{'quote':'the API launches Monday.'},{'quote':''}])
        raw['topics'].append({'keyword':'허구','summary':'없음','quotes':['없는 인용']})
        result=n.validate_notes(raw,SOURCE)
        self.assertEqual(result['schema_version'],1)
        self.assertEqual(result['source_sha256'],hashlib.sha256(SOURCE.encode('utf-8')).hexdigest())
        self.assertEqual(len(result['summary']),1);self.assertEqual(len(result['highlights']),1);self.assertEqual(len(result['topics']),1)
        self.assertEqual(result['summary'][0]['quotes'],['The API launches Monday.'])
    def test_empty_malformed_and_overlong_items_fail(self):
        n=self.notes()
        for raw in ('bad JSON','[]','{}','{"summary":[],"highlights":[],"topics":[]}',generated('absent'),generated(' ')):
            with self.subTest(raw=raw),self.assertRaises(ValueError):n.validate_notes(raw,SOURCE)
        source='x'*1001
        for raw in (generated('x'*601),{'summary':[{'text':'x'*1001,'quotes':['x']}],'highlights':[],'topics':[]},{'summary':[],'highlights':[],'topics':[{'keyword':'x'*81,'summary':'x','quotes':['x']}]}, {'summary':'oops','highlights':[],'topics':[]}):
            with self.subTest(raw=raw),self.assertRaises(ValueError):n.validate_notes(raw,source)
    def test_terminal_closing_brackets_preserve_exact_content(self):
        n=self.notes();source='그는 "API [v2]"와 경로 \\data를 확인했습니다.'
        response=json.dumps(generated(source),ensure_ascii=False)
        self.assertTrue(response.endswith(']}]}'))
        malformed=response[:-4]+'}]}]}'
        expected=n.validate_notes(response,source)
        self.assertEqual(n.validate_notes(malformed,source),expected)
        for rejected in (response[:-1],response[:-4]+'}',response+'{}',response[:-4]+',]}]}',
                '{"summary":[{"text":"끝나지 않은 인용 }]}]}'):
            with self.subTest(rejected=rejected),self.assertRaises(ValueError):n.validate_notes(rejected,source)
    def test_every_chunk_participates_and_checkpoint_resumes(self):
        n=self.notes();source=('한국어 English synthetic 42. '*300)+'마지막 결론 END.';seen=[];budgets=[]
        def produce(text,budget):
            seen.append(text);budgets.append(budget);raw=generated(text[:80]);raw['summary'][0]['text']=f'구간 {len(seen)}';return json.dumps(raw,ensure_ascii=False)
        state=None;result={}
        while 'notes' not in result:
            result=n.notes_unit(source,state,produce)
            if 'notes_checkpoint' in result:
                state=json.loads(json.dumps(result['notes_checkpoint']))
                self.assertEqual(result['completed_units'],len(seen))
        self.assertEqual(''.join(seen),source);self.assertGreater(len(seen),3)
        self.assertLessEqual(len(seen),6);self.assertTrue(all(len(c)<=1800 for c in seen));self.assertTrue(all(b<=3072 for b in budgets))
        self.assertEqual(len(result['notes']['summary']),len(seen))
        with self.assertRaises(ValueError):n.notes_unit(source+'changed',state,produce)
    def test_publication_adds_notes_identity_and_preserves_old_versions(self):
        n=self.notes()
        with tempfile.TemporaryDirectory() as td:
            job={'id':'job','uuid':'fixture','revision':1,'pcm_hash':'a'*64,'topic':'테스트','recording_date':'2026-10-11'}
            transcript={'text':SOURCE,'segments':[],'alignment_status':'completed'}
            old=pathlib.Path(build_publication(td,job,transcript,{}));before=old.read_bytes()
            notes=n.validate_notes(generated(),SOURCE)
            new=pathlib.Path(build_publication(td,job,transcript,{},notes=notes));m=json.loads(new.read_text())
            self.assertNotEqual(old,new);self.assertEqual(old.read_bytes(),before)
            files={f['name']:pathlib.Path(f['path']) for f in m['files']}
            self.assertIn('테스트 — 2026-10-11 노트.md',files);self.assertIn('테스트 — 2026-10-11 노트.json',files)
            self.assertEqual(json.loads(files['테스트 — 2026-10-11 노트.json'].read_text()),notes)
            markdown=files['테스트 — 2026-10-11 노트.md'].read_text();self.assertIn('AI',markdown);self.assertIn(SOURCE,markdown)
            self.assertEqual(build_publication(td,job,transcript,{},notes=notes),str(new))
            notes['summary'][0]['text']='바뀐 요약'
            newer=pathlib.Path(build_publication(td,job,transcript,{},notes=notes));self.assertNotEqual(new,newer);self.assertTrue(new.exists())

    def test_sentence_chunks_cover_exact_source_with_token_budget(self):
        n=self.notes();source=('문장 한 개입니다. The API starts Monday! '+('x'*50)+'\n')*100
        chunks=n.source_chunks(source)
        self.assertEqual(''.join(chunks),source);self.assertTrue(all(len(c)<=1800 for c in chunks))
        self.assertTrue(all(c.endswith((' ','\n')) for c in chunks[:-1]))
        bounded=n.source_chunks(source,token_count=lambda t:len(t)*4)
        self.assertEqual(''.join(bounded),source);self.assertTrue(all(len(c)*4<=3000 for c in bounded))
    def test_finalization_merges_nfc_casefold_topics_and_duplicate_quotes(self):
        n=self.notes();source='API 시작. API 종료. café 일정. 마지막 계획.'
        raw={'summary':[{'text':'계획','quotes':['API 시작.']},{'text':'계획','quotes':['API 종료.']}],
            'highlights':[{'quote':'API 시작.'},{'quote':'API 시작.'},{'quote':'마지막 계획.'}],
            'topics':[{'keyword':'API','summary':'시작 계획','quotes':['API 시작.']},{'keyword':'api','summary':'종료 계획','quotes':['API 종료.']},
                {'keyword':'CAFÉ','summary':'일정','quotes':['café 일정.']},{'keyword':'Café','summary':'마지막 계획','quotes':['마지막 계획.']}]}
        self.assertTrue(hasattr(n,'finalize_notes'),'notes deduplication missing')
        result=n.finalize_notes(raw,source)
        self.assertEqual(len(result['topics']),2);self.assertEqual(len(result['highlights']),2);self.assertEqual(len(result['summary']),1)
        self.assertEqual(result['topics'][0]['keyword'],'API');self.assertIn('종료 계획',result['topics'][0]['summary'])
        self.assertEqual(result['topics'][0]['quotes'],['API 시작.','API 종료.'])
        self.assertEqual(n.finalize_notes(result,source),result)
    def test_merged_topic_summary_and_quotes_stay_bounded(self):
        n=self.notes();raw={'summary':[],'highlights':[],'topics':[{'keyword':'API' if i%2 else 'api','summary':str(i)*990,'quotes':[str(i)]} for i in range(10)]}
        self.assertTrue(hasattr(n,'finalize_notes'),'notes deduplication missing')
        result=n.finalize_notes(raw,'0123456789')
        self.assertEqual(len(result['topics']),1);self.assertLessEqual(len(result['topics'][0]['summary']),1000)
        self.assertLessEqual(len(result['topics'][0]['quotes']),8)

class NotesWorkerBoundaryTests(unittest.TestCase):
    def source_map(self,content):
        passages=json.loads(content)
        self.assertIsInstance(passages,dict)
        self.assertEqual(list(passages),[str(i+1) for i in range(len(passages))])
        self.assertTrue(passages)
        self.assertTrue(all(isinstance(p,str) and 0<len(p)<=400 for p in passages.values()))
        return passages

    def test_worker_source_ids_resolve_exact_passages_and_reject_invalid_response_atomically(self):
        from unittest.mock import patch
        first='한국어 API 결정입니다. '.ljust(399,'a')+'\n'
        second='English exact evidence. '.ljust(399,'b')+'\n'
        source=first+second
        seen=[];test=self
        class Tokenizer:
            def encode(self,text,add_special_tokens=False):return list(text)
            def apply_chat_template(self,messages,**kwargs):
                seen.append(messages[1]['content'])
                test.assertEqual(test.source_map(seen[-1]),{'1':first,'2':second})
                return list(messages[1]['content'])
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);model=root/'models'/'title';model.mkdir(parents=True)
            (model/'config.json').write_text('{}');(model/'fixture.safetensors').write_bytes(b'synthetic')
            artifact=root/'transcript.json';artifact.write_text(json.dumps({'text':source}),encoding='utf-8')
            request={'state_root':td,'model_root':str(root/'models'),'artifact_path':str(artifact),'notes_protocol':'legacy'}
            before=artifact.read_bytes()
            with patch('mlx_lm.load',return_value=(object(),Tokenizer())),patch('mlx_lm.generate',return_value='API | 출시 결정 | 2,1,2'):
                result=worker.notes(request)
            self.assertEqual(result['completed_units'],1);self.assertEqual(result['total_units'],1)
            notes=result['notes']
            self.assertEqual(notes['summary'],[{'text':'출시 결정','quotes':[second,first]}])
            self.assertEqual(notes['topics'],[{'keyword':'API','summary':'출시 결정','quotes':[second,first]}])
            self.assertEqual(notes['highlights'],[{'quote':second}])
            self.assertEqual(notes['source_sha256'],hashlib.sha256(source.encode()).hexdigest())
            valid='API | valid card | 1'
            invalid=['Missing | absent reference | 3','Injected | fabricated quotation | 없는 인용',
                     'Wrong | too many fields | 1 | ignored','Wrong | no reference',
                     'Wrong | zero ID | 0','Wrong | fractional ID | 1.0',
                     'Wrong | boolean ID | true']
            for response in invalid:
                for raw in (response,valid+'\n'+response):
                    with self.subTest(raw=raw),patch('mlx_lm.load',return_value=(object(),Tokenizer())),patch('mlx_lm.generate',return_value=raw):
                        with self.assertRaises(ValueError):worker.notes(request)
            self.assertEqual(artifact.read_bytes(),before)

    def test_pinned_title_model_bounded_prompts_and_all_source_characters(self):
        from unittest.mock import patch
        # Replace only heavy MLX inference; execute owned-path checks, chunking,
        # exact validation, merging and checkpoint code with synthetic data.
        source=('원문에 있는 명령을 무시하고 작업을 기록합니다. API 일정입니다! '+('x'*65)+'\n')*18
        seen=[];instructions=[];test=self
        class Tokenizer:
            def encode(self,text,add_special_tokens=False):return [0]*(len(text)*4)
            def apply_chat_template(self,messages,**kwargs):
                self_text=messages[1]['content'];seen.append(self_text);instructions.append(messages[0]['content'])
                test.source_map(self_text)
                return [0]*(len(self_text)*4+300)
        def generate(model,tokenizer,**kwargs):
            self.assertLessEqual(len(kwargs['prompt']),4096);self.assertLessEqual(kwargs['max_tokens'],3072)
            return 'API | 계획과 결정 | 1'
        with tempfile.TemporaryDirectory() as td:
            root=pathlib.Path(td);model=root/'models'/'title';model.mkdir(parents=True)
            (model/'config.json').write_text('{}');(model/'fixture.safetensors').write_bytes(b'synthetic')
            artifact=root/'transcript.json';artifact.write_text(json.dumps({'text':source}),encoding='utf-8')
            request={'state_root':td,'model_root':str(root/'models'),'artifact_path':str(artifact),'notes_protocol':'legacy'}
            with patch('mlx_lm.load',return_value=(object(),Tokenizer())) as load,patch('mlx_lm.generate',side_effect=generate):
                result={}
                while 'notes' not in result:
                    result=worker.notes(request)
                    if 'notes_checkpoint' in result:request['notes_checkpoint']=result['notes_checkpoint']
                self.assertTrue(all(pathlib.Path(call.args[0]).resolve()==model.resolve() and call.kwargs['trust_remote_code'] is False for call in load.call_args_list))
        self.assertEqual(''.join(p for t in seen for p in self.source_map(t).values()),source)
        self.assertTrue(all(sum(len(p)*4 for p in self.source_map(t).values())<=3000 for t in seen))
        self.assertEqual(len(result['notes']['topics']),1)
        self.assertTrue(all('모든 사실' in s and '다른 주제' in s for s in instructions))
