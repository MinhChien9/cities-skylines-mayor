import json
import os
import tempfile
import unittest
import wave

from post.voice import DEFAULT_VOICE, line_key, synthesize


def write_wav(path, seconds):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(48000)
        w.writeframes(b"\x00\x00" * int(48000 * seconds))


class VoiceCacheTests(unittest.TestCase):
    def test_unchanged_lines_are_not_regenerated(self):
        folder = tempfile.mkdtemp()
        script = {"lines": [{"vo": "Một."}, {"vo": "Hai."}]}
        script_path = os.path.join(folder, "script.vi.json")
        with open(script_path, "w", encoding="utf-8") as f:
            json.dump(script, f, ensure_ascii=False)
        out = os.path.join(folder, "build", "voice")
        os.makedirs(out)
        lines = []
        for i, text in enumerate(["Một.", "Hai."], start=1):
            path = os.path.join(out, f"{i:02d}.wav")
            write_wav(path, 0.5 * i)
            lines.append({"line": i, "key": line_key(text, DEFAULT_VOICE), "text": text, "file": path, "seconds": 0.5 * i})
        with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as f:
            json.dump({"lines": lines}, f, ensure_ascii=False)

        # Every line is cached, so the TTS model is never loaded (vieneu is not installed here).
        manifest = synthesize(script_path)

        self.assertEqual([l["seconds"] for l in manifest["lines"]], [0.5, 1.0])
        self.assertEqual(manifest["voice"], DEFAULT_VOICE)

    def test_key_changes_with_text_and_voice(self):
        self.assertNotEqual(line_key("Một.", "A"), line_key("Một!", "A"))
        self.assertNotEqual(line_key("Một.", "A"), line_key("Một.", "B"))


if __name__ == "__main__":
    unittest.main()
