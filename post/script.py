"""The video script format, and its validator.

    python -m post.script episodes/e001-ngay-1/script.vi.json --brief episodes/e001-ngay-1/brief.json

A script is written by Claude from a brief (see docs/13-post-production.md) and is the
single input of the later stages: voice (each line's `vo`), subtitles (the same text,
timed) and the edit (each line's `visual`). Shape:

    {
      "id": "e001-ngay-1",
      "title": "AI làm thị trưởng — ngày 1",
      "format": "short",                 # short = 9:16, <= 60 s; episode = 16:9
      "language": "vi",
      "recording": "D:/cs1-mayor-recordings/....mp4",
      "lines": [
        {"role": "hook", "vo": "...", "onScreen": "71 → 166",
         "visual": {"type": "clip", "in": 230.0, "out": 233.0}, "source": [5]},
        {"role": "beat", ...},
        {"role": "punchline", ...}
      ]
    }

visual types: clip {in, out, speed?} from the recording; chapter {title} (a recording
chapter: cut by marker); timelapse {viewpoint, folder?} from the brief's frames; still
{file}. The edit fits every visual to its line's voice-over. `source` lists the journal entries (seq)
the line is based on: every claim must trace back to something that happened.
"""
import argparse
import json
import sys

from .common import syllables

SPEECH_RATE = 5.0         # Vietnamese syllables per second, a calm narrator
HOOK_MAX_SECONDS = 3.0
SHORT_MAX_SECONDS = 60.0
FORMATS = {"short", "episode"}
VISUALS = {"clip", "chapter", "timelapse", "still"}
LINE_PAD = 0.6           # lead-in + breath the edit adds around each voiced line


def vo_seconds(text):
    return syllables(text) / SPEECH_RATE


def visual_seconds(visual, brief=None):
    kind = visual.get("type")
    if kind == "clip":
        return max(0.0, (visual["out"] - visual["in"]) / float(visual.get("speed", 1.0)))
    if kind == "timelapse":
        frames = _timelapse_frames(visual, brief)
        return len(frames) / float(visual.get("fps", 8)) if frames else 0.0
    return 0.0  # a still lasts as long as its voice-over


def line_seconds(line, brief=None):
    """The edit is voice-driven: a line lasts its voice-over plus a pad, and the visual
    is trimmed or retimed to fit (post.edit), so the visual does not lengthen it."""
    return vo_seconds(line.get("vo", "")) + LINE_PAD


def _timelapse_frames(visual, brief):
    if not brief:
        return []
    for session in brief.get("timelapse", []):
        if visual.get("folder") and session["folder"] != visual["folder"]:
            continue
        frames = session["viewpoints"].get(visual.get("viewpoint"))
        if frames:
            return frames
    return []


def validate(script, brief=None):
    """Returns (errors, warnings, timings)."""
    errors, warnings, timings = [], [], []
    for key in ("id", "title", "format", "language", "lines"):
        if not script.get(key):
            errors.append(f"missing '{key}'")
    if errors:
        return errors, warnings, timings
    if script["format"] not in FORMATS:
        errors.append(f"format must be one of {sorted(FORMATS)}")
    if script["language"] != "vi":
        warnings.append("language is not 'vi'")

    lines = script["lines"]
    roles = [l.get("role") for l in lines]
    if roles[0] != "hook":
        errors.append("the first line must be the hook")
    if roles[-1] != "punchline":
        errors.append("the last line must be the punchline")
    if any(r not in ("hook", "beat", "punchline") for r in roles):
        errors.append("roles are hook, beat, punchline")
    if len(lines) < 3:
        errors.append("a story needs a hook, at least one beat and a punchline")

    seqs = {b["seq"] for b in brief.get("beats", [])} if brief else None
    duration = (brief or {}).get("recording", {}) or {}
    duration = duration.get("duration")

    for i, line in enumerate(lines):
        where = f"line {i + 1} ({line.get('role')})"
        if not (line.get("vo") or "").strip():
            errors.append(f"{where}: empty vo")
        visual = line.get("visual") or {}
        if visual.get("type") not in VISUALS:
            errors.append(f"{where}: visual.type must be one of {sorted(VISUALS)}")
        elif visual["type"] == "clip":
            if not (0 <= visual.get("in", -1) < visual.get("out", -1)):
                errors.append(f"{where}: clip needs 0 <= in < out")
            elif duration and visual["out"] > duration + 0.01:
                errors.append(f"{where}: clip ends at {visual['out']} s, the recording is {duration:.1f} s")
        elif visual["type"] == "chapter" and brief is not None and visual.get("title") not in {
                c["title"] for c in ((brief.get("recording") or {}).get("chapters") or [])}:
            errors.append(f"{where}: no recording chapter titled {visual.get('title')!r}")
        elif visual["type"] == "timelapse" and brief is not None and not _timelapse_frames(visual, brief):
            errors.append(f"{where}: no timelapse frames for viewpoint '{visual.get('viewpoint')}'")
        elif visual["type"] == "still" and not visual.get("file"):
            errors.append(f"{where}: still needs a file")

        source = line.get("source") or []
        if line.get("role") == "beat" and not source:
            errors.append(f"{where}: a beat must cite its journal entries in 'source'")
        if seqs is not None:
            unknown = [s for s in source if s not in seqs]
            if unknown:
                errors.append(f"{where}: source {unknown} not in the brief")

        seconds = line_seconds(line, brief)
        timings.append({"line": i + 1, "role": line.get("role"), "syllables": syllables(line.get("vo", "")),
                        "voSeconds": round(vo_seconds(line.get("vo", "")), 2), "seconds": round(seconds, 2)})
        if line.get("role") == "hook" and vo_seconds(line.get("vo", "")) > HOOK_MAX_SECONDS:
            errors.append(f"{where}: the hook must be spoken in {HOOK_MAX_SECONDS:.0f} s "
                          f"(~{int(HOOK_MAX_SECONDS * SPEECH_RATE)} syllables), it has {syllables(line['vo'])}")

    total = sum(t["seconds"] for t in timings)
    if script["format"] == "short" and total > SHORT_MAX_SECONDS:
        errors.append(f"a short must be <= {SHORT_MAX_SECONDS:.0f} s, this one is ~{total:.0f} s")
    if script["format"] == "short" and total < 20:
        warnings.append(f"a short of ~{total:.0f} s is very short")
    return errors, warnings, timings


def main(argv=None):
    parser = argparse.ArgumentParser(description="Validate a video script.")
    parser.add_argument("script")
    parser.add_argument("--brief")
    args = parser.parse_args(argv)
    with open(args.script, encoding="utf-8") as f:
        script = json.load(f)
    brief = None
    if args.brief:
        with open(args.brief, encoding="utf-8") as f:
            brief = json.load(f)
    errors, warnings, timings = validate(script, brief)
    for t in timings:
        print(f"  {t['line']:>2} {t['role']:<9} {t['syllables']:>3} syl  vo {t['voSeconds']:>5.1f} s  line {t['seconds']:>5.1f} s")
    print(f"  total ~{sum(t['seconds'] for t in timings):.1f} s")
    for w in warnings:
        print("warning:", w)
    for e in errors:
        print("error:", e)
    sys.exit(1 if errors else 0)


if __name__ == "__main__":
    main()
