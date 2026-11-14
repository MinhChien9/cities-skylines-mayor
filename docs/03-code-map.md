# Code map

Where every file lives, what it owns, and which file you want when you are about to
change something.

---

## How to read this repository

The system is three programs in a row:

```
opencode  ──MCP over stdio──▶  mcp-server/  ──HTTP──▶  src/ (in-game mod)  ──▶  the city
```

Read it in that order. Each layer only knows about the one below it:

- `mcp-server/` knows HTTP and JSON. It has **no** idea what a simulation thread is.
- `src/` knows the game. It has **no** idea what MCP is.

The seam between them is a single HTTP endpoint: `127.0.0.1:32123`.

---

## `src/` — the in-game bridge mod

C# 5, `net35`, compiled against the game's own assemblies. Namespace root:
`SkylinesAgentBridge`.

| Folder | Namespace | Purpose |
|--------|-----------|---------|
| `src/Bridge/` | `SkylinesAgentBridge` | The HTTP listener, the route table, the mod hooks |
| `src/Bridge/Http/` | `SkylinesAgentBridge.Http` | The hand-rolled HTTP request/response types |
| `src/Dispatch/` | `SkylinesAgentBridge.Threading` | The simulation-thread dispatcher |
| `src/Commands/` | `SkylinesAgentBridge.Commands` | Every mutation the agent can perform |
| `src/State/` | `SkylinesAgentBridge.State` | Every read of city state, plus the detectors |
| `src/Support/` | `SkylinesAgentBridge.Support` | The JSON reader and the result envelope |

### `src/Bridge/` — the host

| File | Lines | Owns |
|------|-------|------|
| `AgentBridge.cs` | 150 | The singleton that owns the dispatcher and the server. `Instance`, `LevelLoaded`, `Start()`, `OnLevelLoaded()`, `OnLevelUnloading()`, `EnsureICitiesManagers()`. |
| `AgentBridgeMod.cs` | 31 | The `IUserMod` entry point. The game always constructs it, so it is where the server is started. |
| `AgentBridgeLoading.cs` | 18 | `LoadingExtensionBase` hook — forwards level load/unload to the singleton. |
| `AgentBridgeThreading.cs` | 27 | `ThreadingExtensionBase` hook — pumps the dispatcher per frame and captures the `ICities.IManagers` handle in `OnCreated`. |
| `ApiServer.cs` | 119 | The TCP listener. Accepts connections, hands each to a thread-pool worker, writes the response. Nothing else. |
| `ApiRoutes.cs` | 438 | The whole HTTP surface: one table of `(method, path) → handler`, plus the route descriptions shown in the in-game console. |
| `AgentBridgeNotifier.cs` | 349 | The draggable in-game console panel. Shows what the agent is doing, live. |
| `Http/HttpRequest.cs` | 190 | Parses one HTTP/1.1 request off the socket and reads its query parameters. |
| `Http/HttpResponse.cs` | 55 | Writes a JSON response with the right status line and headers. |

### `src/Dispatch/` — the load-bearing file

| File | Lines | Owns |
|------|-------|------|
| `SimulationDispatcher.cs` | 311 | Runs every game-state read and write on the CS1 **simulation thread**. |
| `MainThreadQueue.cs` | ~200 | The ONE named exception: render-only work (camera framing, world→screen projection) drained on the **Unity main thread** by the game's per-frame hook. It must never touch city state. |

Read this file before you touch anything else. Its class comment explains the whole
threading story. The rule it enforces:

> If the simulation thread is not available, the request **fails**. The dispatcher never
> runs the work inline on the HTTP worker thread.

An earlier version had a "run it inline so the caller still gets a result" fallback. It
silently reintroduced exactly the race the design exists to prevent, and it was removed.

### `src/Commands/` — everything the agent can change

| File | Lines | Endpoint(s) | Owns |
|------|-------|-------------|------|
| `RoadCommands.cs` | 156 | `build-road`, `build-network` | Building one road segment, reusing a nearby node. |
| `BuildingCommands.cs` | 209 | `place-building`, `move-building`, `set-building-active` | Placing, moving and toggling buildings. |
| `ZoneCommands.cs` | 571 | `set-zone`, `repair-zones-to-growables`, `repair-zone-clusters` | Painting zones and the two zone-repair algorithms. |
| `BulldozeCommands.cs` | 73 | `bulldoze` | Removing a building, a segment or a node. |
| `EconomyCommands.cs` | 524 | `set-tax-rate`, `/state/budgets`, `set-budget` | Reading and writing tax rates across every service target, plus service day/night budgets. |
| `PolicyCommands.cs` | 405 | `/state/policies`, `set-policy` | The 127-policy catalogue (name, game group, loaded state, city-wide and per-district state) and setting/unsetting policies city-wide, per district or per park. |
| `TransportCommands.cs` | 712 | `/state/transport`, `/prefabs/transport`, `transport-line` | The transport line model: prefab catalogue with compatible vehicles, line state (stops, vehicles, active flags, passenger counters), and the six line-editor actions (create / add-stop / remove-stop / activate / rename / set-vehicle). |
| `DistrictCommands.cs` | 1118 | `/state/districts`, `district` | Districts and special areas over the game's 512 x 512 cell grid: the two entity lists with names, types, extents and areas, plus create / rename / paint / erase / specialize / release. Painting calls `DistrictTool.ApplyBrush`, the game's own brush. |
| `SaveCommands.cs` | 132 | `save` | Saving the city and listing `.crp` files on disk. |
| `SimulationCommands.cs` | 32 | `set-simulation-speed` | Pause and speed 1–3. |
| `CameraCommands.cs` | ~380 | `set-camera`, `project-point` | Camera framing by writing the free camera's (`CameraController`) target fields directly — the `ICities.ICamera` wrapper drives the *cinematic* camera and cannot aim the player's view — plus the live edge-scrolling toggle and world→screen projection. **Runs on the Unity main thread** through the main-thread queue — see below. |
| `AssetCommands.cs` | 60 | `disable-blocked-assets` | Disabling prefabs the game ships broken. |
| `AssetPolicy.cs` | 39 | — | The blocklist itself. |
| `BatchCommands.cs` | 110 | `batch` | Runs a list of sub-commands in one round trip. |
| `CommandInput.cs` | 27 | — | Reads a coordinate out of a request body. |

### `src/State/` — everything the agent can read

| File | Lines | Endpoint(s) | Owns |
|------|-------|-------------|------|
| `GameState.cs` | 814 | all `/state/*` and `/prefabs/*` | The snapshot builders. One method per payload. |
| `ProblemCollector.cs` | 343 | `state/problems` | Every active problem icon, with positions and counts. |
| `CapabilityCollector.cs` | 200 | `state/capabilities` | Game version, expansions, milestones, unlocked features, simulation clock — read through the `ICities` handle. |
| `RoadAnomalyCollector.cs` | 885 | `state/road-anomalies` | Short stubs, near-miss nodes, dead ends, overlaps, crossings without a node, terrain cliffs. |
| `ZoneAnomalyCollector.cs` | 367 | `state/zone-anomalies` | Mottled and unzoned blocks, and zoned clusters. |
| `BuildingAnomalyCollector.cs` | 159 | `state/building-anomalies` | Buildings sitting on top of roads. |
| `ExternalConnectionCollector.cs` | 230 | `state/external-connections` | The road network split into connected components. |
| `GameInfoHelpers.cs` | 93 | — | Shared predicates over game objects (see below). |
| `ZoneHelpers.cs` | 19 | — | Clamps a zone block's row count to the range the game accepts. |

### `src/Support/` — primitives with no game dependency

| File | Lines | Owns |
|------|-------|------|
| `JsonUtil.cs` | 169 | A regex-based JSON reader. The game has no JSON library and the mod must not depend on assemblies the game does not load. |
| `CommandResult.cs` | 27 | The result envelope every command returns: `Ok`, `Json`, `Error`. |
| `StringUtil.cs` | 11 | Case-insensitive `Contains`. |
| `WindowGeometry.cs` | ~100 | The game window's client rectangle in desktop pixels (user32 `GetClientRect`/`ClientToScreen`) — the translation between Unity screen coordinates and the cursor's desktop coordinates. |

---

## `mcp-server/` — the MCP server

C#, .NET 10, `ModelContextProtocol` 2.4.0. Namespace `Cs1Mayor.Mcp`.

| File | Lines | Owns |
|------|-------|------|
| `Program.cs` | 67 | Host setup, the stdio transport, and the operating instructions sent to the agent. |
| `CityTools.cs` | ~660 | The office tools — every read plus `city_overview`, the non-spatial levers, and the allowlisted `city_request`. Each is a thin relay of HTTP calls. |
| `BuildTools.cs` | ~260 | `city_map` (PNG) and the game-validated build tools: road/network, building place/move, zoning, bulldoze. |
| `MapRenderer.cs` | ~240 | Renders the `/state/map` raster to PNG: palette, height shading, labelled grid, dead-end rings. |
| `BridgeClient.cs` | 181 | The HTTP client, the error translation, and body truncation so errors stay small in the agent's context. |
| `ObsClient.cs` | ~390 | The studio's obs-websocket v5 client: Hello/Identify with SHA-256 auth, requests matched by id, events, reconnect on the next request (a request in flight when the connection drops fails instead of being resent). |
| `StudioSettings.cs` | ~80 | Finds and reads the git-ignored `studio.local.json` written by `scripts/setup-obs.ps1` (OBS url, password, scene). |

### The tool surface (the mayor's office)

| Tool | Bridge call |
|------|-------------|
| `city_health` | `/health` |
| `city_threads` | `/debug/threads` |
| `city_overview` | `/state/summary` + `/demand` + `/economy` + `/problems` + `/chirps` in one call |
| `city_summary` · `city_demand` · `city_problems` · `city_chirps` | `/state/summary` · `/demand` · `/problems` · `/chirps` |
| `city_zones` · `city_economy` · `city_budgets` · `city_policies` · `city_facilities` · `city_growables` · `city_networks` | the matching `/state/*` read |
| `city_road_anomalies` · `city_building_anomalies` · `city_zone_anomalies` | the matching `/state/*-anomalies` read |
| `city_external_connections` · `city_saves` · `city_capabilities` · `city_districts` · `city_transport` · `city_transport_prefabs` | the matching `/state/*` / `/prefabs/*` read |
| `city_set_simulation_speed` | `POST /commands/set-simulation-speed` |
| `city_set_tax_rate` | `POST /commands/set-tax-rate` |
| `city_set_budget` | `POST /commands/set-budget` |
| `city_set_policy` | `POST /commands/set-policy` |
| `city_set_building_active` | `POST /commands/set-building-active` |
| `city_camera` (frame a world point / read camera state / toggle edge scrolling) | `POST /commands/set-camera` (Unity main thread) |
| `city_project` (world point → cursor pixels) | `POST /commands/project-point` (Unity main thread) |
| `city_transport_line` (activate / rename / set-vehicle / remove-stop) | `POST /commands/transport-line` |
| `city_district` (rename / specialize / release / state) | `POST /commands/district` |
| `city_save` | `POST /commands/save` |
| `city_request` | GET any endpoint; POST only the office allowlist |

The **build endpoints are deliberately not tools**: roads, zones, buildings,
bulldozing, stop placement and district painting are spatial work the agent does with
its cursor through the game's own UI (see `AGENTS.md`). The bridge keeps the endpoints
([`04-api-reference.md`](04-api-reference.md)) — they are just not agent-reachable.

### The future `mcp-server/` layout

No files are planned beyond what exists: a new **read** endpoint only needs a new thin
tool in `CityTools.cs` — and `city_request` already reaches it before that tool lands.

| File | Owns |
|------|------|
| `CityTools.cs` | grows with the bridge: one tool per new read endpoint |
| `src/Dispatch/MainThreadQueue.cs` (bridge) | **built (P2)** — render-only work: camera framing and the world→screen projection. It may not mutate city state. The one named exception to the thread rule. |

And the bridge gains one named exception to the thread rule:

| File | Owns |
|------|------|
| `src/Dispatch/MainThreadQueue.cs` | **built (P2)** — render-only work: camera framing, projection. It may not mutate city state. |

---

## `tests/` — automated checks

`tests/Cs1Mayor.Mcp.Tests/` (xunit, .NET 10).

| File | Covers |
|------|--------|
| `BridgeClientTests.cs` | JSON parsing, error translation, unreachable-bridge handling |
| `CityToolsTests.cs` | Every tool's endpoint mapping, limit clamping, request bodies, error shape |
| `JsonUtilTests.cs` | The bridge's regex JSON reader, linked straight from `src/Support/` |
| `ObsClientTests.cs` | `ObsClient` auth, request/response, errors, timeouts, reconnect, events; `StudioSettings` parsing |
| `StubObsServer.cs` | A raw-TCP obs-websocket v5 stub the OBS tests run against (no ASP.NET dependency) |

The JSON reader is compiled into the test project from `src/Support/JsonUtil.cs`
rather than copied, so the tests exercise the exact file the game loads.

The in-game mod has no automated test harness — it can only be tested against a running
game. That is what [`07-verification.md`](07-verification.md) is for.

---

## `scripts/`

| Script | Does |
|--------|------|
| `build-mod.ps1` | Builds `src/` against the game's assemblies and installs the DLL |
| `install-mod.ps1` | Build + copy, then prints the thread-proof instructions |
| `start-game.ps1` | Launches Cities: Skylines and waits for the bridge to pass the thread gate |
| `restart-game.ps1` | Saves the city, rebuilds and installs the mod, relaunches and resumes the save |
| `setup-obs.ps1` | Enables obs-websocket, creates the "CS1 Mayor" scene and recording settings, writes `studio.local.json` |
| `smoke-test-mcp.py` | Drives the MCP server over stdio exactly like a real client |
| `game-ui.ps1` | OS-level mouse/keyboard injection into the game window |
| `check-api-docs.py` | Fails if an endpoint in the API reference is missing from the route table |

---

## "I want to change X. Where do I go?"

| I want to… | Go to |
|-------------|-------|
| Add a new MCP tool | `mcp-server/CityTools.cs`, then add the endpoint in `src/Bridge/ApiRoutes.cs` and the handler in `src/Commands/` |
| Add a new bridge endpoint | `src/Bridge/ApiRoutes.cs` (route + description), the handler in `src/Commands/` or `src/State/`, and a row in `docs/04-api-reference.md` |
| Change what the snapshot reports | `src/State/GameState.cs` |
| Change how an anomaly is detected | the matching `src/State/*Collector.cs` |
| Change a game mutation | the matching `src/Commands/*Commands.cs` |
| Change how the agent is told to behave | `mcp-server/Program.cs` (the instructions) and `AGENTS.md` (the manual) |
| Change how a session remembers things | `mcp-server/SessionStore.cs` (goal, journal, checklist) |
| Change how the agent waits or what a delta contains | `mcp-server/WaitEngine.cs` |
| Change what the agent is forbidden to do | `mcp-server/PolicyGuard.cs` |
| Add a capability a player has but the agent does not | [`12-command-surface.md`](12-command-surface.md) — pick a tier, then work the matrix row |
| Change the threading model | `src/Dispatch/SimulationDispatcher.cs` — read [`01-architecture.md`](01-architecture.md) first |
| Change how the game is built | `src/SkylinesAgentBridge.csproj` and `scripts/build-mod.ps1` |
| Change the AI's mayorial knowledge | `docs/05-game-mechanics.md`, `docs/06-expansions.md` |

---

## Naming rules used in this repository

- One public concept per file. If a file needs a "and" in its description, split it.
- A file is named after what it owns, not after the pattern it implements.
  `GameInfoHelpers` owns the predicates; `GameState` owns the payloads.
- Shared helpers live in `Support/` (no game dependency) or `State/GameInfoHelpers.cs`
  (game-dependent), and there is exactly one copy of each.
