"""The running storyline: episodes/series.json (see docs/15-mayor-persona.md).

    python -m post.series context
    python -m post.series check   episodes/<id>/script.vi.json
    python -m post.series publish episodes/<id>/script.vi.json

series.json: {"episodes": [{id, day, title, lesson, threads}], "threads": [{id, title,
status: open|closed, opened, closed?}]}. A script's optional "threads" block declares what
the episode does to the story: {"advances": [ids], "closes": [ids], "opens": [{id, title}]}.
"""
import argparse
import json
import os
import re
import sys

from .common import REPO

SERIES = os.path.join(REPO, "episodes", "series.json")


def load(path=SERIES):
    if not os.path.exists(path):
        return {"episodes": [], "threads": []}
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def save(series, path=SERIES):
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(series, f, ensure_ascii=False, indent=2)
        f.write("\n")


def context(series, last=3):
    """The story so far, compact enough to put in a brief."""
    return {
        "episodes": [{k: e.get(k) for k in ("id", "day", "title", "lesson")} for e in series["episodes"][-last:]],
        "openThreads": [{k: t.get(k) for k in ("id", "title", "opened")} for t in series["threads"]
                        if t.get("status") == "open"],
        "nextDay": max([e.get("day", 0) for e in series["episodes"]] or [0]) + 1,
    }


def check(script, series):
    """Errors in a script's threads block against the series."""
    errors = []
    block = script.get("threads")
    known = {t["id"]: t for t in series["threads"]}
    if block is None:
        return ["the script has no 'threads' block: every episode advances or closes a thread"]
    opened = [t.get("id") for t in block.get("opens", [])]
    for tid in opened:
        if not tid or not re.fullmatch(r"[a-z0-9-]+", tid):
            errors.append(f"thread id {tid!r} must be lowercase letters, digits and '-'")
        elif tid in known:
            errors.append(f"thread '{tid}' already exists; advance or close it instead")
    for t in block.get("opens", []):
        if not t.get("title"):
            errors.append(f"opened thread '{t.get('id')}' needs a title")
    for verb in ("advances", "closes"):
        for tid in block.get(verb, []):
            if tid in opened:
                continue
            if tid not in known:
                errors.append(f"{verb} unknown thread '{tid}'")
            elif known[tid].get("status") != "open":
                errors.append(f"{verb} thread '{tid}', which is already closed")
    if not block.get("advances") and not block.get("closes") and series["threads"]:
        errors.append("the episode must advance or close at least one thread")
    if any(e["id"] == script.get("id") for e in series["episodes"]):
        errors.append(f"episode {script.get('id')} is already published")
    return errors


def publish(script, series, day=None):
    """Record the episode and apply its thread changes (returns the new series)."""
    errors = check(script, series)
    if errors:
        raise ValueError("; ".join(errors))
    block = script["threads"]
    lesson = next((l["vo"] for l in reversed(script["lines"]) if l.get("role") == "punchline"), None)
    for t in block.get("opens", []):
        series["threads"].append({"id": t["id"], "title": t["title"], "status": "open", "opened": script["id"]})
    for t in series["threads"]:
        if t["id"] in block.get("closes", []):
            t["status"] = "closed"
            t["closed"] = script["id"]
    series["episodes"].append({
        "id": script["id"],
        "day": day if day is not None else context(series)["nextDay"],
        "title": script["title"],
        "lesson": lesson,
        "threads": block,
    })
    return series


def main(argv=None):
    parser = argparse.ArgumentParser(description="The series' running storyline.")
    sub = parser.add_subparsers(dest="command", required=True)
    sub.add_parser("context")
    for name in ("check", "publish"):
        p = sub.add_parser(name)
        p.add_argument("script")
    args = parser.parse_args(argv)
    series = load()
    if args.command == "context":
        print(json.dumps(context(series), ensure_ascii=False, indent=2))
        return
    with open(args.script, encoding="utf-8") as f:
        script = json.load(f)
    if args.command == "check":
        errors = check(script, series)
        for e in errors:
            print("error:", e)
        print("ok" if not errors else "")
        sys.exit(1 if errors else 0)
    save(publish(script, series))
    print(f"published {script['id']}; open threads: "
          f"{[t['id'] for t in series['threads'] if t['status'] == 'open']}")


if __name__ == "__main__":
    main()
