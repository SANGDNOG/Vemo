import json
import unittest
from voice_memo_transcriber import notes


class ReferenceNotesTests(unittest.TestCase):
    def test_exact_source_ids(self):
        source=''.join(f'{i}번 한국어 English 문장.\n' for i in range(100))
        parts=notes.source_passages(source)
        self.assertEqual(''.join(parts),source)
        self.assertTrue(all(0<len(p)<=400 for p in parts))
        result=notes.decode_notes_lines('API | 일정 설명 | 1,2\n배포 | 작업 설명 | 2',source)
        self.assertEqual(result['summary'][0]['quotes'],parts[:2])
        self.assertEqual(result['topics'][1]['quotes'],[parts[1]])
        self.assertEqual(notes.validate_notes(result,source),result)

    def test_invalid_lines_fail_without_partial_result(self):
        source='source evidence'
        cases=['','bad','API | summary | 0','API | summary | 999','API | summary | true',
               'API | summary | 1.0','API | summary | 1 extra','API | summary | 1|2',
               ' | summary | 1','API | | 1','API | summary | '+','.join(['1']*9),
               'x'*81+' | summary | 1','API | '+'x'*1001+' | 1',
               '\n'.join(['API | summary | 1']*9),'API | valid | 1\nbad line',None,{},1]
        for value in cases:
            with self.subTest(value=str(value)[:30]),self.assertRaises(ValueError):notes.decode_notes_lines(value,source)

    def test_reference_dedup_and_unmodified_text(self):
        result=notes.decode_notes_lines('API | 설명 "{ 문자열 | 1,1','원문')
        self.assertEqual(result['summary'][0]['quotes'],['원문'])
        self.assertEqual(result['summary'][0]['text'],'설명 "{ 문자열')
