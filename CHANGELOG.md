# Changelog

All notable changes to the bridge mod and the MCP server. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/) (pre-1.0: minor = new or changed tools,
patch = fixes). Every PR adds its line under `[Unreleased]` with the issue number;
`docs/releasing.md` says how a version is cut.

## [Unreleased]

### Fixed
- `build-road` marks each collision `blocking` (the game's own rule; grown buildings and power
  lines are removed, not blocking), and the hint names the first blocker. A long power line
  is refused for a road or turbine its pylons would hit, never for its length (#66).
- `build-road` names what a refused leg collides with (`collidesWith`: segment or building, id,
  prefab, `reason` `narrowAngle` / `cannotSplit` / `overlap`) and gives a matching hint. The
  pipe in #65 snapped to its own node, not a road's. The game refused it for leaving that node
  within ~41° of an existing pipe (#65).
- `restart-game.ps1` and `start-game.ps1` start the game through the Epic Games Launcher, so it
  gets the Epic login and loads the owned DLC. The Paradox launcher is skipped, so nobody
  has to click Play. `-Launch Direct` keeps the old bare start. `start-game.ps1 -Resume`
  now resumes the save as documented. This also explains #71: the missing expansion
  buildings were caused by the launch (#84).

### Added
- `city_map` service coverage overlays: `police`, `fire`, `health`, `education`, `highSchool`,
  `university`, `entertainment`, `transport`, `deathCare`, on the game's wellbeing scale (#73).
- Leave the main menu without the cursor: `city_load_game` loads a save and `city_new_game`
  starts a map or a scenario (`POST /commands/load`, `/commands/new-game`, `GET /state/maps`).
  They work before any city is loaded, wait for the load and check the city name.
  `/health` gains `loading` and `loadCount`, and `/state/summary` gains `cityName` and
  `scenario` (#69).
- `city_capabilities.loadedContent`: building prefab counts per service and sub-service, each
  expansion's `loaded` flag with its evidence, and `missingContent`. Ownership and
  `SupportsExpansion` do not prove that content loaded (#70).
- `city_district` creates, paints and erases districts and special areas (`parkType`,
  `parkLevel`, brush `x`, `z`, `radius`), so painting no longer needs a hand-written
  `city_request` body (#68).
- The in-game API console has a close button (`X`) that hides it completely and is
  remembered across restarts; **Options → Skylines Agent Bridge → Show the API console**
  brings it back (#82).

### Changed
- README and docs rewritten for newcomers: install from the release, task-first pages with
  plain names (`docs/getting-started.md`, `docs/tools.md`, `docs/architecture.md`,
  `docs/development.md`, …), outdated history removed, the expansions caveat (#71) and the
  happiness levers added (#80).

## [0.1.0] - 2026-11-15

The first public release: an MCP server that lets an AI agent play the mayor of a live
Cities: Skylines (2015) city without a cursor, through the game's own tool code.

### Added
- **Bridge mod** (`SkylinesAgentBridge.dll`): a localhost HTTP API whose every game read
  and write runs on the simulation thread (the threading fix over the upstream fork).
- **47 MCP tools** (`city_*`): every stat (`city_overview` bundles the pulse), utilities
  capacity vs usage, demand with the game's own target, scenario goals (`city_scenario`),
  map tiles (`city_areas`, `city_unlock_area`), budgets, policies, taxes, saves, transport
  lines (create / add stops / close), districts, the camera, and `city_activity` (this
  session's builds and office actions, from the activity log).
- **Validated building**: roads and every network through `NetTool.CreateNode` (snapping,
  junction splitting at nodes and crossings, `Underwater` refusal), buildings through the
  `BuildingTool` placement rules (zone-grid and shoreline snapping, deep roadside services,
  real-treasury check), zoning by segment with geometric/compass sides and a zone brush,
  bulldozing, moving buildings, emptying cemeteries and landfills. Dry runs list what a
  build would demolish (`wouldRemove`, `removesPowerLines`); builds report what they removed.
- **`city_map`**: a semantic top-down PNG from game data with world coordinates, dead ends,
  locked tiles, and land value / pollution / noise / traffic overlays.
- **Honest play**: milestone-locked prefabs and zones are refused; cheat mods are reported.
- **Activity log** (`activity/<session>.jsonl`): every mutating call with the game date,
  population and cash; the video project (`cities-skylines-video`) follows it.
- Project management: issues first (forms for bugs, features, game findings and tasks;
  labels; milestones; PR-standards and stale triage workflows; CODEOWNERS),
  `docs/development.md`, `docs/releasing.md`, `scripts/release.ps1`, this
  changelog and `SECURITY.md`.
- Scripts: `build-mod.ps1`, `install-mod.ps1`, `start-game.ps1`, `restart-game.ps1`
  (save → rebuild → relaunch → resume), the MCP smoke test and the API doc check.
- Docs: architecture, getting started, code map, API reference, game mechanics, the
  expansions, verification, the command surface, and AGENTS.md (the mayor's manual).

### Removed
- The studio (OBS, decision journal, camera moves, timelapse) and post-production moved
  to their own repo, `cities-skylines-video` (#63).

[Unreleased]: https://github.com/MinhChien9/cities-skylines-mayor/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/MinhChien9/cities-skylines-mayor/releases/tag/v0.1.0
