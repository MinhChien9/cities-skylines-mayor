# e002 — "Thành phố vườn không thể thắng" · review sheet

**Status: rendered (silent), awaiting human review. Not published.**

Renders (git-ignored, rebuild with `python -X utf8 -m post.edit episodes/e002-thanh-pho-vuon/script.vi.json --silent`
and `python -X utf8 -m post.thumbnail episodes/e002-thanh-pho-vuon/script.vi.json`):

| File | Format | Length |
|---|---|---|
| `build/render/e002-thanh-pho-vuon-9x16.mp4` | Short / TikTok, 1080×1920 @ 30, no audio track | 59.3 s |
| `build/render/e002-thanh-pho-vuon-16x9.mp4` | YouTube master, 1920×1080 @ 30, no audio track | 59.3 s |
| `build/render/e002-thanh-pho-vuon-thumb-16x9.jpg` / `-cover-9x16.jpg` | thumbnail / cover | — |
| `build/render/e002-thanh-pho-vuon.vi.srt` (copied to `transcript.vi.srt`) | transcript | 11 cues |

The full script, shot by shot, is in [`STORYBOARD.md`](STORYBOARD.md).

## Fact check (every line against the journal)

| # | Text (vi) | Journal | Fact |
|---|---|---|---|
| 1 | Hạnh phúc 58. Dưới 60 là thua. | 2 | `city_scenario`: happiness 58; lose condition "happiness under 60" + "play 30 game weeks" |
| 2 | Tôi là AI. Kịch bản đầu tiên: Thành phố vườn. 30 tuần để vượt 60. | 2 | City of Gardens is first in the game's scenario list (sorted by name) |
| 3 | Để thắng: 10 công viên, chợ hữu cơ, hạnh phúc 85, 50.000 dân. | 2 | the four win conditions, all required at once (`Simultaneous`) |
| 4 | Mã của game nói: công viên gần nhà nâng hạnh phúc nhiều nhất. | 4 | `ResidentAI.UpdateWellbeing`: entertainment is the largest term (local up to +60, citywide up to +40) |
| 5 | …dỡ 29 nhà máy bỏ hoang, đặt 10 công viên, phần lớn lên lô cửa hàng. | 5, 34–43, 47 | 29 bulldozed; 10 different parks; 6 on shop lots, 4 on house lots (0–3 houses each) |
| 6 | Ba tuần sau, hạnh phúc từ 58 lên 83. | 58 | game 2026-12-10, scenario happiness 82–83 (the lose-condition bar read 83) |
| 7 | Đồi phía bắc quá dốc. Tôi mua ô đất phía tây và dựng khu Vườn Tây. | 136 | Garden Avenue tunnelled (Basic Road Slope); cross streets `SlopeTooSteep`; west tile ₡3,700 |
| 8 | Lỗi của tôi: quên kéo ống nước lên đồi. Năm căn nhà không có nước. | 156 | five Garden Avenue houses with `Water` problems; pipe built after |
| 9 | Hết 30 tuần, hạnh phúc vẫn 82. Thoát thua. | 167 | week 30/30 at happiness 82; lose condition 3 not triggered |
| 10 | Nhưng chợ hữu cơ vẫn là 0. Thiếu gói Green Cities… | 167 | no `CommercialEco` (or After Dark / Natural Disasters) building prefab is loaded on this install |
| 11 | Kịch bản này không thể thắng ở đây. Ba mục tiêu kia vẫn trong tầm tay. | 167 | parks 10/10 done; happiness 82/85 and population 6,665/50,000 still open |

## Automated checks

- `post.script`: valid, silent timing ~59.3 s (≤ 60 s), hook 2.6 s, every beat cites the journal
- Streams: video only (`-an`), 1080×1920 and 1920×1080, H.264
- Contact sheet checked: the cards and subtitles render with full Vietnamese diacritics

## For the reviewer

- [ ] Watch both renders start to end (no sound: there is none)
- [ ] Known visual issues: lines 1, 4, 6 and 8 use footage with the game UI and the bridge
      console overlay (top left), and the game's tutorial tooltips (bottom right) in lines 1 and 4.
      Line 6 deliberately shows the scenario's own Story Message ("…enjoy the many parks…
      You have gained 10000 money!"), which the subtitle overlaps. Accept, or re-cut to the B-roll
      chapters
- [ ] Line 10's claim is about **this install**: the expansions' buildings are not loaded
      (Epic build). If Green Cities is owned on the Epic account, check its installation before
      publishing so the claim stays true
- [ ] Add music on the platform (TikTok/Shorts library) or render with a voice-over later
- [ ] Disclosure on upload: AI-played game, AI-written captions → mark as altered/synthetic
      content (YouTube) / AI-generated content (TikTok)
- [ ] After publishing: `python -m post.series publish episodes/e002-thanh-pho-vuon/script.vi.json`
      (opens `organic`, `population-50k`, `happiness-85`)

## Draft upload metadata

**Title:** AI làm thị trưởng — kịch bản 1: Thành phố vườn không thể thắng

**Description:**
Một AI tự chơi Cities: Skylines và làm thị trưởng. Kịch bản đầu tiên, City of Gardens:
hạnh phúc 58, dưới 60 sau 30 tuần là thua. AI đọc mã của game, dựng 10 công viên và kéo
hạnh phúc lên 83 — rồi phát hiện một mục tiêu không bao giờ đạt được trên máy này. Mọi
quyết định trong video là của AI, ghi lại từ nhật ký của nó; phụ đề do AI viết.

Cities: Skylines © Paradox Interactive / Colossal Order.
