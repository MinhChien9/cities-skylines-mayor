# Roadmap

The product is the **command surface**: every action the game can perform is one bridge
endpoint, exposed to the agent as one raw MCP tool. The master table — and the unlocked
"next releases" below in table form — lives in [`12-command-surface.md`](12-command-surface.md).

## Done

- [x] **Research** — CS1 internals decompiled with ILSpy, all API signatures verified
- [x] **Architecture decision** — in-game bridge + external MCP server, transport measured
- [x] **Fork** of `Sunwood-ai-labs/cities-skylines1-agent-skill` (MIT) — HTTP server,
      121 KB state builder, and all command implementations reused
- [x] **Threading fix** — `SimulationDispatcher` routes all game work to the simulation
      thread; the upstream's reflection bypass and `CommandQueue` are deleted
- [x] **`/debug/threads`** — runtime proof of which thread executes commands
- [x] **MCP server** — C# / .NET 10, `ModelContextProtocol` 2.4.0, stdio
- [x] **Raw 1:1 tool surface** — 33 tools, one per bridge endpoint, each relaying one
      HTTP call with no server-side logic; `city_request` reaches *any* endpoint, so new
      bridge work is agent-callable before a dedicated tool exists
- [x] **Protocol smoke test** — initialize → tools/list → tools/call verified
- [x] **Knowledge base** — `docs/05-game-mechanics.md`, `docs/06-expansions.md`
- [x] **Agent operating manual** — `AGENTS.md` (raw tools; decisions are the agent's)
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

## Next — full command surface

Grouped by bridge release, because **every bridge change costs a game restart**. The
verified API signatures for every row are in [`12-command-surface.md`](12-command-surface.md).
Each release also adds its MCP tools (or relies on `city_request` in the interim).

### R0 — capture `ICities` (Tier 0)

- [ ] Capture `IManagers` in `ICities.ThreadingExtensionBase.OnCreated`
- [ ] `GET /state/capabilities` — what this running game supports: expansions, version,
      the milestone names, the unlocked `Feature`s
- [ ] Read the simulation clock from `IThreading` instead of parsing the date

### R1 — budgets & policies (Tier 1, easy)

- [ ] `GET /state/budgets` — per-service day/night budgets
- [ ] `POST /commands/set-budget` — `EconomyManager.SetBudget` **[verified]**
- [ ] `GET /state/policies` — the 127-value `Policies` enum, grouped, with city-wide and
      per-district state and whether each is loaded **[verified]**
- [ ] `POST /commands/set-policy` — `SetCityPolicy` / `SetDistrictPolicy` **[verified]**
- [ ] MCP tools `city_budgets`, `city_set_budget`, `city_policies`, `city_set_policy`

### R2 — transport lines (Tier 1 + Tier 3)

- [ ] `GET /prefabs/transport` — the `TransportInfo` catalogue (bus, tram, metro, ferry,
      cargo) and their default vehicles
- [ ] `GET /state/transport` — lines, stops, vehicles, active state
- [ ] `POST /commands/transport-line` — create / add stop / remove stop / activate /
      rename / set vehicle **[verified API: `TransportManager.CreateLine`,
      `TransportLine.AddStop`, `TransportLine.SetActive`]**
- [ ] MCP tool `city_transport_line`
- [ ] **Verify**: a bus line runs, picks up passengers, and the city reads it back
- [ ] *No `ICities` transport interface exists — this is the one big family with no
      supported API.*

### R3 — districts & special areas (Tier 2 + Tier 3)

- [ ] `POST /commands/district` — create / rename / paint / specialise **[verified:
      `DistrictManager.CreateDistrict`, `SetDistrictName`, `m_districtGrid`]**
- [ ] One implementation covering districts, parks, airport areas, campus areas and
      pedestrian zones — they are all `DistrictPark` with a `ParkType` **[verified]**
- [ ] `GET /state/parks` — park/area list with types and extents
- [ ] MCP tool `city_district`
- [ ] **Verify**: `GetDistrict(worldPos)` returns the painted district

### R4 — terrain, resources, view, load (Tier 0)

- [ ] `POST /commands/edit-terrain` — raise / lower / flatten / smooth
      **[verified: `ICities.ITerrain.GetHeights` / `SetHeights` / `RawToHeight`]**
- [ ] `GET /state/resources` — ore, oil, forest, fertility per cell
      **[verified: `ICities.IResource.GetResource` / `SetResource`]**
- [ ] `POST /commands/render-view` — camera framing + PNG capture
      **[verified: `ICities.ICamera.SetCameraTarget` / `GetCameraPosition`]**
- [ ] `POST /commands/load` — load a save **[verified: `ISerializableData.LoadGame`]**
- [ ] A **named main-thread queue** in `SimulationDispatcher` — the one sanctioned
      exception to the simulation-thread rule, for rendering only
- [ ] MCP tools `city_terrain` and `city_view`
- [ ] **Verify**: the agent looks at the image and judges it

### R5 — props, progress, industry (Tier 1)

- [ ] `GET /prefabs/props` — the `PropInfo` catalogue
- [ ] `POST /commands/place-prop` — `PropManager.CreateProp` **[verified]**
- [ ] `GET /state/progress` — current milestone, progress, unlocked `Feature`s
      **[verified: `UnlockManager.GetCurrentMilestone`, `Unlocked(Feature)]**
- [ ] `POST /commands/unlock-milestone` / `POST /commands/unlock-area` — exposed as
      ordinary commands (Tier 0, verified) because the server does not make judgement
      calls; `AGENTS.md` flags them as cheats to avoid without the user's say-so
- [ ] `GET /state/industry` — industrial buildings, their production and inputs
- [ ] MCP tools `city_props`, `city_place_prop`, `city_progress`, `city_unlock`

### R6 — UI automation fallback (tier 3)

- [ ] Move the `game-ui.ps1` primitives into the MCP server (focus, click, type, key,
      screenshot)
- [ ] World→screen projection so the agent can aim at a world position
- [ ] `city_ui` tool — the escape hatch for anything with no API
- [ ] **Verify**: an agent action that only the UI can perform changes the game state

## Verification discipline (stands in place of the old evaluation phase)

Each new command is verified the same way the Phase 2 list was: **act → re-read →
compare → expect**, on a live city, before the row in [`12-command-surface.md`](12-command-surface.md)
moves to "Done". The save-integrity rule stays: reload the save and diff the readbacks —
a command that corrupts the save has failed, no matter what its response says.

## Known open questions

| Question | Notes |
|---|---|
| Does the simulation thread step while the game is paused? | `SimulationStep()` drains the action queue **before** the pause check, and `FixedUpdate()` keeps pulsing the lock — so it likely does. **Verify empirically.** If it does not, paused-mode commands need the game unpaused. |
| Can a command be cancelled once running? | `IThreading` has no cancellation hook. Cancellation is best-effort: queued jobs can be dropped, running jobs finish. |
| Is `System.IO.Pipes` worth it after all? | Measured ~12.6 µs vs ~139 µs. Not worth it. Revisit only if HTTP ever becomes a bottleneck (it will not). |
| Does `PrefabCollection<TransportInfo>` enumerate the bus/metro assets? | Almost certainly — same generic as the road catalogue — but unverified. |
| How is the industry area represented? | `DistrictPark.ParkType` shows campus / airport / pedestrian, not industry. |
| Can a transport line be built without the UI? | If `NetManager` refuses to attach a transport segment to an existing road node, the UI route becomes mandatory. There is no `ICities` transport interface. |
| Does `ISerializableData.LoadGame` work while the dispatcher holds a pending job? | Must be answered before exposing `/commands/load`. Safest rule: refuse to load unless the dispatcher is idle. |
