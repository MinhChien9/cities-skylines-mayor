# The series — episode format, naming, thumbnails, upload

How a played session becomes a published episode, every time. Production steps are in
[`13-post-production.md`](13-post-production.md); this is the format around them.

## Episode format

| | Short | Episode |
|---|---|---|
| Platform | YouTube Shorts, TikTok | YouTube |
| Frame | 1080×1920 | 1920×1080 |
| Length | 30–60 s | 6–12 min |
| Story | one problem → attempts → fix or cliffhanger | a session: 3–5 problems, a turn, a result, the next goal |
| Script `format` | `short` | `episode` |

Every episode is **one real session**: its journal, recording and timelapse. Shorts may
be cut from the same session as an episode, but each tells its own story with its own
script — never a template filled with different numbers (see Variation in 13).

## Naming

- **Episode id:** `eNNN-<slug>` — a three-digit running number and a short Vietnamese slug
  without diacritics: `e001-ngay-1`, `e002-nuoc-sach`. The folder `episodes/<id>/` holds
  `script.vi.json` and `REVIEW.md` (committed) and `build/` (generated).
- **Day number:** "ngày N" counts the mayor's play sessions, not in-game days.
- **Title pattern:** `AI làm thị trưởng — ngày N: <the hook>` (≤ 70 characters). The
  hook changes every episode; the prefix makes the series findable.
- **Files:** `build/render/<id>-16x9.mp4`, `<id>-9x16.mp4`, `<id>-thumb-16x9.jpg`,
  `<id>-cover-9x16.jpg`.

## Thumbnail and cover

```powershell
python -X utf8 -m post.thumbnail episodes/e001-ngay-1/script.vi.json
```

From the script's `thumbnail` block: `at` (seconds into the edit; the frame comes from the
picture track, so there are no subtitles on it), `title` (2–5 words, capitals; `|` forces a
line break) and `number` (the before → after figure). The frame gets a contrast and
colour lift, darkened bands behind the text, white title and yellow number (Arial Bold,
full Vietnamese diacritics). 1280×720 for YouTube (< 2 MB), 1080×1920 cover for Shorts.
Choose a frame that shows the result, and a number the video actually proves.

## Upload checklist

Before an episode goes up — every box, every time:

- [ ] `REVIEW.md` complete: every line fact-checked against the journal, the flagged
      words listened to, the known issues accepted or fixed
- [ ] A human watched both renders with sound, start to end
- [ ] **AI disclosure:** YouTube → *Altered or synthetic content: Yes* (AI voice, AI-played
      game); TikTok → *AI-generated content* label on
- [ ] Description says it plainly: an AI plays and decides, the voice is AI-generated
      (VieNeu-TTS), and credits the game: *Cities: Skylines © Paradox Interactive /
      Colossal Order*
- [ ] Paradox's current content-creation policy re-checked before monetising
- [ ] Music is the generated pad or a track with a licence on file
- [ ] Captions: the burned-in subtitles; upload the script text as the transcript too
- [ ] Title follows the pattern; thumbnail/cover are this episode's, with a true number
- [ ] Not a re-skin of an earlier episode: different hook type, different beat order,
      a different closing line (YouTube's inauthentic-content policy)
- [ ] `python -m post.series check` passes for the script's `threads` block
- [ ] After publishing: record the URL and date in the episode's `REVIEW.md`, then
      `python -m post.series publish episodes/<id>/script.vi.json` and commit `series.json`
