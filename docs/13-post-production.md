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

## 3. Validate

```powershell
python -m post.script episodes/e001-ngay-1/script.vi.json --brief episodes/e001-ngay-1/brief.json
```

Checks the shape (hook first, punchline last), the hook's 3-second budget, the Short's
60-second budget (from syllables and clip lengths), clip ranges inside the recording,
timelapse frames that exist, and that every beat cites journal entries that exist. Prints
the estimated timing per line. Fix errors by rewriting, not by squeezing the numbers.
