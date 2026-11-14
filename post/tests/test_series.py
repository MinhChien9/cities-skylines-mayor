import unittest

from post.series import check, context, publish


def script(threads, sid="e001"):
    return {"id": sid, "title": "T", "threads": threads,
            "lines": [{"role": "hook", "vo": "a"}, {"role": "punchline", "vo": "Gần thôi chưa đủ."}]}


E001 = {"closes": ["sewage"], "opens": [{"id": "sewage", "title": "Nước thải"}, {"id": "jobs", "title": "Việc làm"}]}


class SeriesTests(unittest.TestCase):
    def test_first_episode_opens_and_closes(self):
        series = publish(script(E001), {"episodes": [], "threads": []})
        status = {t["id"]: t["status"] for t in series["threads"]}
        self.assertEqual(status, {"sewage": "closed", "jobs": "open"})
        self.assertEqual(series["episodes"][0]["day"], 1)
        self.assertEqual(series["episodes"][0]["lesson"], "Gần thôi chưa đủ.")
        ctx = context(series)
        self.assertEqual([t["id"] for t in ctx["openThreads"]], ["jobs"])
        self.assertEqual(ctx["nextDay"], 2)

    def test_next_episode_must_touch_an_open_thread(self):
        series = publish(script(E001), {"episodes": [], "threads": []})
        self.assertIn("the episode must advance or close at least one thread",
                      check(script({"opens": [{"id": "traffic", "title": "Kẹt xe"}]}, "e002"), series))
        self.assertEqual(check(script({"advances": ["jobs"]}, "e002"), series), [])

    def test_unknown_closed_duplicate_and_bad_ids(self):
        series = publish(script(E001), {"episodes": [], "threads": []})
        errors = check(script({"advances": ["nope"], "closes": ["sewage"],
                               "opens": [{"id": "jobs", "title": "x"}, {"id": "Bad Id", "title": "y"}]}, "e001"), series)
        text = " | ".join(errors)
        for expected in ("unknown thread 'nope'", "already closed", "'jobs' already exists",
                         "lowercase letters", "already published"):
            self.assertIn(expected, text)

    def test_script_without_threads(self):
        self.assertTrue(check({"id": "e009", "lines": []}, {"episodes": [], "threads": []}))


if __name__ == "__main__":
    unittest.main()
