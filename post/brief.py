"""Build a story brief from a session's decision journal.

    python -m post.brief journal/20261115-011235.jsonl -o episodes/<id>/brief.json

The brief is what the script writer (Claude) reads: every note, marker, build and office
action on one timeline, with the recording timecode (seconds into the video file), the
game date and the city's numbers; the recording's chapters; the timelapse frames taken
during the session; and the before/after stats. It is deterministic and has no opinions:
the story is written from it, not by it.
"""
import argparse
import glob
import json
import os
from datetime import datetime

from .common import probe, read_jsonl, timecode_seconds
from . import series as series_state

NOTE_KINDS = {"decision", "mistake", "milestone", "observation", "plan", "note"}


def _summarise_action(entry):
    args = entry.get("args") or {}
    result = entry.get("result") if isinstance(entry.get("result"), dict) else {}
    summary = {"tool": entry.get("tool", ""), "ok": result.get("ok")}
    for key in ("cost", "errors", "error", "buildingPrefab", "position"):
        if result.get(key) not in (None, [], ""):
            summary[key] = result[key]
    for key in ("prefab", "action", "speed", "paused", "name", "id", "active", "rate", "service"):
        if key in args:
            summary.setdefault("args", {})[key] = args[key]
    return summary


def build_brief(journal_path, recording=None, timelapse_root=None):
    entries = read_jsonl(journal_path)
    if not entries:
        raise ValueError(f"{journal_path} is empty")

    recordings = [e["result"]["outputPath"] for e in entries
                  if e.get("tool") == "studio_record" and isinstance(e.get("result"), dict)
                  and e["result"].get("outputPath")]
    recording = recording or (recordings[-1] if recordings else None)

    beats = []
    for e in entries:
        kind = e.get("kind")
        rec = e.get("recording") or {}
        game = e.get("game") or {}
        beat = {
            "seq": e.get("seq"),
            "kind": kind,
            "t": timecode_seconds(rec.get("timecode")) if rec.get("active") else None,
            "gameDate": game.get("date"),
            "population": game.get("population"),
            "cash": game.get("cash"),
        }
        if kind in NOTE_KINDS or kind == "marker":
            beat["text"] = e.get("text")
        elif kind in ("build", "office"):
            beat["action"] = _summarise_action(e)
        else:
            continue  # studio plumbing (record start/stop, timelapse) is not story
        beats.append(beat)

    games = [e["game"] for e in entries if e.get("game")]
    stats = {}
    if games:
        pops = [g["population"] for g in games if g.get("population") is not None]
        actions = [b["action"] for b in beats if "action" in b]
        stats = {
            "start": games[0],
            "end": games[-1],
            "populationMin": min(pops) if pops else None,
            "populationMax": max(pops) if pops else None,
            "builds": sum(1 for b in beats if b["kind"] == "build" and b["action"].get("ok")),
            "refusedOrFailed": sum(1 for a in actions if a.get("ok") is False),
            "spent": sum(b["action"].get("cost", 0) for b in beats if b["kind"] == "build" and b["action"].get("ok")),
        }

    brief = {
        "session": entries[0].get("session"),
        "journal": os.path.abspath(journal_path),
        "wallStart": entries[0].get("wall"),
        "wallEnd": entries[-1].get("wall"),
        "recording": None,
        "stats": stats,
        "beats": beats,
        "timelapse": _timelapse(entries, timelapse_root, recording),
        # The story so far (episodes/series.json): previous lessons and open threads.
        "series": series_state.context(series_state.load()),
    }
    if recording and os.path.exists(recording):
        duration, chapters = probe(recording)
        brief["recording"] = {"path": recording, "duration": duration, "chapters": chapters}
    elif recording:
        brief["recording"] = {"path": recording, "missing": True}
    return brief


def _timelapse(entries, root, recording):
    """Timelapse sessions whose frames were taken during this journal session."""
    if root is None and recording:
        root = os.path.join(os.path.dirname(recording), "timelapse")
    if not root or not os.path.isdir(root):
        return []
    start = datetime.fromisoformat(entries[0]["wall"])
    end = datetime.fromisoformat(entries[-1]["wall"])
    found = []
    for manifest in sorted(glob.glob(os.path.join(root, "*", "frames.jsonl"))):
        frames = [f for f in read_jsonl(manifest)
                  if start <= datetime.fromisoformat(f["wall"]) <= end and os.path.exists(f["file"])]
        if frames:
            viewpoints = sorted({f["viewpoint"] for f in frames})
            found.append({
                "folder": os.path.dirname(manifest),
                "viewpoints": {v: [f["file"] for f in frames if f["viewpoint"] == v] for v in viewpoints},
                "gameFrom": frames[0]["gameTime"],
                "gameTo": frames[-1]["gameTime"],
            })
    return found


def main(argv=None):
    parser = argparse.ArgumentParser(description="Build a story brief from a decision journal.")
    parser.add_argument("journal")
    parser.add_argument("-o", "--output", required=True)
    parser.add_argument("--recording", help="override the recording found in the journal")
    parser.add_argument("--timelapse-root")
    args = parser.parse_args(argv)
    brief = build_brief(args.journal, args.recording, args.timelapse_root)
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    with open(args.output, "w", encoding="utf-8", newline="\n") as f:
        json.dump(brief, f, ensure_ascii=False, indent=2)
        f.write("\n")
    frames = sum(len(v) for t in brief["timelapse"] for v in t["viewpoints"].values())
    print(f"{args.output}: {len(brief['beats'])} beats, "
          f"recording {(brief['recording'] or {}).get('duration', '?')} s, {frames} timelapse frames")


if __name__ == "__main__":
    main()
