# Roadmap

**The pivot (2026-11-11):** the API-building experiment is over — an agent building
roads/zones/buildings blind, by coordinate, always produced ugly or failing results,
because the bridge skips the validation and visual judgement the game's own UI tools
provide. The product is now the **mayor's office model**:

- The MCP server is the agent's **office**: it sees every stat (so the agent never
  opens an in-game info panel) and controls every non-spatial lever (clock, taxes,
  budgets, policies, saves, line/district management).
- The agent's **cursor** (its CUA channel, driving the game window) does all spatial
  work — roads, zones, buildings, bulldozing, transit lines, district painting —
  through the game's own UI tools, with the clock paused.
- `city_request` POSTs are allowlisted to the office endpoints; the build endpoints
  stay in the bridge for completeness but are not agent-reachable.

`AGENTS.md` is the operating manual for this model. The sections below describe the
bridge work, which continues — the office just reads more of it now.

## Done

- [x] **Research** — CS1 internals decompiled with ILSpy, all API signatures verified
- [x] **Architecture decision** — in-game bridge + external MCP server, transport measured
- [x] **Fork** of `Sunwood-ai-labs/cities-skylines1-agent-skill` (MIT) — HTTP server,
      121 KB state builder, and all command implementations reused
- [x] **Threading fix** — `SimulationDispatcher` routes all game work to the simulation
      thread; the upstream's reflection bypass and `CommandQueue` are deleted
- [x] **`/debug/threads`** — runtime proof of which thread executes commands
- [x] **MCP server** — C# / .NET 10, `ModelContextProtocol` 2.4.0, stdio
- [x] **Raw 1:1 tool surface** — one tool per bridge endpoint, each relaying one
      HTTP call with no server-side logic *(superseded — see the pivot above: the
      surface is now the 32-tool office, and build endpoints are cursor-only)*
- [x] **Office tool surface** — 32 tools: every read plus `city_overview` (the pulse
      in one call), the non-spatial levers, and a POST-allowlisted `city_request`.
      Build tools removed — spatial work is the cursor's (CUA)
- [x] **Protocol smoke test** — initialize → tools/list → tools/call verified
- [x] **Knowledge base** — `docs/05-game-mechanics.md`, `docs/06-expansions.md`
- [x] **Agent operating manual** — `AGENTS.md` (the office/cursor division of labor)
- [x] **Documentation** — architecture, API reference, verification, opencode setup
- [x] **Project structure** — `src/` split into `Bridge` / `Dispatch` / `Commands` / `State` /
      `Support`; `GameState.cs` split into focused collectors; HTTP types and the route
      table extracted from `ApiServer.cs`
- [x] **Real project files** — `src/SkylinesAgentBridge.csproj`, `mcp-server/*.csproj`,
      `tests/*.csproj` and a `Cs1Mayor.sln`; the mod is now an ordinary MSBuild project
- [x] **Automated tests** — xunit tests over the MCP server and the bridge's JSON reader
- [x] **CI** — build, test, MCP protocol smoke test and an API-docs/route-table consistency
      check on every push
- [x] **API-surface research** — verified game APIs catalogued in
      [`12-command-surface.md`](12-command-surface.md), including the `ICities.dll`
      discovery (see below)
- [x] **`ICities` found** — the game ships a supported modding API (`ICities.dll`)
      covering terrain, resources, save/load, map tiles, milestones, camera, buildings
      and the simulation clock. The bridge already implements `ThreadingExtensionBase`,
      so `IManagers` is one field away.

## Phase 1 — the go/no-go gate

**Start the game, load a city, and confirm the thread proof.**

```powershell
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

- [x] `probeThreadId == simulationThreadId` — 2 == 2
- [x] `probeThreadId != mainThreadId` — 2 != 1
- [x] `runsOnSimulationThread: true`

If this fails, **nothing else matters** — every mutation would be racing the simulation.
See `docs/01-architecture.md`.

## Phase 2 — in-game smoke test

- [x] Mod loads without errors in `Cities_Data\output_log.txt`
- [x] `/health` responds
- [x] `/state/summary` returns sensible data
- [x] Place one road segment → `/state/road-anomalies` shows no new defect
- [x] Zone some land → `/state/zones` shows occupancy
- [x] Place one service building → `/state/problems` shows it connected (or explains why not)
- [x] Save → verify the `.crp` file appeared
- [x] Reload the save → the city is intact

Two real bugs were found by this phase and fixed — see
[`docs/07-verification.md`](07-verification.md#1c-bugs-found-and-fixed).

## Phase 3 — MCP integration

- [x] Add the server to `~/.config/opencode/opencode.json`
- [x] `opencode mcp list` → `✓ cs1-mayor connected`
- [x] Agent can call the read tools and get real data
- [x] Agent can place a road and verify it

## Next — the office's missing senses

The new priority is everything that lets the office replace an in-game panel or aim the
cursor. Bridge changes are grouped per release (every bridge change costs a game
restart). Verified API signatures for every row are in
[`12-command-surface.md`](12-command-surface.md).

### P1 — `city_overview` (MCP only, no game restart) — **done**

- [x] One MCP tool returning summary + demand + economy + problems + chirps in one
      call — the pulse, without opening a single in-game panel
- [x] `AGENTS.md` rewritten as the operating manual for the office/cursor model
- [x] MCP tool surface restructured: build tools removed, office levers kept,
      `city_request` POST restricted to the office allowlist

### P2 — camera + world→screen projection (bridge, one restart) — **done and verified live**

The glue between office numbers and cursor clicks: the office reads a problem or
anomaly at world `(x,z)`, frames the camera there, and converts the world point to a
screen pixel the cursor can aim at. Without it, every spatial fix starts with the agent
hunting through screenshots.

**Live-game finding (2026-11-11):** `ICities.ICamera` (`IManagers.camera` →
`CameraWrapper`) drives the game's **CinematicCameraController** — the photo-mode
camera. A `SetCameraTarget` call through it changed nothing on screen. The camera the
cursor sees is **`CameraController`** (the free camera), whose state is private fields
(`m_targetPosition`/`m_currentPosition`, `m_targetAngle`/`m_currentAngle` as
`Vector2(vertical°, horizontal°)`, `m_targetSize`/`m_currentSize` = zoom distance, and
the `m_*Velocity` inertia terms). The bridge writes those fields directly on the main
thread — snap or glide, caller's choice.

- [x] A **named main-thread queue** in `src/Dispatch/MainThreadQueue.cs` — the one
      sanctioned exception to the simulation-thread rule, for camera/projection/render
      only; drained by the game's per-frame hook (works while the clock is paused)
- [x] `POST /commands/set-camera` — frame the camera on a world position / read camera
      state via `CameraController` (see the finding above) — includes the ergonomics
      fix: toggle **edge scrolling** live through the game's own
      `CameraController.m_edgeScrolling` `SavedBool` (no restart needed) — **verified
      live both ways**
- [x] `POST /commands/project-point` — world `(x,z[,y])` → Unity screen pixel, client
      window pixel and absolute desktop **cursor** pixel (origin top-left, ready for a
      CUA click) + `onScreen`/`inFront` flags. Runs on the **main thread** through the
      named queue; window geometry via user32 (`Support/WindowGeometry.cs`,
      `EnumWindows` first — `Process.MainWindowHandle` returned 0 live)
- [x] MCP tools `city_camera` and `city_project` (both POST endpoints on the
      `city_request` office allowlist); unit tests added (73 pass)
- [x] **Verify live** (2026-11-11, sandbox city, results in `docs/07-verification.md`
      §1g): camera state read ✓; edge-scroll toggle true→false ✓ (the `SavedBool`
      persists across restarts — it was still `false` after the reload); frame the
      wind turbine at `(-1290, 114, -20)` ✓ (snap, position == target, turbine dead
      centre in the screenshot); project the same point ✓ (`onScreen: true`, cursor
      pixel `(1280, 686)`); a real CUA click at that pixel landed on the turbine
      (selection circle) ✓; `/debug/threads` reports `mainThreadJobsCompleted: 2`,
      `mainThreadLastJobThreadId: 1` (Unity main thread) ≠ `simulationThreadId: 2` ✓.
      Two bugs found and fixed during verification: the first build framed through
      `ICities.ICamera` (cinematic camera — see the finding above), and
      `Process.MainWindowHandle` returned 0 for Cities.exe (replaced by an
      `EnumWindows` scan).

### P3 — load + area reads (bridge)

- [ ] `POST /commands/load` — load a save **[verified: `ISerializableData.LoadGame`]**
- [ ] `GET /state/game-areas` — unlocked map tiles from `GameAreaManager`, so the
      office can tell the cursor where it may build *(was R7; the UI tool path already
      does its own unlocked-tile checks, so this is now an information read, not a
      placement guard)*
- [ ] **Verify**: after load, the summary matches the target save

### P4 — deeper reads (bridge, opportunistic)

- [ ] `GET /state/progress` — current milestone, progress, unlocked `Feature`s
      **[verified: `UnlockManager.GetCurrentMilestone`, `Unlocked(Feature)]`
- [ ] `GET /state/resources` — ore, oil, forest, fertility per cell (where to zone
      specialized industry) **[verified: `ICities.IResource`]** *(terrain *editing*
      is dropped — terraforming belongs to the cursor's land tool if ever wanted)*
- [ ] Info-view data the UI shows but the office cannot read yet: land value,
      pollution, coverage heatmaps — read without opening the panels
- [ ] MCP tools `city_progress`, `city_resources`

*Dropped from the roadmap: `render-view` PNG capture (the CUA channel takes
screenshots), `place-prop` / terrain editing (cursor work), `unlock-milestone` /
`unlock-area` (cheats the office refuses), and the old R6 "UI automation fallback" —
the cursor channel *is* the UI automation now.*

## Done — bridge releases

### R0 — capture `ICities` (Tier 0)

- [x] Capture `IManagers` in `ICities.ThreadingExtensionBase.OnCreated` — stored on
      `AgentBridge.ICitiesManagers`, served as an opaque handle.
      **Live-game finding:** for a mod dropped into the Mods folder, the game loads the
      assembly and constructs `IUserMod` but never instantiates the extension bases —
      neither `OnCreated` nor `OnLevelLoaded` fired — so `EnsureICitiesManagers()`
      lazily constructs the game's own public `ManagersWrapper` on the simulation
      thread as a fallback.
- [x] `GET /state/capabilities` — version (`IApplication.currentVersion`), expansions
      (`SupportsExpansion`), milestone names (`IMilestones.EnumerateMilestones`), the
      current milestone, and the unlocked/locked `UnlockManager.Feature` list.
      Tool: `city_capabilities`.
- [x] Read the simulation clock from `IThreading` (the `clock` block of
      `/state/capabilities`: `simulationTime`, `simulationFrame`, `simulationTick`, …)
      — diff two reads to measure elapsed simulated time

Live-game verification follows the Phase 2 pattern (restart with the new mod → call →
read back); see `docs/07-verification.md`.

### R1 — budgets & policies (Tier 1, easy) — **done and verified live**

- [x] `GET /state/budgets` — per-service day/night budgets (public `GetBudget` readback
      per service; zero-budget rows hidden since they cannot spend)
- [x] `POST /commands/set-budget` — `EconomyManager.SetBudget(Service, SubService, int,
      bool night)`, day and/or night in one call, 0–300 clamp, before/after readback
      **[verified]** *(signatures verified against the live DLL, 2026-11-11)*
- [x] `GET /state/policies` — the 127-value `DistrictPolicies.Policies` catalogue with
      the game's own groups (`Services`, `Taxation`, `CityPlanning`, `Park`, `Event`)
      derived by name, `IsPolicyLoaded`, `IsCityPolicySet` and per-district
      `IsDistrictPolicySet` state **[verified]** *(live: 126 usable policies on a fresh
      city, 116 loaded, groups and `Reserved` correct)*
- [x] `POST /commands/set-policy` — `SetCityPolicy` / `UnsetCityPolicy` for district 0
      (city-wide), `SetDistrictPolicy` / `UnsetDistrictPolicy` per district, and the
      park variants for Park-group policies targeted by park id **[verified]**
      *(live: set → independent readback → unset; unloaded and nonexistent-district
      policies refused cleanly)*. Refuses an unloaded policy with a clear error
      instead of leaving a silent no-op.
- [x] MCP tools `city_budgets`, `city_set_budget`, `city_policies`, `city_set_policy`
- [x] **Verify on a live city** — 2026-11-11, see `docs/07-verification.md` §1e. Budgets
      act → re-read → compare passes (101% trick, day/night split, dryRun integrity,
      baselines restored). Policies pass set/unset cycles with catalogue confirmation;
      note: taxation policies act through the effect path, not the raw slider array.
      **Reload-the-save diff not run** — `/commands/load` does not exist until R4; the
      in-memory readback proof stands, and the state save `R1-verify-budgets-policies`
      is on disk for when `load` ships.


### R2 — transport lines (Tier 1 + Tier 3)

- [x] `GET /prefabs/transport` — the `TransportInfo` catalogue (bus, tram, metro, ferry,
      cargo) and their default vehicles *(the compatible vehicle list per type)*
- [x] `GET /state/transport` — lines, stops, vehicles, active state
- [x] `POST /commands/transport-line` — create / add stop / remove stop / activate /
      rename / set vehicle
      **[verified API, decompiled 2026-11-11: `TransportManager.CreateLine`,
      `TransportLine.AddStop`, `TransportLine.SetActive`; corrections to the earlier
      research: `TransportLine.ReplaceVehicles` is internal — the public vehicle route is
      `TransportManager.AssignSelectedLineVehicle` + `GetVehicleIndex`, and
      `SetLineName` is a one-shot `IEnumerator<bool>` coroutine.]**
- [x] MCP tools `city_transport`, `city_transport_prefabs`, `city_transport_line`
- [x] **Verify live** (2026-11-11, results in `docs/07-verification.md` §1f): catalogue,
      create, stop-chain, loop-close → `complete:true`, remove-stop, activate (day/night),
      rename, set-vehicle, state reads — all pass on the live game; city saved as
      `R2-verify-transport`. Two documented blockers are **city-state, not bridge**: the
      depot cannot stay active (no sewage outlet reachable on the bare map) so no vehicle
      spawned, and population 0 means `passengers` cannot move. Key game behaviour found
      and documented: the game re-snaps non-fixed stop nodes to lane midpoints
      (`TransportLineAI.CheckNodePosition`) — place stops near road-edge midpoints.
- [ ] *Follow-up: re-run vehicle spawn + ridership on a city that has residents and a
      working depot (power + water + sewage).*
- [x] *No `ICities` transport interface exists — confirmed; this is the one big family
      with no supported API.*

### R3 — districts & special areas (Tier 2 + Tier 3)

- [x] `POST /commands/district` — create / rename / paint / erase / specialise / release /
      state **[decompiled 2026-11-11: `DistrictManager.CreateDistrict`, `CreatePark`,
      `SetDistrictName` / `SetParkName` (one-step `IEnumerator<bool>`),
      `DistrictManager.Cell{m_district1..4,m_alpha1..4}` over a 512 × 512 grid at
      `DISTRICTGRID_CELL_SIZE = 19.2f`, `MAX_DISTRICT_COUNT = 128`,
      `GetDistrict(Vector3)` = `cells[z * 512 + x]` of `clamp(x / 19.2 + 256, 0, 511)`.
      Painting reuses the game's own public
      `DistrictTool.ApplyBrush(DistrictTool.Layer, byte id, float radius, Vector3 start,
      Vector3 end)` — it rasterises, does the 4-slot alpha blending and edge smoothing and
      calls `AreaModified` / `ParksAreaModified` itself, so the bridge does not
      reimplement the brush.]**
- [x] One implementation covering districts, parks, airport areas, campus areas and
      pedestrian zones — they are all `DistrictPark` with a `ParkType` **[verified: one
      endpoint, one command, `layer: "district" | "park"`]**
- [x] `GET /state/districts` — both id spaces: names, `parkType` / `parkGroup`, grid and
      world extents, `areaCells`, savings; plus the grid constants. *(The earlier plan
      called this `/state/parks`; it returns districts too, so it was named for both.)*
- [x] MCP tools `city_districts` (read) and `city_district` (write) — reads and writes stay
      separate tools
- [x] **Verify live** (2026-11-11, sandbox city): create → readback ✓ (`named: true` through the
      coroutine); paint → `areaCells.before/after` 51.3→102.7 + `atCenter.district` ✓; specialize
      Forest → Farming replaces it (one per family) ✓; unset clears ✓; rename ✓; erase clears the
      disc (`cellsInBrush` 76→0) ✓; release clears the grid (`paintedCells` → 0) ✓; park layer:
      create NatureReserve (`parkGroup: ParkLife`, `functional: false`) → paint → release ✓.
      *One bug found and fixed during verification: `paint`/`erase` responses were missing the
      outer closing `}` (invalid JSON); fixed in `DistrictCommands.Paint`, rebuilt and
      reinstalled, and re-verified live after a game restart — the response now parses
      cleanly. Practical paint floor (verified live): radius 60 (> 38.4) paints 0 cells,
      radius 77 paints 29.5 — use radius ≥ ~100 so the full-strength core spans 2+ cells.*
- [x] *No `ICities` district interface exists — the whole family is Tier 2.*

### R4–R7 — superseded by the pivot

The old plan (terrain editing, prop placement, milestone/area unlock commands, an
in-MCP UI-automation fallback, and placement guards on the build commands) is retired:
- **Terrain editing, prop placement** → cursor work; the office does not build.
- **`unlock-milestone` / `unlock-area`** → cheats; the office does not offer them.
- **UI automation in the MCP server** → unnecessary; the agent's CUA channel drives
  the game window natively.
- **Placement guards on `build-road`/`place-building`** → moot for the agent (those
  endpoints are no longer exposed); the game's own UI tools validate cursor work.

What survives from those releases is folded into **P2/P3/P4** above (camera +
projection, load + unlocked-tile read, progress/resources/heatmap reads).

## Verification discipline (stands in place of the old evaluation phase)

Each new command is verified the same way the Phase 2 list was: **act → re-read →
compare → expect**, on a live city, before the row in [`12-command-surface.md`](12-command-surface.md)
moves to "Done". The save-integrity rule stays: reload the save and diff the readbacks —
a command that corrupts the save has failed, no matter what its response says.

## Known open questions

| Question | Notes |
|---|---|
| Does the simulation thread step while the game is paused? | **Answered empirically (R0 verification): yes** — `simulationTick` advanced across two reads while paused, so queued commands are drained even paused. Game time itself stays frozen. |
| Can a command be cancelled once running? | `IThreading` has no cancellation hook. Cancellation is best-effort: queued jobs can be dropped, running jobs finish. |
| Is `System.IO.Pipes` worth it after all? | Measured ~12.6 µs vs ~139 µs. Not worth it. Revisit only if HTTP ever becomes a bottleneck (it will not). |
| Does `PrefabCollection<TransportInfo>` enumerate the bus/metro assets? | **Answered (R2): yes** — `GET /prefabs/transport` lists all `TransportInfo` assets with economics and vehicle lists. |
| How is the industry area represented? | `DistrictPark.ParkType` shows campus / airport / pedestrian, not industry. |
| Can a transport line be built without the UI? | **Answered (R2): yes** — `TransportManager.CreateLine` + `TransportLine.AddStop` work headless; the game even re-snaps stops to lane positions afterwards (`TransportLineAI.CheckNodePosition`). |
| Does `ICities.ICamera` aim the camera the player sees? | **Answered (P2, 2026-11-11): no** — `IManagers.camera` (`CameraWrapper`) drives the `CinematicCameraController` (photo mode); framing through it changes nothing on screen. The bridge aims the free `CameraController` via its target fields instead. |
| Does `ISerializableData.LoadGame` work while the dispatcher holds a pending job? | Must be answered before exposing `/commands/load`. Safest rule: refuse to load unless the dispatcher is idle. |
