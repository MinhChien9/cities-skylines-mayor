"""Thumbnails: a 1280x720 YouTube thumbnail and a 1080x1920 Shorts cover per episode.

    python -m post.thumbnail episodes/e001-ngay-1/script.vi.json

Reads the script's "thumbnail" block:

    "thumbnail": {"at": 37.5, "title": "THỊ TRẤN ĐANG CHẾT", "number": "64 → 164"}

`at` is a time in the rendered 16:9 master (default: 60 % in), `title` a 2-5 word hook in
capitals, `number` an optional big figure. The frame gets a contrast/saturation lift and a
dark gradient where the text sits; text is rendered with libass (Arial Bold carries every
Vietnamese diacritic). Writes build/render/<id>-thumb-16x9.jpg and <id>-cover-9x16.jpg.
"""
import argparse
import json
import os
import subprocess

from .common import probe, tool

SIZES = {"thumb-16x9": (1280, 720), "cover-9x16": (1080, 1920)}


def ass_for(title, number, width, height, font="Arial"):
    """ASS with the title (white, black outline) and the number (yellow) for one image."""
    vertical = height > width
    title_size = int(width * (0.11 if vertical else 0.085))
    number_size = int(title_size * 1.35)
    title_margin = int(height * (0.10 if vertical else 0.06))
    number_margin = int(height * (0.18 if vertical else 0.10))
    head = (
        "[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\n"
        f"PlayResX: {width}\nPlayResY: {height}\n\n[V4+ Styles]\n"
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
        "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
        "Alignment, MarginL, MarginR, MarginV, Encoding\n"
        f"Style: Title,{font},{title_size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H96000000,"
        f"-1,0,0,0,100,100,0,0,1,{max(4, title_size // 9)},{max(2, title_size // 18)},"
        f"{8 if vertical else 7},{int(width * 0.05)},{int(width * 0.05)},{title_margin},1\n"
        f"Style: Number,{font},{number_size},&H0000D7FF,&H0000D7FF,&H00000000,&H96000000,"
        f"-1,0,0,0,100,100,0,0,1,{max(5, number_size // 9)},{max(2, number_size // 18)},"
        f"{2 if vertical else 1},{int(width * 0.05)},{int(width * 0.05)},{number_margin},1\n\n"
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
    )
    events = [f"Dialogue: 0,0:00:00.00,0:00:10.00,Title,,0,0,0,,{_wrap(title, vertical)}"]
    if number:
        events.append(f"Dialogue: 0,0:00:00.00,0:00:10.00,Number,,0,0,0,,{number}")
    return head + "\n".join(events) + "\n"


def _wrap(title, vertical):
    """Line breaks: '|' in the title where the author wants them, else balanced lines."""
    if "|" in title:
        return "\\N".join(part.strip() for part in title.split("|"))
    words = title.split()
    if len(words) < 3:
        return title
    rows = 3 if vertical and len(words) >= 6 else 2
    per = -(-len(words) // rows)
    return "\\N".join(" ".join(words[i:i + per]) for i in range(0, len(words), per))


def render(script_path):
    with open(script_path, encoding="utf-8") as f:
        script = json.load(f)
    spec = script.get("thumbnail") or {}
    title = spec.get("title") or script["title"]
    episode = os.path.dirname(os.path.abspath(script_path))
    render_dir = os.path.join(episode, "build", "render")
    # The edit's picture track: the master's timeline without burned-in subtitles.
    master = os.path.join(render_dir, "work", "picture.mp4")
    if not os.path.exists(master):
        raise SystemExit(f"render the episode first (post.edit): {master} is missing")
    duration, _ = probe(master)
    at = float(spec.get("at", duration * 0.6))

    outputs = {}
    for name, (w, h) in SIZES.items():
        ass = os.path.join(render_dir, "work", f"{name}.ass")
        os.makedirs(os.path.dirname(ass), exist_ok=True)
        with open(ass, "w", encoding="utf-8") as f:
            f.write(ass_for(title, spec.get("number"), w, h))
        ass_arg = ass.replace("\\", "/").replace(":", "\\:")
        if h > w:  # the cover crops the centre of the master
            frame = f"crop=ih*9/16:ih,scale={w}:{h}:flags=lanczos"
        else:
            frame = f"scale={w}:{h}:flags=lanczos"
        # Stacked translucent bands approximate a gradient behind the text.
        gradient = ",".join(
            [f"drawbox=x=0:y=0:w=iw:h=ih*{h:.2f}:color=black@0.18:t=fill" for h in (0.46, 0.36, 0.26, 0.16)]
            + [f"drawbox=x=0:y=ih*{1 - h:.2f}:w=iw:h=ih*{h:.2f}:color=black@0.16:t=fill" for h in (0.34, 0.24, 0.14)])
        out = os.path.join(render_dir, f"{script['id']}-{name}.jpg")
        subprocess.run([tool("ffmpeg"), "-hide_banner", "-v", "error", "-y", "-ss", f"{at:.2f}", "-i", master,
                        "-frames:v", "1", "-vf",
                        f"{frame},eq=contrast=1.12:saturation=1.25:brightness=0.02,{gradient},ass='{ass_arg}'",
                        "-q:v", "2", out], check=True)
        outputs[name] = out
    return outputs


def main(argv=None):
    parser = argparse.ArgumentParser(description="Render the episode's thumbnail and Shorts cover.")
    parser.add_argument("script")
    args = parser.parse_args(argv)
    for name, path in render(args.script).items():
        print(f"{name}: {path} ({os.path.getsize(path) // 1024} KB)")


if __name__ == "__main__":
    main()
