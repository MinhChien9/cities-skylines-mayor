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

### Phase 2 — Studio: record the play ⬅ current

Design (decided): a separate MCP tool class `StudioTools` in the same server, talking to
OBS through obs-websocket v5 (a minimal `ClientWebSocket` client, no dependency), plus a
session journal written by the server.

- [ ] **S1 OBS setup script** `scripts/setup-obs.ps1`: enable obs-websocket (port 4455,
      auth on, generated password written to a git-ignored `studio.local.json`), create
      scene "CS1 Mayor" with a Game Capture of `Cities.exe`, recording to
      `D:\cs1-mayor-recordings`, NVENC H.264, hybrid MP4 (chapter markers)
- [ ] **S2 `ObsClient`** — Hello/Identify (SHA-256 auth), request/response, reconnect;
      unit tests against a stub server
- [ ] **S3 `StudioTools`**: `studio_status`, `studio_record` (start/stop/pause/resume),
      `studio_marker` (record chapter + journal entry), `studio_screenshot` (game
      capture frame as an image — a cheap real render when the map is not enough)
- [ ] **S4 Decision journal**: `studio_note(text, kind)` + automatic entries for every
      build tool call, each with wall time, recording timecode, game date, population,
      cash → `journal/<session>.jsonl` (already git-ignored)
- [ ] **S5 Cinematic camera**: orbit / fly-over helper (glide via `city_camera`
      `instant:false`), hide UI for B-roll, fixed timelapse viewpoints
- [ ] **S6 Timelapse capture**: snapshot from fixed viewpoints every N game days
- [ ] Live proof: one recorded session with markers and a journal

### Phase 3 — Post-production

- [ ] Script writer: journal + stats → hook (3 s), story beats, punchline; Vietnamese
- [ ] TTS: decide between VieNeu-TTS (local, open source, GPU/CPU) and ElevenLabs (paid);
      default VieNeu for cost and licensing clarity
- [ ] Subtitles from the script with word timing (forced alignment), burned-in
- [ ] `ffmpeg` assembly: cut by markers, speed ramps, timelapse, 9:16 crop + 16:9 master,
      music bed, loudness normalisation
- [ ] First pilot: a 60 s Short "AI làm thị trưởng — ngày 1"

### Phase 4 — Series

- [ ] Episode format and naming, thumbnail generation, upload checklist (AI disclosure)
- [ ] Mayor persona and running storyline across episodes
- [ ] Map overlays for the story (land value, traffic, pollution) via `/state/map`

### Backlog (not scheduled)

- Transit line drawing and district painting as validated tools
- `/state/progress` (milestones) as story triggers; `/commands/load`

## Decision log

| Date | Decision | Why |
|---|---|---|
| 2026-11-11 | Cursor (CUA) for spatial work | Upstream API building skipped the game's rules |
| 2026-11-15 | Back to API building, through the game's own tool code | CUA cost a screenshot per step and misread terrain; the real fault was bypassing the tools |
| 2026-11-15 | `city_map` semantic raster instead of screenshots | Exact coordinates, independent of camera/fog/UI |
| 2026-11-15 | OBS 33.0.1 portable + ffmpeg 8.1 (BtbN) on `D:\tools` | No winget on this LTSC machine; portable keeps config out of `%APPDATA%` |
| 2026-11-15 | Studio inside the existing MCP server | One server for the agent; the journal can see build calls |

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

## Environment state (update at the end of every task)

| Item | State |
|---|---|
| Game | Epic, `D:\Epic Games\CitiesSkylines`, map "Cannes", test town around (330, -640) |
| Bridge | Installed from `main` (#13), `runsOnSimulationThread: true` |
| MCP | Release build current with `main`; `.mcp.json` wires it into Claude Code |
| OBS | 33.0.1 portable at `D:\tools\obs-studio` (config in `D:\tools\obs-studio\config\obs-studio`); started once, **websocket and scene not configured yet** |
| ffmpeg | 8.1 at `D:\tools\ffmpeg\bin` (NVENC available), not on PATH |
| GPU | RTX 2060 (Turing: NVENC H.264 / HEVC; no AV1 encode) |
