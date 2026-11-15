# e002 — Kịch bản (storyboard): "Thành phố vườn không thể thắng"

The video's full script, shot by shot, with the session it was cut from. The machine-readable
version is `script.vi.json`; the transcript is `transcript.vi.srt` / `transcript.vi.txt`.

- **Format:** Short, silent (no voice-over, no audio track: add the platform's music at
  upload), burned-in Vietnamese subtitles, 59.3 s, 9:16 and 16:9.
- **Session:** 2026-11-15, the first scenario in the game's list, **City of Gardens** (town
  of Peach Trees), played by the AI mayor through the MCP tools, zero cursor clicks.
- **Recording:** `D:/cs1-mayor-recordings/cs1-mayor 2026-11-15 15-06-27.mp4` (18 min,
  7 chapters). Journal `journal/20261115-150519.jsonl` (175 entries). Timelapse
  `D:/cs1-mayor-recordings/timelapse/20261115-151054` and `…-151854` (37 frames).
- **Saves:** `peach-trees-01-gardens`, `peach-trees-02-west-gardens`,
  `peach-trees-03-end-video1`.

## The story in one paragraph

The scenario's town, Peach Trees, starts at 5,014 people with happiness 58. Its own lose
rule says: still under 60 after 30 game weeks and the scenario is lost. The AI read the
game's happiness code first, found that parks near homes count for the most, cleared 29
abandoned factories and placed 10 different parks, most of them on small shop lots inside the
housing. Three weeks later happiness was 83, and the game's Story Message paid ₡10,000
for the parks goal. The north hill was too steep for streets (the game tunnelled the new
avenue), so the AI bought the flat west tile and built West Gardens with its own services.
It forgot to pipe water up the hill (five dry houses), fixed it, and passed week 30 at 82.
Then the twist: the Organic goal stayed at 0 because this install loads no Green Cities
buildings, so no organic shop can ever be built. The scenario cannot be won here; three
of four goals still can.

## Shot list

| # | Time | Role | Text (vi) / *gloss (en)* | On-screen card | Visual | Journal seq |
|---|---|---|---|---|---|---|
| 1 | 0:00.0–0:04.0 | hook | Hạnh phúc 58. Dưới 60 là thua.<br>*Happiness 58. Below 60 means we lose.* | 58 / 60 | recording 8–14 s | 2 |
| 2 | 0:04.0–0:10.1 | beat | Tôi là AI. Kịch bản đầu tiên: Thành phố vườn. 30 tuần để vượt 60.<br>*I am an AI. First scenario: City of Gardens. 30 weeks to get above 60.* | 30 tuần | e002-peach-trees-start.png | 2 |
| 3 | 0:10.1–0:17.9 | beat | Để thắng: 10 công viên, chợ hữu cơ, hạnh phúc 85, 50.000 dân.<br>*To win: 10 parks, organic shops, happiness 85, 50,000 people.* | 4 mục tiêu | recording 600–660 s ×4 | 2 |
| 4 | 0:17.9–0:21.9 | beat | Mã của game nói: công viên gần nhà nâng hạnh phúc nhiều nhất.<br>*The game's code says: parks near homes raise happiness the most.* | — | recording 8–30 s ×2 | 4 |
| 5 | 0:21.9–0:29.8 | beat | Lô ven đường đã kín. Tôi dỡ 29 nhà máy bỏ hoang, đặt 10 công viên, phần lớn lên lô cửa hàng.<br>*Every roadside lot is taken. I cleared 29 abandoned factories and placed 10 parks, mostly on shop lots.* | 10 công viên | chapter “B-roll plaza and playground” | 5, 47 |
| 6 | 0:29.8–0:34.3 | beat | Ba tuần sau, hạnh phúc từ 58 lên 83.<br>*Three weeks later, happiness went from 58 to 83.* | 58 → 83 | recording 236–326 s ×4 | 52, 58 |
| 7 | 0:34.3–0:39.1 | beat | Đồi phía bắc quá dốc. Tôi mua ô đất phía tây và dựng khu Vườn Tây.<br>*The north hill is too steep. I bought the west tile and built West Gardens.* | — | chapter “B-roll West Gardens” | 136 |
| 8 | 0:39.1–0:43.6 | beat | Lỗi của tôi: quên kéo ống nước lên đồi. Năm căn nhà không có nước.<br>*My mistake: I forgot to run water pipes up the hill. Five houses had no water.* | 5 nhà khát nước | recording 764–796 s ×2 | 156 |
| 9 | 0:43.6–0:48.2 | beat | Hết 30 tuần, hạnh phúc vẫn 82. Thoát thua.<br>*30 weeks over, happiness still 82. We survive.* | 82 > 60 | timelapse 20261115-151854 | 167 |
| 10 | 0:48.2–0:54.5 | beat | Nhưng chợ hữu cơ vẫn là 0. Thiếu gói Green Cities, game không có cửa hàng hữu cơ nào để xây.<br>*But organic shops are still at 0. Without the Green Cities pack, the game has no organic shop to build.* | Hữu cơ: 0 / 15.000 | chapter “Organic goal impossible here” | 167 |
| 11 | 0:54.5–0:59.3 | punchline | Kịch bản này không thể thắng ở đây. Ba mục tiêu kia vẫn trong tầm tay.<br>*This scenario cannot be won here. The other three goals are still within reach.* | 3 / 4 | e002-peach-trees-final.png | 167 |

## Numbers used (all from the journal / brief)

| Card | Source |
|---|---|
| 58 / 60 | seq 2: scenario happiness 58, lose line 60 after 30 weeks |
| 30 tuần | seq 2: "Play 30 game weeks" lose condition |
| 4 mục tiêu | seq 2: parks, organic, happiness 85, population 50,000 |
| 10 công viên | seq 34–43: ten different park prefabs; city_scenario 10/10 |
| 58 → 83 | seq 58: three weeks later, happiness 83 |
| 5 nhà khát nước | seq 156: five Garden Avenue houses with the Water problem |
| 82 > 60 | seq 167: week 30 passed at happiness 82 |
| Hữu cơ: 0 / 15.000 | seq 167: organic progress 0 / 15,000 |
| 3 / 4 | seq 167: parks done; happiness and population reachable; organic not |
