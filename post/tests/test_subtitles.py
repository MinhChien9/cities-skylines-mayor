import unittest

from post.subtitles import align, chunks, normalise, to_ass


class AlignTests(unittest.TestCase):
    def test_exact_match_takes_the_recognised_times(self):
        heard = [("Thị", 0.0, 0.1), ("trấn", 0.1, 0.3), ("chết.", 0.3, 0.6)]
        words, score = align("Thị trấn chết.", heard, 1.0)
        self.assertEqual(score, 1.0)
        self.assertEqual([(w["text"], w["start"], w["end"]) for w in words],
                         [("Thị", 0.0, 0.1), ("trấn", 0.1, 0.3), ("chết.", 0.3, 0.6)])

    def test_unheard_words_are_interpolated_between_neighbours(self):
        # "1,2" is heard as two tokens and matches nothing: it fills the gap.
        heard = [("dài", 1.0, 1.2), ("1", 1.2, 1.4), (",2", 1.4, 1.6), ("cây", 1.7, 1.9), ("số.", 1.9, 2.1)]
        words, score = align("dài 1,2 cây số.", heard, 2.5)
        self.assertEqual(score, 0.75)
        self.assertEqual(words[1]["text"], "1,2")
        self.assertAlmostEqual(words[1]["start"], 1.2)
        self.assertAlmostEqual(words[1]["end"], 1.7)

    def test_nothing_heard_spreads_over_the_line(self):
        words, score = align("một hai", [], 2.0)
        self.assertEqual(score, 0.0)
        self.assertLess(words[0]["start"], words[1]["start"])
        self.assertLessEqual(words[1]["end"], 2.0)

    def test_times_never_go_backwards(self):
        heard = [("b", 0.5, 0.7), ("d", 1.5, 1.8)]
        words, _ = align("a b c d e", heard, 2.5)
        starts = [w["start"] for w in words]
        self.assertEqual(starts, sorted(starts))

    def test_normalise(self):
        self.assertEqual(normalise("Dần."), "dần")
        self.assertEqual(normalise("“Ống”"), "ống")


class ChunkAndAssTests(unittest.TestCase):
    WORDS = [{"text": t, "start": i * 0.3, "end": i * 0.3 + 0.25}
             for i, t in enumerate("Một nửa dân đã bỏ đi. 25 căn nhà bỏ hoang,".split())]

    def test_chunks_break_after_punctuation_and_at_four_words(self):
        groups = [" ".join(w["text"] for w in g) for g in chunks(self.WORDS)]
        self.assertEqual(groups, ["Một nửa dân đã", "bỏ đi.", "25 căn nhà bỏ", "hoang,"])

    def test_ass_places_lines_and_highlights_each_word(self):
        ass = to_ass([{"start": 10.0, "words": self.WORDS[:2]}], 1080, 1920)
        self.assertIn("PlayResX: 1080", ass)
        events = [l for l in ass.splitlines() if l.startswith("Dialogue:")]
        self.assertEqual(len(events), 2)                         # one per word of the chunk
        self.assertIn("0:00:10.00,0:00:10.30", events[0])
        self.assertIn("{\\c&H0000D7FF}Một{\\c&H00FFFFFF&} nửa", events[0])
        self.assertIn("Một {\\c&H0000D7FF}nửa", events[1])


if __name__ == "__main__":
    unittest.main()
