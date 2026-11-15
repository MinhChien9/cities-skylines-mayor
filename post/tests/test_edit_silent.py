import unittest

from post.edit import BREATH, LEAD_IN, plan_timeline, silent_timing, transcript_srt
from post.script import MIN_SILENT_LINE, READ_PAUSE, READ_RATE, line_seconds


def script(*texts):
    return {"lines": [{"vo": t, "visual": {"type": "still", "file": "x.png"}} for t in texts]}


class SilentTimingTests(unittest.TestCase):
    def test_a_line_lasts_its_reading_time(self):
        s = script("Thị trấn của tôi đang chết dần rồi đây.")  # 9 syllables
        voice, words = silent_timing(s)
        timeline = plan_timeline(s, voice, words)
        self.assertAlmostEqual(timeline[0]["length"], 9 / READ_RATE + READ_PAUSE, places=2)

    def test_short_lines_hold_for_the_minimum(self):
        s = script("Xong.")
        voice, words = silent_timing(s)
        self.assertAlmostEqual(plan_timeline(s, voice, words)[0]["length"], MIN_SILENT_LINE, places=2)

    def test_words_are_spread_in_order_inside_the_line(self):
        voice, words = silent_timing(script("một hai ba bốn"))
        placed = words["lines"][0]["words"]
        self.assertEqual([w["text"] for w in placed], ["một", "hai", "ba", "bốn"])
        self.assertEqual(placed[0]["start"], 0.0)
        for a, b in zip(placed, placed[1:]):
            self.assertAlmostEqual(a["end"], b["start"], places=3)
        self.assertLessEqual(placed[-1]["end"], voice["lines"][0]["seconds"])


class SilentValidatorTests(unittest.TestCase):
    def test_the_validator_times_silent_lines_like_the_edit(self):
        s = script("Thị trấn của tôi đang chết dần rồi đây.", "Xong.")
        voice, words = silent_timing(s)
        timeline = plan_timeline(s, voice, words)
        for line, item in zip(s["lines"], timeline):
            self.assertAlmostEqual(line_seconds(line, silent=True), item["length"], places=2)


class TranscriptTests(unittest.TestCase):
    def test_one_cue_per_line_on_the_final_timeline(self):
        s = script("Một.", "Hai ba.")
        voice, words = silent_timing(s)
        srt = transcript_srt(s, plan_timeline(s, voice, words))
        cues = srt.strip().split("\n\n")
        self.assertEqual(len(cues), 2)
        self.assertTrue(cues[0].startswith("1\n00:00:00,150 --> "))
        self.assertTrue(cues[1].endswith("Hai ba."))
        first_line_end = MIN_SILENT_LINE  # the second cue starts after the first line
        self.assertIn(f"00:00:0{int(first_line_end + LEAD_IN)}", cues[1])
        self.assertGreater(BREATH, 0)


if __name__ == "__main__":
    unittest.main()
