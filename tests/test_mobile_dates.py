import unittest
from voice_memo_transcriber.dates import recording_day


class MobileDateTests(unittest.TestCase):
    def test_mobile_fixed_offset_preserves_recording_day(self):
        self.assertEqual(recording_day('2026-10-17T00:30:00+14:00', 'UTC+14:00'), '2026-10-17')
        self.assertEqual(recording_day('2026-10-17T23:30:00-12:00', 'UTC-12:00'), '2026-10-17')
        self.assertEqual(recording_day('2026-10-17T00:01:00+05:45', 'UTC+05:45'), '2026-10-17')

    def test_invalid_fixed_offsets_fail(self):
        for zone in ('UTC+14:01', 'UTC+25:00', 'UTC+05:60', 'UTC+5:30'):
            with self.subTest(zone=zone), self.assertRaises(ValueError):
                recording_day('2026-10-17T00:30:00Z', zone)

    def test_existing_iana_and_fallback_remain(self):
        self.assertEqual(recording_day('2026-10-16T23:30:00Z', 'Asia/Seoul'), '2026-10-17')
        self.assertEqual(recording_day('2026-10-16T23:30:00Z', None), '2026-10-17')
        self.assertEqual(recording_day('2026-10-16T23:30:00Z', 'UTC'), '2026-10-16')
