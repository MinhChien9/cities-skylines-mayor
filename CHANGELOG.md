# Changelog

All notable changes to the bridge mod and the MCP server. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/) (pre-1.0: minor = new or changed tools,
patch = fixes). Every PR adds its line under `[Unreleased]` with the issue number;
`docs/releasing.md` says how a version is cut.

## [Unreleased]

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
