# Roadmap

## Done

- [x] **Research** — CS1 internals decompiled with ILSpy, all API signatures verified
- [x] **Architecture decision** — in-game bridge + external MCP server, transport measured
- [x] **Fork** of `Sunwood-ai-labs/cities-skylines1-agent-skill` (MIT) — HTTP server,
      121 KB state builder, and all command implementations reused
- [x] **Threading fix** — `SimulationDispatcher` routes all game work to the simulation
      thread; the upstream's reflection bypass and `CommandQueue` are deleted
- [x] **`/debug/threads`** — runtime proof of which thread executes commands
- [x] **MCP server** — C# / .NET 10, `ModelContextProtocol` 2.4.0, 13 tools, stdio
- [x] **Protocol smoke test** — initialize → tools/list → tools/call verified
- [x] **Knowledge base** — `docs/05-game-mechanics.md`, `docs/06-expansions.md`
- [x] **Agent operating manual** — `AGENTS.md`
- [x] **Documentation** — architecture, API reference, verification, opencode setup
- [x] **Project structure** — `src/` split into `Bridge` / `Dispatch` / `Commands` / `State` /
      `Support`; `GameState.cs` split into focused collectors; HTTP types and the route
      table extracted from `ApiServer.cs`
- [x] **Real project files** — `src/SkylinesAgentBridge.csproj`, `mcp-server/*.csproj`,
      `tests/*.csproj` and a `Cs1Mayor.sln`; the mod is now an ordinary MSBuild project
- [x] **Automated tests** — 42 xunit tests over the MCP server and the bridge's JSON reader
- [x] **CI** — build, test, MCP protocol smoke test and an API-docs/route-table consistency
      check on every push
- [x] **Contributor conventions** — branch naming, conventional commits, PR and issue
      templates copied from the opencode project
- [x] **Product design** — the play loop and the parity target are specified in
      [`11-play-loop.md`](11-play-loop.md) and [`12-capability-parity.md`](12-capability-parity.md);
      the CS1 API surface they depend on is verified against the live assemblies
- [x] **`ICities` found** — the game ships a supported modding API (`ICities.dll`)
      covering terrain, resources, save/load, map tiles, milestones, camera, buildings
      and the simulation clock. The bridge already implements `ThreadingExtensionBase`,
      so `IManagers` is one field away. Most of the parity target is Tier 0.

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
- [x] Agent can call `city_snapshot` and get real data
- [x] Agent can place a road and verify it

## Phase 4 — the play loop

*The point of the product: the user states a goal, the agent plays until it is met.*
Design: [`11-play-loop.md`](11-play-loop.md).

### 4a. Session memory — MCP server only, no game restart

- [ ] `SessionStore` — file-backed under `%LOCALAPPDATA%\cs1-mayor\sessions\`
      (goal, checklist, journal, last snapshot, settings)
- [ ] `city_goal` — set / get / update the goal and its checklist / check whether the
      target metrics are met
- [ ] `city_journal` — append a decision entry; read the last N
- [ ] Unit tests for the store, the journal format and the goal checker

### 4b. Waiting and deltas — MCP server only

- [ ] `city_wait(days | until, timeoutSeconds)` — poll `/state/summary` server-side,
      never block the simulation thread
- [ ] Curated metric set (population, cash, income, expenses, demand, happiness,
      problem counts, milestone) and a diff against the last snapshot
- [ ] Detect `paused` / `no-game` and report them instead of spinning
- [ ] Auto-resume (speed 3) when the session says so
- [ ] Fold the delta into `city_snapshot`'s sibling so the agent has **one** way to
      check progress

### 4c. The agent's operating system

- [ ] Rewrite the server `instructions` in `Program.cs` for goal mode
- [ ] Rewrite `AGENTS.md` for goal mode: goal → plan → batch → wait → verify → record
- [ ] Rewrite the `AGENTS.md` in-game equivalent (the notifier panel shows the agent's
      intent, so it should show the current goal and step)

### 4d. Delta quality

- [ ] Problem deltas resolve *which* problems went away, not just how many
- [ ] Milestone progress in the delta
- [ ] Coverage percentage per service in the delta

## Phase 5 — capability parity

*The agent can do anything a player can do.* Matrix and API notes:
[`12-capability-parity.md`](12-capability-parity.md).

> **The `ICities` discovery.** Cities: Skylines ships a **supported** modding API in
> `ICities.dll` — terrain, natural resources, save/load, map tiles, milestones, the
> camera, building placement, the simulation clock. The bridge already implements
> `ICities.ThreadingExtensionBase`, so capturing `IManagers` is a one-field change that
> unlocks most of the parity surface with **no reflection and no UI automation**.
> That is release **R0**, and it comes first.

Grouped by bridge release, because **every bridge change costs a game restart**.

### R0 — capture `ICities` (Tier 0)

- [ ] Capture `IManagers` in `ICities.ThreadingExtensionBase.OnCreated`
- [ ] `GET /state/capabilities` — what this running game supports: expansions, version,
      the milestone names, the unlocked `Feature`s
- [ ] Read the simulation clock from `IThreading` instead of parsing the date
- [ ] MCP tool `city_progress` starts here

### R1 — budgets & policies (Tier 1, easy)

- [ ] `GET /state/budgets` — per-service day/night budgets
- [ ] `POST /commands/set-budget` — `EconomyManager.SetBudget` **[verified]**
- [ ] `GET /state/policies` — the 127-value `Policies` enum, grouped, with city-wide and
      per-district state and whether each is loaded **[verified]**
- [ ] `POST /commands/set-policy` — `SetCityPolicy` / `SetDistrictPolicy` **[verified]**
- [ ] MCP tools `city_budget`, `city_policy`

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

### R4 — terrain, resources, view, saves (Tier 0)

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
- [ ] `GET /state/industry` — industrial buildings, their production and inputs
- [ ] Milestone unlock command — **built for parity, refused by the server** (§6 of
      [`11-play-loop.md`](11-play-loop.md))
- [ ] MCP tools `city_progress`, `city_industry`

### R6 — UI automation fallback (tier 3)

- [ ] Move the `game-ui.ps1` primitives into the MCP server (focus, click, type, key,
      screenshot)
- [ ] World→screen projection so the agent can aim at a world position
- [ ] `city_ui` tool — the escape hatch for anything with no API
- [ ] **Verify**: an agent action that only the UI can perform changes the game state

## Phase 6 — evaluation

- [ ] Headless-ish harness: run at 3× for N simulated months, then score
- [ ] Metrics: population, cash curve, service coverage %, road connectivity, problem
      icons by type, milestone progress
- [ ] **Save integrity check** — reload the save and diff the snapshot. A city that scores
      well but produces a corrupt save has failed.
- [ ] Compare against a hand-built reference city
- [ ] Goal-completion harness: give the agent a goal, run it unattended, score the
      result — this is the product's acceptance test

## Known open questions

| Question | Notes |
|---|---|
| Does the simulation thread step while the game is paused? | `SimulationStep()` drains the action queue **before** the pause check, and `FixedUpdate()` keeps pulsing the lock — so it likely does. **Verify empirically.** If it does not, paused-mode commands need the game unpaused. |
| Can a command be cancelled once running? | `IThreading` has no cancellation hook. Cancellation is best-effort: queued jobs can be dropped, running jobs finish. |
| Should milestones be unlockable? | **Decided: no.** The agent earns them. The capability exists for player parity and the server refuses it by default. See [`11-play-loop.md`](11-play-loop.md) §6. |
| Is `System.IO.Pipes` worth it after all? | Measured ~12.6 µs vs ~139 µs. Not worth it. Revisit only if HTTP ever becomes a bottleneck (it will not). |
| Does `PrefabCollection<TransportInfo>` enumerate the bus/metro assets? | Almost certainly — same generic as the road catalogue — but unverified. |
| How is the industry area represented? | `DistrictPark.ParkType` shows campus / airport / pedestrian, not industry. |
| Can a transport line be built without the UI? | If `NetManager` refuses to attach a transport segment to an existing road node, the UI route becomes mandatory. There is no `ICities` transport interface. |
| Does `ISerializableData.LoadGame` work while the dispatcher holds a pending job? | Must be answered before exposing `/commands/load`. Safest rule: refuse to load unless the dispatcher is idle. |
