"""Lossless overflow formatting, using synthetic passages only."""
import json
import unittest

from voice_memo_transcriber.study_notes import decode


class StudyNotesOverflowTests(unittest.TestCase):
    passages = [f'Source passage {i}.\n' for i in range(1, 81)]

    def value(self, lengths):
        return {'summary': [{'text': chr(65+i)*n, 'refs': [i+1]}
                            for i, n in enumerate(lengths)],
                'topics': [{'keyword': 'Topic', 'summary': 'Detailed condition.', 'refs': [1]}]}

    def decode(self, value):
        return decode(json.dumps(value), self.passages, ''.join(self.passages))

    def test_feasible_partition_survives_shortest_pair_dead_end(self):
        value = self.value([250, 100, 100, 250, 400, 400, 400])
        result = self.decode(value)
        self.assertEqual([len(s['text']) for s in result['summary']], [352, 352, 400, 400, 400])
        self.assertEqual([s['text'] for s in result['summary']],
                         ['A'*250+'\n\n'+'B'*100, 'C'*100+'\n\n'+'D'*250, 'E'*400, 'F'*400, 'G'*400])
        self.assertEqual([s['quotes'] for s in result['summary']],
                         [self.passages[:2], self.passages[2:4], [self.passages[4]], [self.passages[5]], [self.passages[6]]])

    def test_eight_summaries_preserve_every_text_and_ordered_evidence(self):
        value = self.value([123, 102, 102, 90, 102, 85, 94, 116])
        value['summary'][0]['refs'] = [3, 1]
        value['summary'][1]['refs'] = [1, 2]
        value['summary'][2]['refs'] = [2, 3]
        result = self.decode(value)
        # Deterministic longest-prefix partition; nothing is discarded.
        groups = [[0, 1, 2], [3, 4], [5], [6], [7]]
        self.assertEqual([s['text'] for s in result['summary']],
                         ['\n\n'.join(value['summary'][i]['text'] for i in group) for group in groups])
        expected_refs = [[3, 1, 2], [4, 5], [6], [7], [8]]
        self.assertEqual([s['quotes'] for s in result['summary']],
                         [[self.passages[i-1] for i in ids] for ids in expected_refs])
        self.assertEqual(result['topics'][0]['summary'], 'Detailed condition.')

    def test_partition_is_deterministic_and_five_are_unchanged(self):
        value = self.value([10]*6)
        result = self.decode(value)
        self.assertEqual(result['summary'][0]['text'], 'A'*10+'\n\n'+'B'*10)
        value['summary'] = value['summary'][:5]
        self.assertEqual([s['text'] for s in self.decode(value)['summary']],
                         [s['text'] for s in value['summary']])

    def test_merge_accepts_exact_text_and_evidence_bounds(self):
        value = self.value([199]*6)
        value['summary'][0]['refs'] = list(range(1, 33))
        value['summary'][1]['refs'] = list(range(33, 65))
        result = self.decode(value)
        self.assertEqual(len(result['summary'][0]['text']), 400)
        self.assertEqual(result['summary'][0]['quotes'], self.passages[:64])

    def test_unmergeable_overflow_is_rejected_without_cropping(self):
        for lengths in ([200]*6, [250]*8):
            with self.subTest(lengths=lengths), self.assertRaisesRegex(ValueError, 'summary_overflow_unmergeable'):
                self.decode(self.value(lengths))
        value = self.value([10]*6)
        for i, item in enumerate(value['summary']):
            item['refs'] = list(range(1, 65)) if i%2 == 0 else list(range(17, 81))
        with self.assertRaisesRegex(ValueError, 'summary_overflow_unmergeable'):
            self.decode(value)

    def test_invalid_evidence_union_is_skipped_for_a_valid_partition(self):
        value = self.value([1, 1, 100, 100, 100, 100])
        value['summary'][0]['refs'] = list(range(1, 65))
        value['summary'][1]['refs'] = list(range(17, 81))
        result = self.decode(value)
        self.assertEqual([s['text'] for s in result['summary']],
                         ['A', 'B', 'C'*100+'\n\n'+'D'*100, 'E'*100, 'F'*100])

    def test_all_original_refs_and_fields_validate_before_any_merge(self):
        for refs in ([1, 1], [True], [0], [81], ['1'], list(range(1, 66))):
            value = self.value([10]*8); value['summary'][7]['refs'] = refs
            with self.subTest(refs=refs), self.assertRaisesRegex(ValueError, 'invalid_canonical_integer_refs'):
                self.decode(value)
        value = self.value([10]*8); value['summary'][7]['extra'] = 'not allowed'
        with self.assertRaisesRegex(ValueError, 'invalid_summary_fields'):
            self.decode(value)
        value = self.value([10]*8); value['topics'][0]['refs'] = [0]
        with self.assertRaisesRegex(ValueError, 'invalid_canonical_integer_refs'):
            self.decode(value)

    def test_sixteen_is_bounded_and_malformed_json_remains_rejected(self):
        value = self.value([10]*16)
        result = self.decode(value)
        self.assertEqual(len(result['summary']), 5)
        self.assertEqual('\n\n'.join(s['text'] for s in result['summary']),
                         '\n\n'.join(s['text'] for s in value['summary']))
        with self.assertRaisesRegex(ValueError, 'invalid_summary_count'):
            self.decode(self.value([10]*17))
        raw = json.dumps(value)
        for malformed in (raw[:-1], raw+'}', raw.replace('"refs": [1]', '"refs": [1], "refs": [2]', 1)):
            with self.subTest(malformed=malformed), self.assertRaises(ValueError):
                decode(malformed, self.passages, ''.join(self.passages))

    def test_full_topic_duplicate_cannot_be_hidden_by_merging(self):
        value = self.value([10]*6)
        value['topics'][0]['summary'] = value['summary'][0]['text']
        with self.assertRaisesRegex(ValueError, 'core_duplicates_full_topic'):
            self.decode(value)
        value['topics'][0]['summary'] = 'A'*10+'\n\n'+'B'*10
        with self.assertRaisesRegex(ValueError, 'core_duplicates_full_topic'):
            self.decode(value)


if __name__ == '__main__': unittest.main()
