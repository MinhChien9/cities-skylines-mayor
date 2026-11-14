"""Subtitles: word timing for every voiced line, and ASS subtitles to burn in.

    D:\\tools\\venv-post\\Scripts\\python.exe -X utf8 -m post.subtitles episodes/e001-ngay-1/script.vi.json

Alignment: faster-whisper (CTranslate2, no torch) transcribes each line's WAV with word
timestamps; the recognised words are matched to the script's own words (difflib), matched
script words take the recognised times, the rest are interpolated between their matched
neighbours. The subtitle text is always the script's, never the transcript. The share of
matched words is reported per line: a low score means the TTS misread the line.

Writes episodes/<id>/build/subtitles/words.json (line-local word times). The edit
(post.edit) places the lines on the final timeline and calls to_ass() for the burn-in.
"""
import argparse
import difflib
import json
import os
import re
import unicodedata

MIN_MATCH = 0.6


def normalise(token):
    """Lowercase, NFC, no punctuation: the form two spellings of a word are compared in."""
    token = unicodedata.normalize("NFC", token.lower())
    return re.sub(r"[^\w]", "", token)


def display_tokens(text):
    """The script's words as shown on screen (punctuation stays attached)."""
    return text.split()


def align(script_text, recognised, duration):
    """Times for each script word from recognised words [(word, start, end)].

    Returns (words, score): words = [{"text", "start", "end"}], score = matched share.
    """
    tokens = display_tokens(script_text)
    if not tokens:
        return [], 0.0
    ours = [normalise(t) for t in tokens]
    theirs = [normalise(w) for w, _, _ in recognised]
    times = [None] * len(tokens)
    matcher = difflib.SequenceMatcher(a=ours, b=theirs, autojunk=False)
    for block in matcher.get_matching_blocks():
        for k in range(block.size):
            _, start, end = recognised[block.b + k]
            times[block.a + k] = (start, end)
    matched = sum(1 for t in times if t is not None)

    # Interpolate the gaps by character weight between the neighbouring anchors.
    first_start = next((t[0] for t in times if t), 0.0)
    last_end = next((t[1] for t in reversed(times) if t), duration)
    i = 0
    while i < len(tokens):
        if times[i] is not None:
            i += 1
            continue
        j = i
        while j < len(tokens) and times[j] is None:
            j += 1
        left = times[i - 1][1] if i > 0 else (0.0 if j < len(tokens) else first_start)
        right = times[j][0] if j < len(tokens) else max(last_end, left)
        if i == 0 and j < len(tokens):
            left = max(0.0, min(first_start, right) - 0.3 * (j - i))
        if j == len(tokens) and i > 0:
            right = max(duration - 0.05, left)
        weights = [max(1, len(ours[k])) for k in range(i, j)]
        span = max(0.0, right - left)
        cursor = left
        for k, w in zip(range(i, j), weights):
            step = span * w / sum(weights)
            times[k] = (cursor, cursor + step)
            cursor += step
        i = j

    words = [{"text": tok, "start": round(s, 3), "end": round(max(e, s + 0.05), 3)}
             for tok, (s, e) in zip(tokens, times)]
    return words, matched / len(tokens)


def chunks(words, max_words=4, max_chars=22):
    """Group a line's words into short on-screen chunks, breaking after punctuation."""
    out, current = [], []
    for w in words:
        if current and (len(current) >= max_words
                        or len(" ".join(x["text"] for x in current + [w])) > max_chars):
            out.append(current)
            current = []
        current.append(w)
        if w["text"][-1:] in ".,!?:;…":
            out.append(current)
            current = []
    if current:
        out.append(current)
    return out


def _ass_time(seconds):
    cs = int(round(max(0.0, seconds) * 100))
    return f"{cs // 360000}:{cs // 6000 % 60:02d}:{cs // 100 % 60:02d}.{cs % 100:02d}"


def to_ass(lines, width, height, font="Arial", highlight="&H0000D7FF"):
    """ASS subtitles from placed lines: [{"start": s, "words": [...]}] (word times line-local).

    One event per chunk, shown until the next chunk starts; the word being spoken is
    coloured (karaoke-style override tags), the others white with a black outline.
    """
    size = int(height * (0.058 if height > width else 0.065))
    margin_v = int(height * (0.30 if height > width else 0.08))
    header = (
        "[Script Info]\nScriptType: v4.00+\nWrapStyle: 0\nScaledBorderAndShadow: yes\n"
        f"PlayResX: {width}\nPlayResY: {height}\n\n"
        "[V4+ Styles]\n"
        "Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, "
        "Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, "
        "Alignment, MarginL, MarginR, MarginV, Encoding\n"
        f"Style: Default,{font},{size},&H00FFFFFF,&H00FFFFFF,&H00000000,&H64000000,"
        f"-1,0,0,0,100,100,0,0,1,{max(2, size // 12)},{max(1, size // 30)},2,"
        f"{int(width * 0.06)},{int(width * 0.06)},{margin_v},1\n\n"
        "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\n"
    )
    events = []
    for line in lines:
        groups = chunks(line["words"])
        for g, group in enumerate(groups):
            start = line["start"] + group[0]["start"]
            end = line["start"] + (groups[g + 1][0]["start"] if g + 1 < len(groups) else group[-1]["end"] + 0.25)
            # Sub-events per spoken word, so the highlight follows the voice.
            for k, word in enumerate(group):
                w_start = line["start"] + word["start"] if k else start
                w_end = line["start"] + group[k + 1]["start"] if k + 1 < len(group) else end
                text = " ".join(
                    (f"{{\\c{highlight}}}{_escape(x['text'])}{{\\c&H00FFFFFF&}}" if i == k else _escape(x["text"]))
                    for i, x in enumerate(group))
                events.append(f"Dialogue: 0,{_ass_time(w_start)},{_ass_time(w_end)},Default,,0,0,0,,{text}")
    return header + "\n".join(events) + "\n"


def _escape(text):
    return text.replace("{", "(").replace("}", ")").replace("\\", "/")


def transcribe_lines(manifest, model_size="small"):
    from faster_whisper import WhisperModel  # the venv only: tests do not need it
    model = WhisperModel(model_size, device="cpu", compute_type="int8")
    results = []
    for line in manifest["lines"]:
        segments, _ = model.transcribe(line["file"], language="vi", word_timestamps=True,
                                       initial_prompt=line["text"], beam_size=5, vad_filter=False)
        recognised = [(w.word.strip(), w.start, w.end) for s in segments for w in (s.words or [])]
        words, score = align(line["text"], recognised, line["seconds"])
        results.append({"line": line["line"], "text": line["text"], "seconds": line["seconds"],
                        "score": round(score, 3), "heard": " ".join(w for w, _, _ in recognised),
                        "words": words})
        flag = "" if score >= MIN_MATCH else "   <-- check this line: the TTS may have misread it"
        print(f"  {line['line']:02d} match {score:4.0%}  {line['text']}{flag}", flush=True)
    return results


def main(argv=None):
    parser = argparse.ArgumentParser(description="Word timing for every voiced line.")
    parser.add_argument("script")
    parser.add_argument("--model", default="small")
    args = parser.parse_args(argv)
    episode = os.path.dirname(os.path.abspath(args.script))
    with open(os.path.join(episode, "build", "voice", "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)
    results = transcribe_lines(manifest, args.model)
    out_dir = os.path.join(episode, "build", "subtitles")
    os.makedirs(out_dir, exist_ok=True)
    with open(os.path.join(out_dir, "words.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"model": args.model, "lines": results}, f, ensure_ascii=False, indent=2)
        f.write("\n")
    low = [r["line"] for r in results if r["score"] < MIN_MATCH]
    print(f"{len(results)} lines aligned; mean match {sum(r['score'] for r in results) / len(results):.0%}"
          + (f"; check lines {low}" if low else ""))


if __name__ == "__main__":
    main()
