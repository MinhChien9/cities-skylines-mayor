"""Small shared helpers for the post-production scripts (stdlib only)."""
import json
import os
import re
import shutil
import subprocess

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_FFMPEG_DIR = r"D:\tools\ffmpeg\bin"


def tool(name):
    """Path to ffmpeg/ffprobe: $CS1_FFMPEG_DIR, then D:\\tools\\ffmpeg\\bin, then PATH."""
    for folder in (os.environ.get("CS1_FFMPEG_DIR"), DEFAULT_FFMPEG_DIR):
        if folder:
            candidate = os.path.join(folder, name + (".exe" if os.name == "nt" else ""))
            if os.path.exists(candidate):
                return candidate
    found = shutil.which(name)
    if not found:
        raise FileNotFoundError(f"{name} not found; set CS1_FFMPEG_DIR")
    return found


def timecode_seconds(timecode):
    """'00:03:22.416' -> 202.416; None stays None."""
    if not timecode:
        return None
    hours, minutes, seconds = timecode.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def read_jsonl(path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def probe(path):
    """Duration (s) and chapters [{title, start, end}] of a media file via ffprobe."""
    out = subprocess.run(
        [tool("ffprobe"), "-v", "error", "-show_entries", "format=duration",
         "-show_chapters", "-of", "json", path],
        capture_output=True, text=True, encoding="utf-8", check=True).stdout
    data = json.loads(out)
    chapters = [
        {"title": c.get("tags", {}).get("title", ""), "start": float(c["start_time"]), "end": float(c["end_time"])}
        for c in data.get("chapters", [])
        # OBS adds its own "Start" at 0 and a trailing "Dummy"; they are not story beats.
        if c.get("tags", {}).get("title") not in ("Start", "Dummy")
    ]
    return float(data["format"]["duration"]), chapters


_VOWELS = set("aăâeêioôơuưy"
              "áàảãạắằẳẵặấầẩẫậéèẻẽẹếềểễệíìỉĩịóòỏõọốồổỗộớờởỡợúùủũụứừửữựýỳỷỹỵ")


def words(text):
    """The spoken words of a line (Vietnamese: one syllable per word)."""
    return re.findall(r"[^\s.,!?;:…\"“”()\[\]–—-]+", text)


def syllables(text):
    """Vietnamese is written one syllable per space-separated word. Numbers count by
    their digits as a rough reading length (166 -> 'một trăm sáu mươi sáu' = 6)."""
    count = 0
    for w in words(text.lower()):
        digits = sum(ch.isdigit() for ch in w)
        if digits:
            count += max(1, digits * 2)
        elif any(ch in _VOWELS for ch in w):
            count += 1
    return count
