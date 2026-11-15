# PLAN — CS1 Mayor

The project plan: the goal, the phase summary, the decision log, the lessons and the
environment state. **The backlog is not here: it is the GitHub issues**
(https://github.com/MinhChien9/cities-skylines-mayor/issues), grouped into milestones (one per release). See
[`docs/development.md`](docs/development.md).

## Session protocol

1. **Start:** read this file, then `CLAUDE.md` / `AGENTS.md`. Check "Environment state"
   below against the machine (game running? bridge `/health`?).
2. **Pick work from the issues:** `gh issue list --milestone v0.2.0`, then `--label p1`.
   Say which issue you take. One issue = one branch = one PR with `Closes #n`
   (`.github/CONTRIBUTING.md`). Do not start a second issue in the same PR.
3. **New problems become issues, not notes:** a tool bug, a missing lever or something
   learned about the game → `gh issue create` with the matching form's headings (bug,
   feature, finding, task). Never fix silently and never add a backlog line here.
4. **End of task:** the PR closes its issue, adds a `CHANGELOG.md` line under
   `[Unreleased]`, records a *decision* here if one was made, and updates "Environment
   state". A closed *finding* adds its lesson to the list below (or to
   `docs/game-mechanics.md`).

## Goal

An MCP server that lets an AI agent play Cities: Skylines 1 on its own: read every stat,
pull every lever and build through the game's own tool code, without a cursor. This repo
is the MCP server and the in-game bridge only.

Recording the play and making videos from it (OBS, decision journal, timelapse, scripts,
subtitles, edit, series) is the separate **cs1-video-making** repo (`../cs1-video-making`),
which runs its own `cs1-studio` MCP server next to this one and follows this server's
activity log (`activity/<session>.jsonl`). Phases 2–4 below were built here and moved
there on 2026-11-15.

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

### Phases 2–4 — Studio, post-production, series ✅ (moved)

Built here, then moved to **cs1-video-making** (2026-11-15) with their decisions and
lessons: OBS recording and markers, the decision journal, cinematic camera and timelapse,
the `post/` pipeline (brief, script, TTS, subtitles, edit, thumbnails, series), and the
episodes. What stayed: `city_camera` (including `hideUi`) and `city_map` overlays/`saveAs`.

### Phase 5 — Scenarios

- [x] Play a scenario honestly: `/state/scenario` + `city_scenario` (win/lose progress, next
      milestone, zone unlocks); locked prefabs and zones are refused (`UnlockCheck`).
      First run: City of Gardens (video e002 in cs1-video-making)
- [x] Next play session moved to the video repo's issues (playing is recorded there)

### Phase 6 — MCP only

- [x] Split the video pipeline into `cs1-video-making`; replace the decision journal with
      the neutral activity log (`activity/`, `city_activity`); `city_map saveAs` writes to
      `$CS1_MAP_DIR` (default `maps/`)

### Phase 7 — Public and issue-driven

- [x] Issue forms, labels, milestones, triage workflows, CODEOWNERS; development process
      and release docs; CHANGELOG; SECURITY; `scripts/release.ps1`; v0.1.0 released
- [x] The old backlog and every open roadmap item are issues (#65–#77)

### Backlog

GitHub issues: https://github.com/MinhChien9/cities-skylines-mayor/issues. Next release: milestone **v0.2.0**.

## Decision log

| Date | Decision | Why |
|---|---|---|
| 2026-11-11 | Cursor (CUA) for spatial work | Upstream API building skipped the game's rules |
| 2026-11-15 | Back to API building, through the game's own tool code | CUA cost a screenshot per step and misread terrain; the real fault was bypassing the tools |
| 2026-11-15 | `city_map` semantic raster instead of screenshots | Exact coordinates, independent of camera/fog/UI |
| 2026-11-15 | Activity log (formerly the journal) through one MCP call-tool filter, not per tool; dry runs and reads are not logged, refusals are | One place covers every mutating tool; refusals and mistakes are story material |
| 2026-11-15 | Hide UI through the game's free-camera mode (`m_freeCamera`), not by hiding panels | The game's own hide-UI path also hides problem icons, names and borders, and the bridge console with them |
| 2026-11-15 | Overlay tints scale to the highest value in view but with a floor per layer | Pure relative scaling made a town with land value 4 everywhere look vivid; the floor keeps weak signals faint and the maps truthful |
| 2026-11-15 | The bridge refuses milestone-locked prefabs and zones (`UnlockCheck`) | The game's tools do not check unlocks (the toolbar hides them); in a scenario a locked building is a cheat and a false win |
| 2026-11-15 | Scenario goals read through the game's own trigger objects (`UnlockManager.m_scenarioTriggers`, `GetLocalizedProgress`) | Same numbers as the in-game goals tab; no reimplementation of the conditions |
| 2026-11-15 | Split: this repo is MCP only; recording and the video pipeline moved to `cs1-video-making` with its own `cs1-studio` MCP server. The mayor server keeps a neutral activity log the studio tails | The user wanted the MCP to stand alone. A file is the loosest coupling: the mayor knows nothing about OBS, the studio stamps each logged action with the recording timecode the moment it appears |
| 2026-11-15 | Scripts start the game through the Epic URI and hand the Epic login from the Paradox launcher to `Cities.exe` themselves | A bare `Cities.exe` loads no DLC; this skips the Play click too (#84) |
| 2026-11-15 | Load / new-game run on the Unity main thread with no level loaded, through a `DontDestroyOnLoad` pump | The game's own Load and New Game panels run there; there is no simulation to protect at the main menu (#69) |
| 2026-11-15 | Cargo is reported as coverage and the industrial level-up score, not as a line type | CS1 has no player-made cargo lines; cargo stations act through `CargoTransport` coverage (#74) |
| 2026-11-16 | `build-road` auto-splits only at nodes and crossings within 4 m of the road's own level (terrain + `elevation`) | An overpass split at the highway below became an at-grade junction and failed `SlopeTooSteep`; what then blocks an overpass is a pillar, so the hint lists the planned node positions (#99) |

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
- Roadside placement: when no zone slot fits (any building deeper than the 4-cell zone
  block: schools, fire stations, cemeteries), `BuildingTool` still accepts the snapped
  spot if `BuildingTool.CheckSpace` passes. The bridge skipped that step and refused every
  5+-deep service with `GridNotFound` (found in free play 2026-11-15, fixed in #35).
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
- **Power islands break on budget cuts.** The east districts ran on their own wind
  turbines only; cutting the electricity budget to 80% left the city 21% spare but the
  island short, and 56 buildings were abandoned within one pulse. A coal plant inside the
  island fixed it. Before trimming a utility budget, check every isolated grid, not the
  citywide surplus.
- Raising the education budget raises *every* school's capacity, the university's too:
  it fixed high-school overflow but added graduates to an already graduate-heavy
  unemployment. The city settled near 52k: residential demand stays 0 while ~6,000
  graduates wait for office jobs.
- **A dedicated office tile broke the 52k plateau.** Tile (2,0) bought for ₡10.5k, a
  grid zoned mostly Office with an industrial strip for goods and its own coal plant:
  unemployment 21 % → 12 %, the residential target turned positive and the city grew
  to 53.4k. Mistake on the way: the new columns were not dry-run and cut through about
  20 grown offices on the boulevard they crossed; dry-run and route through gaps.
- **Water pollution to 0 %: groundwater towers, no sea intakes.** After years of drains
  the whole west-coast sea stayed polluted (6–9 % city water pollution with every intake
  north of all discharge). Removing intakes one group at a time showed the two
  northernmost were the source; 22 water towers (₡3.5k each, power passes tower to
  tower) in a clean residential area now supply 1.32M at 0 %.
- A power line can follow a road corridor under a highway: pylons in the gaps between
  ramps and carriageways (spans ≤ ~90 m; a 10 m leg is `TooShort`, and a dry run on a
  route through an existing node re-splits there, so rebuild the span in one piece).
  The Cooper Street line joined the east districts to the main grid.
- **The cycle at 55k: jobs, then homes.** Once the office tiles brought unemployment to
  2 % (more vacant jobs than unemployed), the residential target rose to 30+ but the
  population still drifted down: only ~300 empty residential cells were left, so each
  spawn spent the demand and deaths outpaced move-ins. A residential district on the
  south tile (services placed first, a power line from the district coal plant, a pipe on
  every avenue) restarted growth at once (+1.6k in ten minutes). Watch empty zoned cells
  per type, not just demand.
- In this build the Water Pumping Station (`Water Intake`) is `ShorelineOrGround` and the
  `Advanced Wind Turbine` is the offshore `OnWater` model: the bridge accepts an inland
  pumping station and refuses an inland advanced turbine (`WaterNotFound`) because the
  game does.
- **Scenarios (City of Gardens, 2026-11-15).** Happiness is `(health + wellbeing) / 2` per
  citizen (`ResidentAI`); the largest wellbeing term is entertainment near home (parks: up to
  +60 local, +40 citywide), then transport, police, fire, education (+60 each). Ten
  different cheap parks on shop lots took scenario happiness from 58 to 83 in three game weeks.
  The scenario's "10 parks" trigger paid ₡10,000 through a Story Message.
- Scenario zone unlocks are custom: Peach Trees had only low density and industry until
  Milestone 6 (7,500 people) — read `city_scenario.zoneMilestones` before planning density.
- **On Epic, DLC loads only with the Epic login.** The game needs Epic's login arguments
  (`-AUTH_PASSWORD=<exchange code>`, `-epicapp=…`), which Epic hands to the Paradox
  launcher and the launcher hands to `Cities.exe` on Play. `restart-game.ps1` used to start
  a bare `Cities.exe`, so no `CommercialEco`, Leisure, Tourist or disaster prefabs loaded and
  City of Gardens' organic goal looked impossible (#71). The real cause was the launch (#84).
  Started through Epic, 39 `CommercialEco` and the other DLC buildings load. Without them an
  Organic district keeps demolishing ordinary shops with nothing to replace them (commercial
  fell 213 → 152). `SteamHelper.IsDLCOwned` proves nothing on Epic; count the prefabs.
- Shops spawn only at "good places" (`ZoneBlock.IsGoodPlace`: within ~40 m of a building that
  conducts power) while any good place exists elsewhere.
- A straight Basic Road over a ridge became a tunnel (`Basic Road Slope`) where the grade was
  too steep; nothing can be zoned along the tunnel. A 420 m power line in one span was
  refused (`ObjectCollision`); three shorter legs built.
- A new segment may not leave a node within about 41° of a segment already there
  (`NetTool.CanAddSegment`, dot product above 0.75): `ObjectCollision`, as in the player's tool.
  A pipe from a junction at (-141, 105) aimed along an existing pipe looked like "snapped to a
  road node" (#65). It had snapped to the pipe's own node. Builds now name the colliding
  objects (`collidesWith`, `reason: narrowAngle`).

## Environment state (update at the end of every task)

| Item | State |
|---|---|
| Game | Epic, `D:\Epic Games\CitiesSkylines`. **Current: scenario City of Gardens** (Peach Trees, 6,665 people, happiness 82, game 2027-08, save `peach-trees-03-end-video1`; 2 of 9 tiles; win 1/4: parks done; organic shops are possible now that the game starts with the Epic login, #84; save `restart-20261115-213128`). Cheat mods are **off** now. Earlier free-play city: map "Cannes". **50k free-play run reached 50,259 citizens** (2026-11-15, game 2056-06, save `mayor-50k-reached`), then 53.4k (game 2064-11, save `mayor-50k-34`). 5 of 9 tiles owned (start, south, far south, east, south-east): west/north/south/far-south districts, office park, east office district; nuclear + 2 coal + 15 turbines; 5 coastal pumping stations north of all discharge, 6 treatment plants on the south shore; incinerators in the NE, south and east; 2 bus lines; a highway exit/entrance at Lake Avenue. Taxes 9%, electricity 100%, healthcare 130%. Standing issues: ~250 dead awaiting pickup, crime, 15% unemployment (over-educated workforce). **Built-in cheat mods Unlimited Money and Unlock All are enabled**; the bridge checks real cash anyway |
| Bridge | Installed from the #99 branch (`build-road` same-level auto-split, `crossesWithoutJunction`, pillar hint) on top of `main` after #95 (collision/variant reports, `/commands/load` + `/commands/new-game`, `/state/maps`, `/state/resources`, `/state/industry`, coverage and resource overlays), `runsOnSimulationThread: true`. The game is started through Epic (`scripts/restart-game.ps1`), so all 14 expansions load (`city_capabilities.loadedContent`) |
| MCP | Release rebuilt with #99 (`city_build_road autoSplit`), after #96 (new tools: `city_maps`, `city_load_game`, `city_new_game`, `city_resources`, `city_industry`; `city_district` create/paint/erase). A running server locks `bin/Release/net10.0/Cs1Mayor.Mcp.dll`; renaming the locked DLL aside lets the build replace it, then `/mcp` reconnect picks it up |
