"""Voice-over: one WAV per script line with VieNeu-TTS (local, Apache-2.0, 48 kHz).

    D:\\tools\\venv-post\\Scripts\\python.exe -m post.voice episodes/e001-ngay-1/script.vi.json

Runs in the post-production venv (`pip install vieneu`; CPU uses ONNX, no torch). Writes
episodes/<id>/build/voice/NN.wav and manifest.json (text, file, seconds per line).
A line is regenerated only when its text or the voice changed. The voice is the script's
"voice" field, else Thiện Minh (male, northern, storytelling).
"""
import argparse
import hashlib
import json
import os
import wave

DEFAULT_VOICE = "Thiện Minh"
SAMPLE_RATE = 48000


def wav_seconds(path):
    with wave.open(path, "rb") as w:
        return w.getnframes() / float(w.getframerate())


def line_key(text, voice):
    return hashlib.sha256(f"{voice}\n{text}".encode("utf-8")).hexdigest()[:16]


def synthesize(script_path, out_dir=None, force=False):
    with open(script_path, encoding="utf-8") as f:
        script = json.load(f)
    episode = os.path.dirname(os.path.abspath(script_path))
    out_dir = out_dir or os.path.join(episode, "build", "voice")
    os.makedirs(out_dir, exist_ok=True)
    manifest_path = os.path.join(out_dir, "manifest.json")
    previous = {}
    if os.path.exists(manifest_path) and not force:
        with open(manifest_path, encoding="utf-8") as f:
            previous = {l["key"]: l for l in json.load(f)["lines"]}

    voice = script.get("voice") or DEFAULT_VOICE
    tts = None
    lines = []
    for i, line in enumerate(script["lines"], start=1):
        text = line["vo"].strip()
        key = line_key(text, voice)
        path = os.path.join(out_dir, f"{i:02d}.wav")
        if key in previous and os.path.exists(path) and previous[key]["file"] == path:
            lines.append(previous[key])
            continue
        if tts is None:
            from vieneu import Vieneu  # imported late: the validator and tests do not need it
            tts = Vieneu()
        audio = tts.infer(text, voice=voice)
        tts.save(audio, path)
        lines.append({"line": i, "key": key, "text": text, "file": path, "seconds": round(wav_seconds(path), 3)})
        print(f"  {i:02d} {lines[-1]['seconds']:5.2f} s  {text}", flush=True)

    manifest = {"script": os.path.abspath(script_path), "voice": voice, "sampleRate": SAMPLE_RATE, "lines": lines}
    with open(manifest_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
        f.write("\n")
    return manifest


def main(argv=None):
    parser = argparse.ArgumentParser(description="Voice every script line with VieNeu-TTS.")
    parser.add_argument("script")
    parser.add_argument("--out")
    parser.add_argument("--force", action="store_true", help="regenerate every line")
    args = parser.parse_args(argv)
    manifest = synthesize(args.script, args.out, args.force)
    total = sum(l["seconds"] for l in manifest["lines"])
    print(f"{len(manifest['lines'])} lines, {total:.1f} s of voice ({manifest['voice']})")


if __name__ == "__main__":
    main()
