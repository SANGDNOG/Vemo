"""Lossless bounded summary normalization, using synthetic data only."""
import copy
import hashlib
import json
import unittest

from voice_memo_transcriber import study_notes as study


class StudySummaryOverflowIndependentTests(unittest.TestCase):
    def fixture(self, count=8, passage_count=80):
        passages = [f'근거 {i:03d}: 합성 조건을 보존합니다.\n' for i in range(1, passage_count + 1)]
        value = {
            'summary': [{'text': f'결론 {i:02d}: 조건 {i}에서는 제외하지 않습니다.',
                         'refs': [i, i + 1]} for i in range(1, count + 1)],
            'topics': [{'keyword': '독립 주제', 'summary': '상세 설명은 그대로 유지합니다.', 'refs': [1, 2]}],
        }
        return value, passages

    def decode(self, value, passages):
        return study.decode(json.dumps(value, ensure_ascii=False), passages, ''.join(passages))

    def assert_lossless_adjacent_groups(self, value, passages, result):
        self.assertTrue(1 <= len(result['summary']) <= 5)
        index = 0
        for card in result['summary']:
            self.assertLessEqual(len(card['text']), 400)
            remaining = card['text']
            group = []
            while remaining.strip():
                self.assertLess(index, len(value['summary']), 'output added text')
                original = value['summary'][index]
                self.assertTrue(remaining.startswith(original['text']), 'text changed, reordered, or dropped')
                remaining = remaining[len(original['text']):].lstrip()
                group.append(original)
                index += 1
            self.assertTrue(group, 'empty output group')
            expected = {passages[ref - 1] for item in group for ref in item['refs']}
            self.assertEqual(set(card['quotes']), expected, 'a grouped claim lost or gained evidence')
            self.assertEqual(len(card['quotes']), len(expected))
            self.assertLessEqual(len(card['quotes']), 64)
        self.assertEqual(index, len(value['summary']), 'tail summaries were discarded')
        self.assertEqual(result['topics'], [
            {'keyword': item['keyword'], 'summary': item['summary'],
             'quotes': [passages[ref - 1] for ref in item['refs']]}
            for item in value['topics']])
        self.assertEqual(result['source_sha256'], hashlib.sha256(''.join(passages).encode()).hexdigest())

    def test_every_supported_overflow_count_preserves_text_refs_order_and_topics(self):
        for count in range(6, 17):
            with self.subTest(count=count):
                value, passages = self.fixture(count)
                self.assert_lossless_adjacent_groups(value, passages, self.decode(value, passages))

    def test_eight_uneven_cards_preserve_all_content_without_truncating(self):
        value, passages = self.fixture()
        # Synthetic lengths match the sanitized failing shape, never its private text.
        for i, (item, length) in enumerate(zip(value['summary'], [123, 102, 102, 90, 102, 85, 94, 116])):
            prefix = f'독립 결론 {i:02d}: '
            item['text'] = prefix + chr(0xAC00 + i) * (length - len(prefix))
        self.assert_lossless_adjacent_groups(value, passages, self.decode(value, passages))

    def test_in_range_summaries_remain_separate_and_unchanged(self):
        for count in (1, 5):
            value, passages = self.fixture(count)
            result = self.decode(value, passages)
            self.assertEqual(result['summary'], [
                {'text': item['text'], 'quotes': [passages[r - 1] for r in item['refs']]}
                for item in value['summary']])

    def test_feasible_adjacent_partition_is_not_lost_to_shortest_pair_merge(self):
        value, passages = self.fixture(7)
        for i, (item, length) in enumerate(zip(value['summary'], [250, 100, 100, 250, 400, 400, 400])):
            item['text'] = f'{i:02d}' + chr(0xAC00 + i) * (length - 2)
        result = self.decode(value, passages)
        self.assert_lossless_adjacent_groups(value, passages, result)
        # Only the first two and next two cards can merge into five valid groups.
        self.assertEqual([len(card['text']) for card in result['summary']], [352, 352, 400, 400, 400])

    def test_more_than_sixteen_and_unmergeable_text_reject(self):
        value, passages = self.fixture(17)
        with self.assertRaises(ValueError):
            self.decode(value, passages)
        value, passages = self.fixture(6)
        for i, item in enumerate(value['summary']):
            item['text'] = f'{i:02d}' + chr(0xAC00 + i) * 398
        with self.assertRaises(ValueError):
            self.decode(value, passages)

    def test_unmergeable_evidence_rejects_instead_of_discarding_refs(self):
        value, passages = self.fixture(6, 384)
        for i, item in enumerate(value['summary']):
            item['refs'] = list(range(i * 64 + 1, (i + 1) * 64 + 1))
        with self.assertRaises(ValueError):
            self.decode(value, passages)

    def test_shared_sixty_four_refs_can_merge_without_losing_any_evidence(self):
        value, passages = self.fixture(6)
        for item in value['summary']:
            item['refs'] = list(range(1, 65))
        self.assert_lossless_adjacent_groups(value, passages, self.decode(value, passages))

    def test_invalid_late_card_cannot_be_hidden_by_normalization(self):
        base, passages = self.fixture()
        bad_cards = [None, {'text': '형식 누락'}, {'text': '빈 근거', 'refs': []},
                     {'text': '중복 근거', 'refs': [1, 1]}, {'text': '범위 초과', 'refs': [81]},
                     {'text': '정수 아님', 'refs': [True]}, {'text': '정수 문자열', 'refs': ['1']},
                     {'text': 'x' * 401, 'refs': [1]}, {'text': 7, 'refs': [1]},
                     {'text': '추가 필드', 'refs': [1], 'quotes': ['fabricated']}]
        for bad in bad_cards:
            with self.subTest(card=bad):
                value = copy.deepcopy(base)
                value['summary'][-1] = bad
                with self.assertRaises(ValueError):
                    self.decode(value, passages)

    def test_overflow_does_not_relax_json_or_topic_validation(self):
        value, passages = self.fixture()
        raw = json.dumps(value)
        for invalid in (raw + '}', raw[:-1], raw.replace('"refs": [1, 2]', '"refs": [1], "refs": [1, 2]', 1)):
            with self.subTest(raw_variant=invalid[:30]), self.assertRaises(ValueError):
                study.decode(invalid, passages, ''.join(passages))
        for field, bad in [('refs', [81]), ('summary', 'x' * 1001), ('keyword', 'x' * 81)]:
            value, passages = self.fixture()
            value['topics'][0][field] = bad
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.decode(value, passages)


if __name__ == '__main__':
    unittest.main()
