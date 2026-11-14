import json
import os
import tempfile
import unittest

from post.brief import build_brief
from post.common import syllables, timecode_seconds
from post.script import validate


def entry(seq, kind, minute, timecode=None, population=100, **extra):
    e = {
        "seq": seq, "kind": kind, "session": "s1",
        "wall": f"2026-11-15T01:{minute:02d}:00.000+07:00",
        "recording": {"active": timecode is not None, "paused": False, "timecode": timecode},
        "game": {"date": f"2030-01-{seq:02d}T00:00:00", "population": population, "cash": 1000 - seq, "paused": False},
    }
    e.update(extra)
    return e


JOURNAL = [
    entry(1, "studio", 0, tool="studio_record", args={"action": "start"}, result={"ok": True}),
    entry(2, "observation", 1, "00:00:01.000", 75, text="The town is emptying."),
    entry(3, "build", 2, "00:00:30.500", 75, tool="city_build_road",
          args={"prefab": "Power Line", "startX": 1}, result={"ok": True, "cost": 2980, "segmentIds": [1, 2]}),
    entry(4, "build", 3, "00:01:00.000", 80, tool="city_place_building",
          args={"prefab": "Wind Turbine"}, result={"ok": False, "errors": ["ObjectCollision"]}),
    entry(5, "mistake", 4, "00:01:05.000", 64, text="Stopped 20 m short."),
    entry(6, "marker", 5, "00:01:10.000", 90, text="Plan B"),
    entry(7, "milestone", 6, None, 166, text="Back to 166."),
    entry(8, "studio", 7, tool="studio_record", args={"action": "stop"},
          result={"ok": True, "outputPath": "Z:/does-not-exist.mp4"}),
]


class CommonTests(unittest.TestCase):
    def test_timecode(self):
        self.assertEqual(timecode_seconds("00:03:22.416"), 202.416)
        self.assertIsNone(timecode_seconds(None))

    def test_syllables_count_vietnamese_words_and_numbers(self):
        self.assertEqual(syllables("Thị trấn của tôi đang chết dần."), 7)
        self.assertEqual(syllables("Dân số từ 64 lên 164."), 3 + 4 + 1 + 6)
        self.assertEqual(syllables(""), 0)


class BriefTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.mkdtemp()
        self.path = os.path.join(self.dir, "j.jsonl")
        with open(self.path, "w", encoding="utf-8") as f:
            for e in JOURNAL:
                f.write(json.dumps(e) + "\n")

    def test_beats_skip_studio_plumbing_and_keep_story(self):
        brief = build_brief(self.path)
        self.assertEqual([b["seq"] for b in brief["beats"]], [2, 3, 4, 5, 6, 7])
        self.assertEqual(brief["beats"][1]["t"], 30.5)
        self.assertIsNone(brief["beats"][5]["t"])            # not recording
        self.assertEqual(brief["beats"][1]["action"]["cost"], 2980)
        self.assertEqual(brief["beats"][2]["action"]["errors"], ["ObjectCollision"])

    def test_stats(self):
        stats = build_brief(self.path)["stats"]
        self.assertEqual(stats["populationMin"], 64)
        self.assertEqual(stats["populationMax"], 166)
        self.assertEqual(stats["builds"], 1)
        self.assertEqual(stats["refusedOrFailed"], 1)
        self.assertEqual(stats["spent"], 2980)

    def test_recording_from_the_journal_reported_missing(self):
        brief = build_brief(self.path)
        self.assertEqual(brief["recording"], {"path": "Z:/does-not-exist.mp4", "missing": True})


def script(**overrides):
    s = {
        "id": "t", "title": "T", "format": "short", "language": "vi",
        "lines": [
            {"role": "hook", "vo": "Thị trấn của tôi đang chết dần.", "visual": {"type": "clip", "in": 0, "out": 3}, "source": [2]},
            {"role": "beat", "vo": "Tôi kéo dây điện.", "visual": {"type": "clip", "in": 30, "out": 40, "speed": 2}, "source": [3]},
            {"role": "punchline", "vo": "Gần thôi chưa đủ.", "visual": {"type": "clip", "in": 60, "out": 64}},
        ],
    }
    s.update(overrides)
    return s


BRIEF = {"beats": [{"seq": 2}, {"seq": 3}], "recording": {"duration": 100.0}, "timelapse": [
    {"folder": "F", "viewpoints": {"overview": ["a.png"] * 16}}]}


class ScriptTests(unittest.TestCase):
    def test_valid_script(self):
        errors, warnings, timings = validate(script(), BRIEF)
        self.assertEqual(errors, [])
        self.assertEqual(timings[1]["seconds"], 5.0)        # 10 s clip at 2x

    def test_shape_rules(self):
        lines = script()["lines"]
        errors, _, _ = validate(script(lines=[lines[1], lines[0], lines[2]]), BRIEF)
        self.assertIn("the first line must be the hook", errors)
        errors, _, _ = validate(script(lines=lines[:2]), BRIEF)
        self.assertIn("the last line must be the punchline", errors)

    def test_hook_must_fit_three_seconds(self):
        lines = script()["lines"]
        lines[0] = dict(lines[0], vo=" ".join(["một"] * 20))
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("hook must be spoken" in e for e in errors))

    def test_beats_cite_existing_journal_entries(self):
        lines = script()["lines"]
        lines[1] = dict(lines[1], source=[99])
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("source [99]" in e for e in errors))
        lines[1] = dict(lines[1], source=[])
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("must cite" in e for e in errors))

    def test_clip_inside_recording(self):
        lines = script()["lines"]
        lines[2] = dict(lines[2], visual={"type": "clip", "in": 90, "out": 120})
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("recording is 100.0 s" in e for e in errors))

    def test_short_budget_and_timelapse_frames(self):
        lines = script()["lines"]
        lines[1] = dict(lines[1], visual={"type": "timelapse", "viewpoint": "overview", "fps": 8})
        errors, _, timings = validate(script(lines=lines), BRIEF)
        self.assertEqual(errors, [])
        self.assertEqual(timings[1]["seconds"], 2.0)        # 16 frames at 8 fps
        lines[1] = dict(lines[1], visual={"type": "clip", "in": 0, "out": 99})
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("a short must be" in e for e in errors))

    def test_missing_timelapse_viewpoint(self):
        lines = script()["lines"]
        lines[1] = dict(lines[1], visual={"type": "timelapse", "viewpoint": "nope"})
        errors, _, _ = validate(script(lines=lines), BRIEF)
        self.assertTrue(any("no timelapse frames" in e for e in errors))


if __name__ == "__main__":
    unittest.main()
