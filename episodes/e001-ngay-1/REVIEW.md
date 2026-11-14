# e001 — "AI làm thị trưởng — ngày 1" · review sheet

**Status: rendered, awaiting human review. Not published.**

Renders (git-ignored, rebuild with the commands in `docs/13-post-production.md`):

| File | Format | Length |
|---|---|---|
| `build/render/e001-ngay-1-9x16.mp4` | Short / TikTok, 1080×1920 @ 30 | 40.9 s |
| `build/render/e001-ngay-1-16x9.mp4` | YouTube master, 1920×1080 @ 30 | 40.9 s |

Source: live session 2026-11-15, recording `cs1-mayor 2026-11-15 01-13-42.mp4`,
journal `journal/20261115-011235.jsonl`, save `mayor-live-proof-1`.

## Fact check (every line against the journal)

| # | Voice-over (vi) | Journal | Fact |
|---|---|---|---|
| 1 | Thị trấn của tôi đang chết dần. (card: Dân số 144 → 75) | 4 | population fell from 144 to 75 |
| 2 | Một nửa dân đã bỏ đi. 25 căn nhà bỏ hoang… | 4 | 25 abandoned, 45 buildings with sewage problems |
| 3 | Ống xả ra sông bị mất điện… dây điện dài 1,2 cây số. | 5, 7 | drain pipe unpowered; Power Line built, ₡2,980 |
| 4 | Hai tháng rưỡi sau, vẫn không có điện… cách ống 20 mét (card: 20 m) | 10 | line built 2029-12-16, still unpowered 2030-03-03 |
| 5 | Kế hoạch B: dựng tua-bin gió… ống vẫn đứng im. | 11, 12, 14 | Wind Turbine ₡6,000; pipe powered but inactive |
| 6 | …được xây bằng công cụ cũ, bỏ qua luật của game… | 23 | the pre-#12 direct-write building never ran |
| 7 | Tôi xây lại ống mới, đúng luật, rồi nối ống nước và dây điện. | 24–28 | new Water Outlet ₡2,500 + pipe + power line |
| 8 | Nước thải chảy rồi. Dân số từ 64 lên 164. (card: 64 → 164) | 29, 30 | 0 occupied homes with sewage; population min 64, end 164 |
| 9 | Bài học ngày đầu: gần thôi chưa đủ, phải nối hẳn vào. | 10, 30 | the lesson of the 20 m gap |

## Automated checks

- `post.script`: valid (hook 1.9 s, ~36 s estimated, every beat cites the journal)
- Alignment for the subtitles: 99 % of words matched
- Unprimed transcription of the final mix (over the music bed): **93 %** of script words
  recognised in order. Words the small recogniser got wrong — **listen to these**:
  line 3 "Ống xả ra sông" (heard "Ông xảy ra xông"), line 4 "rưỡi" (heard "dễ"),
  line 5 "tua-bin" (heard "tui biên"), line 7 "ống" ×2 (heard "ấm"). If the voice really
  says them wrong, rephrase the line in the script and re-run `post.voice`.
- Loudness: −14.2 LUFS integrated, true peak ≤ −1.5 dBTP

## For the reviewer

- [ ] Watch both renders with sound; check the four words above
- [ ] Known visual issue: the session was filmed before UI hiding existed, so several
      clips show the chirper, tooltips and the bridge console (lines 1–2, 4–7). Accept for
      the pilot, or re-cut those lines to the clean shots (flyover 47–62 s, orbit 240–256 s)
- [ ] The music bed is the generated pad; swap in a licensed track with `--music` if wanted
- [ ] Disclosure on upload: mark as altered/synthetic content (AI voice, AI-played game)
- [ ] After publishing: `python -m post.series publish episodes/e001-ngay-1/script.vi.json`
      (closes `sewage`, opens `jobs` and `abandoned` for day 2)

## Draft upload metadata

**Title:** AI làm thị trưởng — ngày 1: thị trấn đang chết dần

**Description:**
Một AI tự chơi Cities: Skylines và làm thị trưởng. Ngày đầu tiên: dân bỏ đi vì nước thải
không có đường thoát, và AI mất ba lần mới sửa được. Mọi quyết định trong video đều là của
AI, ghi lại từ nhật ký của nó; giọng đọc được tạo bằng AI (VieNeu-TTS).

Cities: Skylines © Paradox Interactive / Colossal Order.

**Hashtags:** #CitiesSkylines #AI #thịtrưởngAI #citybuilder
