# Post-production — from a played session to a video

The mayor plays; the studio records (Phase 2). This is what happens next:

```
journal/<session>.jsonl ──▶ post.brief  ──▶ brief.json
                                               │  Claude writes, by the rules below
                                               ▼
                                   episodes/<id>/script.vi.json ──▶ post.script (validate)
                                               │
                                               ▼
                              voice (TTS) ─▶ subtitles ─▶ edit (ffmpeg) ─▶ human review
```

Everything lives in `post/` (Python, stdlib only for the brief and the script) and
`episodes/<id>/`. Episode folders hold the authored script (committed) and generated
files (brief, audio, renders — git-ignored).

## 1. The brief

```powershell
python -m post.brief journal/20261115-011235.jsonl -o episodes/e001-ngay-1/brief.json
```

One timeline of what happened: every note (`decision`, `mistake`, `milestone`,
`observation`, `plan`), marker, build and office action, each with its journal `seq`, the
recording time `t` (seconds into the video file, `null` when not recording), the game date,
population and cash. Plus the recording's duration and chapters (OBS's own `Start` /
`Dummy` removed), the timelapse frames taken during the session, and before/after stats.

## 2. Writing the script (Claude)

Read the brief, then write `episodes/<id>/script.vi.json` (format in `post/script.py`).

**Truth.** Every line is grounded in the journal: beats cite the `seq` numbers they use
in `source`, and the numbers on screen are the brief's numbers. Do not invent drama the
journal does not contain; the real mistakes are better than invented ones.

**Shape (Short, 9:16, ≤ 60 s).**
- **Hook — 3 seconds, ≤ 15 syllables.** The stakes, not the setup: "Thị trấn của tôi đang
  chết dần." Visual: the most telling frame. An `onScreen` number helps (`144 → 75`).
- **Beats — 4 to 7.** Problem → attempt → what went wrong → what was learned → fix. A beat
  is one idea, one visual, 1–2 short sentences. Keep the mistakes: they are the story.
- **Punchline — the turn or the lesson**, in a line a viewer would repeat. End on the
  result (`onScreen` the after-number) or on the next problem as a cliffhanger.

**Voice.** Vietnamese, spoken, first person: the AI mayor (`tôi`) telling its own day.
Short words, short sentences, no English jargon where a Vietnamese word exists; numbers
written as digits (the TTS reads them). Calm, a little dry; the humour comes from the
facts. About 5 syllables per second: the validator estimates time from that.

**Visuals.** `clip` ranges come from the brief's `t` values and chapters (a beat's clip
starts at or just before its `t`); speed up long, slow stretches with `speed` (2–8).
`timelapse` shows growth over game time; `still` for a saved screenshot.

**Variation (platform policy).** YouTube demonetises mass-produced, templated AI content.
Each episode must tell a different story from its own journal: vary the hook type
(number, question, confession), the order of beats and the closing line. Never reuse a
script as a template. Every video gets a human review before publishing and is labelled
as AI-made.

## 3. Voice (VieNeu-TTS)

```powershell
D:\tools\venv-post\Scripts\python.exe -X utf8 -m post.voice episodes/e001-ngay-1/script.vi.json
```

Local, offline, **Apache-2.0 including its preset voices** (commercial and monetised use
allowed; never clone a real person's voice without permission). `pip install vieneu` in
the venv `D:\tools\venv-post`; on CPU it runs through ONNX, no torch (~2× real time on
this machine). Set `HF_HOME=D:\tools\hf-cache` so the model stays off C:. One WAV per
line (48 kHz mono) and `build/voice/manifest.json` with the exact durations; unchanged
lines are not regenerated. The default narrator is **Thiện Minh** (male, northern,
storytelling); a script may set `"voice"` to any preset (`Vieneu().list_preset_voices()`).
It speaks ~4.4 syllables/s, a little slower than the validator's 5.0 estimate, so leave
the Short some margin under 60 s.

## 4. Subtitles (word timing)

```powershell
D:\tools\venv-post\Scripts\python.exe -X utf8 -m post.subtitles episodes/e001-ngay-1/script.vi.json
```

faster-whisper (`small`, CPU int8; `pip install faster-whisper` in the same venv)
transcribes each voiced line with word timestamps, primed with the line's own text. The
recognised words are matched to the script's words; matched words take the recognised
times and the rest (numbers such as `1,2` are often split) are interpolated between their
neighbours. The text on screen is always the script's. The match share per line is
printed: below 60 % the TTS probably misread the line (the priming makes this a soft
check, not a proof). Output: `build/subtitles/words.json` (line-local word times).

`post.subtitles.to_ass()` turns placed lines into ASS for ffmpeg's `ass` filter (libass):
chunks of up to 4 words / 22 characters, broken after punctuation, bold white with a
black outline in the lower third of a 9:16 frame, and the word being spoken in yellow.
Arial renders every Vietnamese diacritic.

## 5. Validate

```powershell
python -m post.script episodes/e001-ngay-1/script.vi.json --brief episodes/e001-ngay-1/brief.json
```

Checks the shape (hook first, punchline last), the hook's 3-second budget, the Short's
60-second budget (voice estimate + 0.6 s per line), clip ranges inside the recording,
chapters and timelapse frames that exist, and that every beat cites journal entries that
exist. Prints the estimated timing per line. Fix errors by rewriting, not by squeezing
the numbers.

## 6. Edit (ffmpeg)

```powershell
python -X utf8 -m post.edit episodes/e001-ngay-1/script.vi.json [--music bed.mp3] [--game-audio 0.1]
```

The voice drives the timeline: a line lasts 0.15 s + its voice + 0.45 s, and its visual
is fitted to that — a `clip` is trimmed, or slowed when it is too short at its `speed`
(the speed ramp is the clip's `speed`); a `chapter` is the recording chapter of that
title (cut by marker); a `timelapse` spreads its frames over the line; a `still` holds.

- **Picture:** 1920×1080 at 30 fps, NVENC H.264. The 9:16 Short crops the centre of the
  frame *above* the game's bottom toolbar and scales it to 1080×1920.
- **Text:** the word-timed subtitles (chunks sized to the frame width) and the lines'
  `onScreen` cards (large, yellow, upper part of the frame), burned in with libass.
- **Sound:** the voice; a music bed ducked under it (`--music`, looped; by default a
  quiet synthesised A-minor pad, so nothing needs licensing); game audio off by default
  (the game's soundtrack is not ours); loudness-normalised to −14 LUFS, −1.5 dBTP — the
  level YouTube and TikTok play at.

Outputs `build/render/<id>-16x9.mp4` (the YouTube master) and `<id>-9x16.mp4` (Shorts,
TikTok). The e001 pilot renders in ~20 s.

**Film for the edit.** Footage recorded with the game UI on shows the chirper, tooltips
and the bridge console. Hide the UI (`city_camera hideUi:true`) for any stretch that
might become a clip, and film B-roll with `studio_camera_move`.
