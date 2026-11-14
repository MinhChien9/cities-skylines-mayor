"""The edit: script + voice + word timing -> a 16:9 master and a 9:16 Short.

    python -m post.edit episodes/e001-ngay-1/script.vi.json [--music bed.mp3] [--game-audio 0.1]

The voice drives the timeline: each line lasts its voice-over plus a breath, and its
visual is fitted to that length (a clip is trimmed or retimed - the speed ramp - a
timelapse gets the frame rate that spans the line, a still holds). Visual types: clip
{in, out, speed?}, chapter {title} (a recording chapter, i.e. cut by marker), timelapse
{viewpoint, folder?}, still {file}.

Audio: the voice lines, a music bed ducked under the voice (a file, or by default a
quiet synthesised pad - no licensing questions), optional game audio (off by default:
the game's soundtrack is not ours), loudness-normalised to -14 LUFS / -1.5 dBTP.

Video: 1920x1080 30 fps master; the 9:16 version crops the centre of the frame above the
game's bottom toolbar and scales to 1080x1920. Both get burned-in subtitles (ASS, the
spoken word highlighted) and the lines' onScreen cards. NVENC H.264.
Outputs: episodes/<id>/build/render/<id>-16x9.mp4 and <id>-9x16.mp4.
"""
import argparse
import json
import os
import subprocess

from .common import probe, tool
from .script import validate
from .subtitles import to_ass

FPS = 30
BREATH = 0.45          # silence after each line
LEAD_IN = 0.15         # silence before the first word
MASTER = (1920, 1080)
VERTICAL = (1080, 1920)
TOOLBAR = 0.075        # bottom share of the game frame taken by its toolbar


def run(args):
    subprocess.run([tool("ffmpeg"), "-hide_banner", "-v", "error", "-y"] + args, check=True)


def resolve_visual(visual, brief):
    """chapter -> clip using the brief's recording chapters."""
    if visual.get("type") != "chapter":
        return visual
    for chapter in (brief.get("recording") or {}).get("chapters", []):
        if chapter["title"] == visual.get("title"):
            return {"type": "clip", "in": chapter["start"], "out": chapter["end"], "speed": visual.get("speed", 1.0)}
    raise ValueError(f"no chapter titled {visual.get('title')!r} in the recording")


def plan_timeline(script, voice, words):
    """Line start/length on the final timeline."""
    timeline, t = [], 0.0
    for line, v, w in zip(script["lines"], voice["lines"], words["lines"]):
        length = LEAD_IN + v["seconds"] + BREATH
        timeline.append({"start": round(t, 3), "length": round(length, 3), "voice": v["file"],
                         "words": [dict(x, start=x["start"] + LEAD_IN, end=x["end"] + LEAD_IN) for x in w["words"]],
                         "onScreen": line.get("onScreen"), "visual": line["visual"]})
        t += length
    return timeline


def clip_window(visual, length):
    """(start, source seconds, playback speed) so a clip exactly fills `length`."""
    available = visual["out"] - visual["in"]
    speed = float(visual.get("speed", 1.0))
    if available / speed < length:          # too short at that speed: slow it to fit
        speed = max(available / length, 0.25)
    return visual["in"], min(available, length * speed), speed


def render_segment(i, item, brief, recording, folder):
    out = os.path.join(folder, f"seg{i:02d}.mp4")
    length = item["length"]
    visual = resolve_visual(item["visual"], brief)
    scale = f"scale={MASTER[0]}:{MASTER[1]}:force_original_aspect_ratio=increase,crop={MASTER[0]}:{MASTER[1]},setsar=1"
    encode = ["-an", "-c:v", "h264_nvenc", "-preset", "p5", "-cq", "19", "-pix_fmt", "yuv420p", "-r", str(FPS)]
    if visual["type"] == "clip":
        start, source, speed = clip_window(visual, length)
        run(["-ss", f"{start:.3f}", "-t", f"{source:.3f}", "-i", recording,
             "-vf", f"setpts=PTS/{speed:.4f},fps={FPS},{scale},tpad=stop_mode=clone:stop_duration=1",
             "-t", f"{length:.3f}"] + encode + [out])
    elif visual["type"] == "timelapse":
        frames = timelapse_frames(visual, brief)
        listing = os.path.join(folder, f"seg{i:02d}.txt")
        per = length / len(frames)
        with open(listing, "w", encoding="utf-8") as f:
            for frame in frames:
                f.write(f"file '{frame}'\nduration {per:.4f}\n")
            f.write(f"file '{frames[-1]}'\n")
        run(["-f", "concat", "-safe", "0", "-i", listing, "-vf", f"fps={FPS},{scale}",
             "-t", f"{length:.3f}"] + encode + [out])
    elif visual["type"] == "still":
        run(["-loop", "1", "-i", visual["file"], "-vf", f"fps={FPS},{scale}", "-t", f"{length:.3f}"] + encode + [out])
    else:
        raise ValueError(f"unknown visual {visual}")
    return out


def timelapse_frames(visual, brief):
    for session in brief.get("timelapse", []):
        if visual.get("folder") and os.path.normpath(session["folder"]) != os.path.normpath(visual["folder"]):
            continue
        frames = session["viewpoints"].get(visual["viewpoint"])
        if frames:
            return [f.replace("\\", "/") for f in frames]
    raise ValueError(f"no timelapse frames for {visual}")


def render_audio(timeline, folder, total, music=None, recording=None, game_audio=0.0):
    voice = os.path.join(folder, "voice.wav")
    listing = os.path.join(folder, "voice.txt")
    with open(listing, "w", encoding="utf-8") as f:
        for i, item in enumerate(timeline):
            padded = os.path.join(folder, f"voice{i:02d}.wav")
            run(["-i", item["voice"], "-af", f"adelay={int(LEAD_IN * 1000)}:all=1,apad=whole_dur={item['length']:.3f}",
                 "-ar", "48000", "-ac", "2", "-t", f"{item['length']:.3f}", padded])
            f.write(f"file '{padded.replace(chr(92), '/')}'\n")
    run(["-f", "concat", "-safe", "0", "-i", listing, "-c:a", "pcm_s16le", voice])

    inputs = ["-i", voice]
    if music:
        inputs += ["-stream_loop", "-1", "-i", music]
    else:
        # A quiet A-minor pad with a slow swell: royalty-free because it is generated here.
        pad = ("0.06*sin(2*PI*110*t)+0.05*sin(2*PI*164.81*t)+0.045*sin(2*PI*220*t)"
               "+0.035*sin(2*PI*261.63*t)+0.03*sin(2*PI*329.63*t)")
        inputs += ["-f", "lavfi", "-i", f"aevalsrc='({pad})*(0.75+0.25*sin(2*PI*0.08*t))':s=48000:c=stereo:d={total + 1:.2f}"]
    filters = [
        f"[1:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0.35,atrim=0:{total:.3f},"
        f"afade=t=in:d=1.5,afade=t=out:st={max(0.0, total - 2.5):.3f}:d=2.5[bed]",
        "[0:a]asplit=2[v][key]",
        "[bed][key]sidechaincompress=threshold=0.02:ratio=8:attack=20:release=400[ducked]",
    ]
    mix = "[v][ducked]"
    count = 2
    if recording and game_audio > 0:
        inputs += ["-i", recording]
        filters.append(f"[2:a]volume={game_audio},atrim=0:{total:.3f}[game]")
        mix += "[game]"
        count = 3
    filters.append(f"{mix}amix=inputs={count}:normalize=0,loudnorm=I=-14:TP=-1.5:LRA=11,"
                   f"aresample=48000,atrim=0:{total:.3f}[out]")
    mixed = os.path.join(folder, "mix.wav")
    run(inputs + ["-filter_complex", ";".join(filters), "-map", "[out]", "-c:a", "pcm_s16le", mixed])
    return mixed


def cards_for(timeline):
    return [{"start": item["start"] + 0.1, "end": item["start"] + item["length"] - 0.1, "text": item["onScreen"]}
            for item in timeline if item.get("onScreen")]


def render(script_path, music=None, game_audio=0.0):
    episode = os.path.dirname(os.path.abspath(script_path))
    build = os.path.join(episode, "build")
    load = lambda p: json.load(open(p, encoding="utf-8"))
    script = load(script_path)
    brief = load(os.path.join(episode, "brief.json"))
    voice = load(os.path.join(build, "voice", "manifest.json"))
    words = load(os.path.join(build, "subtitles", "words.json"))
    errors, _, _ = validate(script, brief)
    if errors:
        raise SystemExit("the script does not validate: " + "; ".join(errors))
    recording = script.get("recording") or brief["recording"]["path"]

    work = os.path.join(build, "render", "work")
    os.makedirs(work, exist_ok=True)
    timeline = plan_timeline(script, voice, words)
    total = sum(item["length"] for item in timeline)

    segments = [render_segment(i, item, brief, recording, work) for i, item in enumerate(timeline)]
    listing = os.path.join(work, "segments.txt")
    with open(listing, "w", encoding="utf-8") as f:
        f.writelines(f"file '{s.replace(chr(92), '/')}'\n" for s in segments)
    picture = os.path.join(work, "picture.mp4")
    run(["-f", "concat", "-safe", "0", "-i", listing, "-c", "copy", picture])
    audio = render_audio(timeline, work, total, music, recording, game_audio)

    placed = [{"start": item["start"], "words": item["words"]} for item in timeline]
    outputs = {}
    for name, (w, h) in (("16x9", MASTER), ("9x16", VERTICAL)):
        ass = os.path.join(work, f"subs-{name}.ass")
        with open(ass, "w", encoding="utf-8") as f:
            f.write(to_ass(placed, w, h, cards=cards_for(timeline)))
        if name == "16x9":
            frame = ""
        else:
            # Centre crop above the toolbar, scaled up to 1080x1920.
            ch = int(MASTER[1] * (1 - TOOLBAR)) // 2 * 2
            cw = int(ch * 9 / 16) // 2 * 2
            frame = f"crop={cw}:{ch}:{(MASTER[0] - cw) // 2}:0,scale={w}:{h}:flags=lanczos,"
        ass_arg = ass.replace("\\", "/").replace(":", "\\:")
        out = os.path.join(build, "render", f"{script['id']}-{name}.mp4")
        run(["-i", picture, "-i", audio, "-map", "0:v", "-map", "1:a",
             "-vf", f"{frame}ass='{ass_arg}'", "-c:v", "h264_nvenc", "-preset", "p5", "-cq", "20",
             "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart",
             "-t", f"{total:.3f}", out])
        outputs[name] = out
    return outputs, total


def main(argv=None):
    parser = argparse.ArgumentParser(description="Render the 16:9 master and the 9:16 Short.")
    parser.add_argument("script")
    parser.add_argument("--music", help="music bed file (looped, ducked); default: a generated pad")
    parser.add_argument("--game-audio", type=float, default=0.0, help="game sound level (0 = off)")
    args = parser.parse_args(argv)
    outputs, total = render(args.script, args.music, args.game_audio)
    for name, path in outputs.items():
        duration, _ = probe(path)
        print(f"{name}: {path} ({duration:.1f} s)")


if __name__ == "__main__":
    main()
