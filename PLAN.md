# PLAN — CS1 Mayor

The project plan. It travels with the repo and is the source of truth for scope and
progress. **Every session reads it first, works the next unchecked item, and updates it
before the task's PR is merged.**

## Session protocol

1. **Start:** read this file, then `CLAUDE.md` / `AGENTS.md`. Check "Environment state"
   below against the machine (game running? bridge `/health`? OBS running?).
2. **Work:** take the next unchecked item of the current phase. One task = one branch =
   one PR (`.github/CONTRIBUTING.md`). Do not start a second task in the same PR.
3. **End of task:** tick the boxes, record decisions and anything learned in the
   decision log, update "Environment state", and commit this file in the same PR.

## Goal

Claude plays Cities: Skylines 1 on its own, the play is recorded, and the recording
becomes short videos (TikTok/Shorts, 9:16) and longer episodes (YouTube, 16:9) with a
script and a Vietnamese voice-over.

Deliverable chain:

```
play (MCP tools) ──▶ capture (OBS + decision journal + timelapse)
                 ──▶ script (Claude, from the journal) ──▶ voice (TTS) + subtitles
                 ──▶ edit (ffmpeg) ──▶ human review ──▶ publish
```

Platform guard-rail: YouTube does not monetize mass-produced, templated AI content
(the "inauthentic content" policy, clarified July 2026). Every video needs a real
storyline (the AI mayor's decisions and mistakes), variation between episodes, and a
human review before publishing. Paradox permits monetized gameplay videos (re-check the
current policy before monetizing).

## Phases

### Phase 1 — Play without a cursor ✅

- [x] Bridge mod with simulation-thread dispatch, 30+ reads, office levers (#1–#11)
- [x] Build through the game's own tool code: roads/networks (`NetTool.CreateNode`,
      auto-split at nodes and crossings), buildings (`BuildingTool` rules), zoning by
      segment, `Underwater` refusal (#12)
- [x] `GET /state/map` + `city_map` PNG — the agent's eyes (#12)
- [x] `scripts/restart-game.ps1` (#12)
- [x] Camera yaw/pitch fix + `instant` glide flag (#13)
- [x] Claude Code wiring: `.mcp.json`, `CLAUDE.md` (#14)
- [x] Live proof: street grid, services, 144 citizens, zero cursor clicks

### Phase 2 — Studio: record the play ✅

Design (decided): a separate MCP tool class `StudioTools` in the same server, talking to
OBS through obs-websocket v5 (a minimal `ClientWebSocket` client, no dependency), plus a
session journal written by the server.

- [x] **S1 OBS setup script** `scripts/setup-obs.ps1`: enable obs-websocket (port 4455,
      auth on, generated password written to a git-ignored `studio.local.json`), create
      scene "CS1 Mayor" with a Game Capture of `Cities.exe`, recording to
      `D:\cs1-mayor-recordings`, NVENC H.264, hybrid MP4 (chapter markers)
- [x] **S2 `ObsClient`** — Hello/Identify (SHA-256 auth), request/response, reconnect;
      unit tests against a stub server
- [x] **S3 `StudioTools`**: `studio_status`, `studio_record` (start/stop/pause/resume),
      `studio_marker` (record chapter + journal entry), `studio_screenshot` (game
      capture frame as an image — a cheap real render when the map is not enough)
- [x] **S4 Decision journal**: `studio_note(text, kind)` + automatic entries for every
      build tool call, each with wall time, recording timecode, game date, population,
      cash → `journal/<session>.jsonl` (already git-ignored)
- [x] **S5 Cinematic camera**: orbit / fly-over helper (glide via `city_camera`
      `instant:false`), hide UI for B-roll, fixed timelapse viewpoints
- [x] **S6 Timelapse capture**: snapshot from fixed viewpoints every N game days
- [x] Live proof: one recorded session with markers and a journal (2026-11-15: 10 min
      recording, 6 chapters, 35-entry journal, 23 timelapse frames, save
      `mayor-live-proof-1`; the mayor found and fixed the town's sewage failure)

### Phase 3 — Post-production ✅

- [x] Script writer: journal + stats → hook (3 s), story beats, punchline; Vietnamese
      (`post.brief` → Claude writes `episodes/<id>/script.vi.json` by
      `docs/13-post-production.md` → `post.script` validates; pilot script e001 written)
- [x] TTS: decide between VieNeu-TTS (local, open source, GPU/CPU) and ElevenLabs (paid);
      default VieNeu for cost and licensing clarity (**VieNeu v3 Turbo**, `post.voice`;
      e001 voiced: 9 lines, 35.4 s)
- [x] Subtitles from the script with word timing (forced alignment), burned-in
      (`post.subtitles`: faster-whisper word times matched to the script, ASS with a
      spoken-word highlight; e001 aligned at 99 %, burn-in checked on a 9:16 test clip)
- [x] `ffmpeg` assembly: cut by markers, speed ramps, timelapse, 9:16 crop + 16:9 master,
      music bed, loudness normalisation (`post.edit`: voice-driven timeline, `chapter`
      visuals, generated pad ducked under the voice, −14 LUFS; e001 renders in ~20 s)
- [x] First pilot: a 60 s Short "AI làm thị trưởng — ngày 1" (40.9 s, 9:16 + 16:9
      rendered; `episodes/e001-ngay-1/REVIEW.md` has the fact check, QA and the
      **human review still to do before publishing**)

### Phase 4 — Series ✅

- [x] Episode format and naming, thumbnail generation, upload checklist (AI disclosure)
      (`docs/14-series.md`; `post.thumbnail` → 1280×720 thumbnail + 9:16 cover; e001 done)
- [x] Mayor persona and running storyline across episodes (`docs/15-mayor-persona.md`;
      `episodes/series.json` + `post.series` context/check/publish; threads in scripts;
      the brief carries the story so far; AGENTS.md start step 5)
- [x] Map overlays for the story (land value, traffic, pollution) via `/state/map`
      (`?overlay=landValue|pollution|noise|traffic`; `city_map overlay/saveAs` → stills)

### Backlog (not scheduled)

- District painting as a validated tool (transit lines: done, #47)
- `/state/progress` (milestones) as story triggers; `/commands/load`
- Map tiles: done (`city_areas`, `city_unlock_area`, #48)

## Decision log

| Date | Decision | Why |
|---|---|---|
| 2026-11-11 | Cursor (CUA) for spatial work | Upstream API building skipped the game's rules |
| 2026-11-15 | Back to API building, through the game's own tool code | CUA cost a screenshot per step and misread terrain; the real fault was bypassing the tools |
| 2026-11-15 | `city_map` semantic raster instead of screenshots | Exact coordinates, independent of camera/fog/UI |
| 2026-11-15 | OBS 33.0.1 portable + ffmpeg 8.1 (BtbN) on `D:\tools` | No winget on this LTSC machine; portable keeps config out of `%APPDATA%` |
| 2026-11-15 | Studio inside the existing MCP server | One server for the agent; the journal can see build calls |
| 2026-11-15 | `setup-obs.ps1` writes only the websocket config file; everything else goes over obs-websocket | OBS owns its scene/profile files and rewrites them on exit; the websocket keeps them consistent |
| 2026-11-15 | Canvas 1920×1080 @ 60 fps, Simple output, quality "Small", Game Capture matched on the executable | Standard YouTube master; the capture survives a title change; the game is scaled to fit |
| 2026-11-15 | Journal through one MCP call-tool filter, not per tool; dry runs and reads are not journalled, refusals are | One place covers every mutating tool; refusals and mistakes are story material |
| 2026-11-15 | Hide UI through the game's free-camera mode (`m_freeCamera`), not by hiding panels | The game's own hide-UI path also hides problem icons, names and borders, and the bridge console with them |
| 2026-11-15 | Camera shots streamed from the MCP server as ~30 Hz glide targets | No new bridge code per move; the game's camera smoothing evens out the steps |
| 2026-11-15 | Timelapse runs inside the MCP server, keyed on game days, and pauses an active recording around each capture (+0.7 s guard on each side) | Without the guard ~0.35 s of each cut leaked into the video (game capture lag); the guard measured clean |
| 2026-11-15 | `studio_marker` waits out a short recording pause (up to 6 s) | A timelapse capture pauses the recording ~3 s; OBS refuses chapters while paused (code 702, found live) |
| 2026-11-15 | Overlay tints scale to the highest value in view but with a floor per layer | Pure relative scaling made a town with land value 4 everywhere look vivid; the floor keeps weak signals faint and the maps truthful |
| 2026-11-15 | The edit is voice-driven; visuals are fitted to each line | The voice is the story's clock; fitting clips (trim, or slow down) keeps lines tight without hand timing |
| 2026-11-15 | Default music bed is a synthesised pad; game audio off by default | No licensing question for monetised uploads; the CS1 soundtrack is not ours. A licensed track can be passed with `--music` |
| 2026-11-15 | Word timing from faster-whisper matched to the script, not a phoneme forced aligner | Whisper (CTranslate2, no torch) gives good word times on clean TTS audio; the script stays the on-screen text; a wav2vec2 aligner would pull torch in for little gain |
| 2026-11-15 | TTS = VieNeu-TTS v3 Turbo, CPU/ONNX, voice Thiện Minh | Apache-2.0 incl. preset voices (monetised use allowed), offline, free, ~2× real time on CPU, no torch; ElevenLabs only if quality demands it |
| 2026-11-15 | Claude writes the script in-session from a deterministic brief; code only builds the brief and validates | The writing is the judgement; the brief and validator keep it truthful (beats cite journal `seq`) and inside the 3 s / 60 s budgets |
| 2026-11-15 | `ObsClient` never resends a request whose connection dropped after sending; it only retries a send that failed | Same rule as the mayor's: a lost answer does not mean OBS did not act (e.g. `StartRecord`) |

Lessons recorded so far:

- `TerrainManager.SampleRawHeightSmooth(float, float)` takes heightmap-grid coordinates
  and returns raw units; use the `Vector3` overloads for world metres.
- `NetSegment.m_averageLength` is a lane length — 0 for pipes and power lines.
- `NetTool.CreateNode` test pass refuses a straight road drawn across a dead end or the
  middle of a segment (`ObjectCollision`); split at those points.
- The game happily builds ground roads under water; the bridge refuses them.
- `CameraController.m_targetAngle` is (x = yaw, y = pitch 0..90).
- Repo line endings are mixed: most of `src/` is CRLF, the rest LF — keep each file's.
- Repo CI is disabled on GitHub; run the checks locally before merging.
- `set-camera` without `y` aims at sea level; where the land is higher the game itself
  re-frames the camera (pitch 30 → 38 within 2 s). Saved viewpoints keep the read-back,
  terrain-adjusted `y`, so they hold steady.
- At speed 3 one 5 s timelapse poll is ~3 game days: that is the finest cadence.
- This ffmpeg build has no `-pattern_type glob`; use a concat list for image sequences.
- `Building.Flags.Active` is derived by the game from `m_productionRate` (the player's
  on/off) *and* whether the building can run; writing the flag alone is undone next step.
  A powered, switched-on, problem-free building that stays inactive is broken, not off.
- Buildings from the pre-#12 direct-write endpoints may be non-functional (the first
  drain pipe never ran). Replace them through the validated tools.
- A water building connects through **its own pipe node at its landward end**, not its
  centre. A pipe ending at the centre leaves a free node and the building stays
  `WaterNotConnected` (a second drain pipe sat unconnected while 19 factories were
  abandoned for sewage). The bridge now snaps a network end that falls inside a building
  to that building's own compatible node.
- An unprimed `small` Whisper pass over the final mix is a good intelligibility check
  (93 % on e001) but mishears Vietnamese tones; it flags words for a human ear, it does
  not prove a TTS error.
- Roadside placement: when no zone slot fits (any building deeper than the 4-cell zone
  block: schools, fire stations, cemeteries), `BuildingTool` still accepts the snapped
  spot if `BuildingTool.CheckSpace` passes. The bridge skipped that step and refused every
  5+-deep service with `GridNotFound` (found in free play 2026-11-15, fixed in #35).
- OBS writes two chapters of its own into every hybrid MP4: "Start" at 0 and a trailing
  "Dummy"; post-production must skip them. `StopRecord` answers before the output
  stops, so `studio_record stop` polls until it is inactive.
- OBS 33.0.1 ships obs-websocket 5.7.6; `CreateRecordChapter` is available (S3 markers).
  `SetInputMute` takes `inputMuted`, not `inputMute`.
- A road built over a power line's **pylon** makes the game delete that line (the
  player's tool only shows a red highlight); crossing the wire between pylons is fine.
  Building Central Avenue over a pylon cut the sewage outlet's power and every service in
  town stopped (found 2026-11-15, 50k run). Builds now report `removed`, and dry runs
  `removesPowerLines`.
- A service building placed over **grown zoned buildings demolishes them** (they are not
  a collision). A coal plant dry run said ok and then took three factories with it;
  dry runs now list `wouldRemove`. Roads demolish grown buildings in their way too; road
  dry runs list them the same way (footprint test against the route).
- `zone-segment` with one side used to zone half a street on each side: it added
  `m_blockStartLeft` and `m_blockEndLeft`, but end blocks are named from the end node, so
  EndLeft is the geometric right. Sides are now decided geometrically, and compass sides
  (`north`/`south`/`east`/`west`) exist (found 2026-11-15: houses grew south of Factory
  Road and shops north of College Road).
- Shore-snapped buildings move with the live water surface: a drain pipe landed 13 m
  from its dry-run spot. Aim pipes at the position the build returned.
- The zone grid of a 4-lane (or wider) road starts 4 m past the kerb, so its first cell
  centre is 8 m out and an aim at the kerb missed the tool's 8 m zone snap (GridNotFound
  everywhere on Park Avenue). The bridge now widens the search to 16 m once.
- This game has the built-in **Unlimited Money** and **Unlock All** mods enabled (now
  shown in `city_capabilities.cheatMods`). Unlock All is why every milestone feature was
  open from the start. Unlimited Money answers the game's money test with "yes", so the
  bridge let a ₡60k police HQ through with ₡30k in the bank (cash −₡27k). The bridge now
  checks the real cash itself.
- Left alone for four game years at ~14k people, both cemeteries filled and 220
  buildings were abandoned for uncollected dead. Crematories don't fill up; build them
  before cemeteries are full. Full cemeteries and landfills can be emptied
  (`city_empty_building`); the game reuses the `LandfillFull` flag for a full cemetery.
- Bus stops are moved onto the road by the game, often 40 m from the point asked for and
  sometimes onto a cross street; a "closing" stop aimed at the first stop's requested
  point became a duplicate 15th stop. `add-stop close: true` now closes the loop. Lines
  are creatable from the MCP tool (`city_transport_line create / add-stop`).
- Map tiles: `GameAreaManager.UnlockArea` does not charge; the area tool's coroutine pays
  `FetchResource(LandPrice, CalculateTilePrice)` first. The bridge does the same
  (`city_unlock_area`). With Unlock All on, 9 tiles are allowed; the first extra tile
  (south, flat, with the highway) cost ₡3,700.
- **Never put a drain pipe up-current of a pumping station.** Drains at z −59/−150 and
  intakes at z −206…−339 on the west coast gave 10% water pollution; the sick count
  tripled (566 → 1797) and happiness fell 84 → 75. Water Treatment Plants (₡15k, +160k
  sewage capacity each, mostly clean outflow) replaced the two nearest drains; pollution
  fell to 3% within minutes.
- An emptied cemetery/landfill stays "emptying" and accepts nothing until
  `city_empty_building empty:false` (problem flag `EmptyingFinished`). Dead piled up again
  until it was switched back.
- Incinerators beside housing give `Pollution` problems in a ring around them;
  `city_move_building` moved two of them out to an industrial edge in one call each.
- Highway ramps through `city_build_road prefab HighwayRamp` work: a curved off-ramp
  from the southbound carriageway (Grove, west side, right-hand traffic) carried traffic
  at once. A tight control point collides with the highway itself; move it outwards.
- Placing a water treatment plant on the coast cleared a power-line pylon in its
  footprint; the `removed` report said so but the caller did not read it. The coast power
  corridor then fed only through incidental neighbours and later failed: every pumping
  station and treatment plant lost power and sewage capacity hit 0. Read `removed` after
  every placement; feed a long utility corridor from a service building, not a
  neighbourhood.
- **Utility cascade, 33k → 13.6k people.** The coast power corridor lost its feed, so the
  pumping stations and treatment plants stopped; with no water the **nuclear plant
  (which needs water) stopped too**, the whole city lost power, and ~1,400 buildings were
  abandoned within one long wait. Guard rails now: two independent feeds into any long
  utility corridor (end each on a service building), electricity budget 100% so the
  coal plants and turbines (no water needed) carry spare capacity, and a pulse check
  that warns on any utility above 85%. The game cleared about half the abandoned
  buildings by itself within months; bulldozing the rest speeds regrowth.
- The reported demand includes the tax/policy offset and can go negative. A city taxed
  at 10% since 7k people stalled at 36.5k with R −1 once its homes had levelled up (9%
  ceilings); back to 9%. Garbage service is distance-bound: incinerators 2.5 km away
  left 168 south-tile buildings abandoned until incinerators were built there.
- **Over-education stalls growth.** At 37k people 4,426 of 4,554 unemployed were
  university graduates (14.4k graduate workers vs graduate-level jobs): unemployed far
  above vacant jobs pins R at 0. Found with `city_demand.formula` (new). The documented
  workplace-demand formula did not match this build either (formula ≈ 97, game 8).
- A low demand bar is not low demand. Workplace showed 2 while the game's own target was
  96: each spawned building spends actual demand, so the bar sits near 0 while
  buildings appear as fast as demand accrues. Judge by `city_demand.formula.target`.
- Buildings conduct power. Removing a treatment plant that stood between a power line
  and the next one cut two pumping stations off; check what a removal disconnects.
- A Water Pumping Station placed inland (allowed, `ShorelineOrGround`) stays inactive
  with no problem flag: it does not pump groundwater. Use water towers for that.
- Long sweeps of dry runs (`place_clean`-style searches) slow the simulation a lot:
  every command runs on the simulation thread, and the collateral / wouldRemove scans
  walk tens of thousands of buildings and segments. Game time advanced about one month
  in an hour of sweeping. Search with `city_map` first, dry-run a handful.
- New districts out of reach of transmission lines can be powered locally with wind
  turbines (free-standing): seven turbines brought an unpowered office district back.
- **Industry → office fixes graduate unemployment, but keep the goods.** Rezoning
  industry to offices (`zone-segment overwrite`) turns over in a few game months (44 of
  48 buildings in one block), but converting almost all of it cut industry from 418 to 49
  buildings and starved 810 shops: ~50 `NoGoods` problems, 33 abandoned. Restored part of
  it beside the highway. Keep industry roughly in proportion to commercial (it was
  stable near 418 : 655).
- Sea pockets where raw drains discharged for years stay polluted long after: two new
  intakes there raised city water pollution 6 % → 11 %, and a death wave followed (2,063
  sick). Water towers (groundwater, clean residential area) dilute; removing two of them
  pushed pollution to 16 %. Eight towers in the north brought it back to 6 %.
- In this build the Water Pumping Station (`Water Intake`) is `ShorelineOrGround` and the
  `Advanced Wind Turbine` is the offshore `OnWater` model: the bridge accepts an inland
  pumping station and refuses an inland advanced turbine (`WaterNotFound`) because the
  game does.
- The game runs windowed at 1600×900, so the 1080p capture is upscaled; a 1920×1080
  game window would be sharper. The bridge's debug console overlay shows in the capture
  (S5 hides UI for B-roll).

## Environment state (update at the end of every task)

| Item | State |
|---|---|
| Game | Epic, `D:\Epic Games\CitiesSkylines`, map "Cannes". **50k free-play run reached 50,259 citizens** (2026-11-15, game date 2056-06, save `mayor-50k-reached`). 3 of 9 tiles owned (start, south, east + south-east): west/north/south/far-south districts, office park, east office district; nuclear + 2 coal + 15 turbines; 5 coastal pumping stations north of all discharge, 6 treatment plants on the south shore; incinerators in the NE, south and east; 2 bus lines; a highway exit/entrance at Lake Avenue. Taxes 9%, electricity 100%, healthcare 130%. Standing issues: ~250 dead awaiting pickup, crime, 15% unemployment (over-educated workforce). **Built-in cheat mods Unlimited Money and Unlock All are enabled**; the bridge checks real cash anyway |
| Bridge | Installed from `main` (#54), `runsOnSimulationThread: true` |
| MCP | Debug build current with `main`. The Release build in `bin/Release` is held by this Claude Code session's running server: rebuild it after the session ends (or `/mcp` reconnect) to get the studio tools in Claude Code |
| OBS | 33.0.1 portable at `D:\tools\obs-studio`, configured by `scripts/setup-obs.ps1`: websocket 4455 (password in `studio.local.json`), scene "CS1 Mayor", recordings in `D:\cs1-mayor-recordings` |
| ffmpeg | 8.1 at `D:\tools\ffmpeg\bin` (NVENC available), not on PATH; no `-pattern_type glob` |
| Post venv | `D:\tools\venv-post` (Python 3.11, `vieneu` 3.8.4, `faster-whisper` 1.2.1); model cache `HF_HOME=D:\tools\hf-cache` |
| GPU | RTX 2060 (Turing: NVENC H.264 / HEVC; no AV1 encode) |
