import unittest

from post.thumbnail import _wrap, ass_for


class ThumbnailTests(unittest.TestCase):
    def test_manual_breaks_win(self):
        self.assertEqual(_wrap("AI CỨU|THỊ TRẤN ĐANG CHẾT", vertical=False), "AI CỨU\\NTHỊ TRẤN ĐANG CHẾT")

    def test_balanced_breaks(self):
        self.assertEqual(_wrap("MỘT HAI BA BỐN", vertical=False), "MỘT HAI\\NBA BỐN")
        self.assertEqual(_wrap("NGẮN GỌN", vertical=False), "NGẮN GỌN")
        self.assertEqual(_wrap("A B C D E F", vertical=True), "A B\\NC D\\NE F")

    def test_ass_has_title_and_optional_number(self):
        with_number = ass_for("TIÊU ĐỀ", "64 → 164", 1280, 720)
        self.assertIn("PlayResX: 1280", with_number)
        self.assertIn(",Title,,0,0,0,,TIÊU ĐỀ", with_number)
        self.assertIn(",Number,,0,0,0,,64 → 164", with_number)
        self.assertNotIn(",Number,", ass_for("TIÊU ĐỀ", None, 1080, 1920).split("[Events]")[1])


if __name__ == "__main__":
    unittest.main()
