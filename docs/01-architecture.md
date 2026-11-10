# CS1 Mayor — Architecture

## What this is

A two-part system that lets an AI agent (opencode, Claude Code, …) act as the mayor of a
live **Cities: Skylines (2015 original)** city.

```
┌──────────────────────────────────────────────┐
│  AI agent (opencode / Claude Code / …)       │
│  reads this repo's docs as its knowledge base │
└───────────────────┬──────────────────────────┘
                    │  MCP over stdio
                    │  (newline-delimited JSON-RPC 2.0)
                    ▼
┌──────────────────────────────────────────────┐
│  Cs1Mayor.Mcp  (.NET 10)                     │
│  13 coarse, action-shaped tools              │
│  translates tool calls → HTTP JSON           │
└───────────────────┬──────────────────────────┘
                    │  HTTP 127.0.0.1:32123
                    │  (loopback only)
                    ▼
┌──────────────────────────────────────────────┐
│  SkylinesAgentBridge.dll  (in-game mod)      │
│  ├ HTTP listener thread                      │
│  ├ SimulationDispatcher                      │
│  │   └ IThreading.QueueSimulationThread      │
│  └ CS1 managers (Zone/Net/Building/Economy…)  │
└───────────────────┬──────────────────────────┘
                    │
        Cities: Skylines 1.21.1-f9 (Unity 5.6.7f1)
```

## The one thing you must understand: threads

Cities: Skylines runs **two** threads that matter:

| Thread | Created by | Owns |
|---|---|---|
| **Unity main thread** | Unity runtime | rendering, `MonoBehaviour.Update`, UI, Unity objects |
| **Simulation thread** | `SimulationManager` (`new Thread(SimulationThread)`, named `"Simulation"`) | all city state: `ZoneManager`, `NetManager`, `BuildingManager`, `CitizenManager`, `EconomyManager`, `DistrictManager` |

Every mutation the **game itself** performs goes through `SimulationManager.AddAction(...)`,
which the simulation thread drains at the top of `SimulationStep()`:

```csharp
// from the real game assemblies (verified with ILSpy)
ZoneTool     → Singleton<SimulationManager>.instance.AddAction(EndZoning(...))
BuildingTool → Singleton<SimulationManager>.instance.AddAction(CreateBuilding())
NetTool      → Singleton<SimulationManager>.instance.AddAction(CreateNode(...))
```

`ICities` exposes exactly the two primitives needed:

```csharp
IThreading.QueueMainThread(Action)        // → Unity main thread
IThreading.QueueSimulationThread(Action)  // → simulation thread
```

### The rule

> **All game-state reads and writes happen on the simulation thread.**
> Only camera, UI and rendering work goes to the main thread.

`SimulationDispatcher` enforces this. The HTTP worker thread never touches a game manager
directly — it hands work to the simulation thread and blocks on a `ManualResetEvent`.

### Why this matters so much

Getting this wrong does **not** crash the game. It produces a city that is subtly wrong and
a save you cannot diagnose: torn `ZoneBlock` structs, `FastList` buffers reallocated
underneath you, corrupted linked lists (the game itself logs `"Invalid list detected!"`),
and a broken `m_buildIndexHistory`.

The upstream project this was forked from drained its command queue from
`ThreadingExtensionBase.OnUpdate`, which the game calls from `SimulationManager.Update()` —
i.e. the **Unity main thread**. It then hit the game's own thread guard:

```
InvalidOperationException: "Already in the same thread. Call directly"
```

and "fixed" it by reflecting into private `*Implementation` methods to bypass the guard.
That workaround is what motivated the upstream project's `repair-road-anomalies.ps1` and
`repair-service-overlap.ps1` scripts.

This fork deletes that workaround entirely. Running on the correct thread means the guard is
never hit and direct public API calls work.

### Verifying it

`GET /debug/threads` returns the proof:

```json
{
  "ok": true,
  "probeThreadId": 17,
  "simulationThreadId": 17,
  "mainThreadId": 5,
  "httpWorkerThreadId": 33,
  "runsOnSimulationThread": true,
  "runsOnMainThread": false
}
```

**`probeThreadId` must equal `simulationThreadId` and must NOT equal `mainThreadId`.**
The MCP tool `city_health` surfaces this. This is the Phase 1 go/no-go gate.

## Components

### `src/` — the in-game bridge mod

C# 5 compiled against the game's **own** runtime assemblies in `Cities_Data\Managed\`, so it
links against exactly the BCL the game loads. It builds as a normal MSBuild project,
`src/SkylinesAgentBridge.csproj`, which disables the default framework references and
points at the game's `mscorlib`, `System`, `System.Core`, `ICities`, `Assembly-CSharp`,
`ColossalManaged` and `UnityEngine` instead.

```
src/
├─ Bridge/          the HTTP listener, the route table, the mod hooks, the in-game console
│  └─ Http/         the hand-rolled HTTP request/response types
├─ Dispatch/        the simulation-thread dispatcher      ← the load-bearing file
├─ Commands/        every mutation the agent can perform
├─ State/           every read of city state, plus the anomaly detectors
└─ Support/         the JSON reader and the command result envelope
```

| Folder | Role |
|---|---|
| `Dispatch/SimulationDispatcher.cs` | **the fix.** Routes work onto the simulation thread. |
| `Bridge/ApiRoutes.cs` | The whole HTTP surface: one `(method, path) → handler` table. |
| `Bridge/ApiServer.cs` | The TCP listener. Accepts, dispatches, writes. Nothing else. |
| `Bridge/AgentBridge*.cs` | The mod hooks and the singleton that owns the server. |
| `State/GameState.cs` | Builds the JSON state views (summary, demand, facilities, …). |
| `State/*Collector.cs` | The anomaly detectors: problems, roads, zones, buildings, connections. |
| `Commands/*Commands.cs` | The actual mutations. |
| `Support/JsonUtil.cs`, `Support/CommandResult.cs` | Small helpers with no game dependency. |

Build with `scripts\build-mod.ps1`.

> **Reference-assembly gotcha:** compile against `Cities_Data\Managed\System.Core.dll`,
> **not** `Mono\lib\mono\unity\System.Core.dll`. The "unity" compile profile has no
> `System.IO.Pipes`; the runtime `Managed\` copy does. The two files have different
> SHA-256 hashes.

A file-by-file map is in [`03-code-map.md`](03-code-map.md).

### `mcp-server/` — the MCP server

C# / .NET 10, `ModelContextProtocol` 2.4.0, stdio transport.

| File | Role |
|---|---|
| `Program.cs` | DI setup, stdio transport, server instructions. |
| `BridgeClient.cs` | HTTP client for the bridge, with agent-friendly error wrapping. |
| `CityTools.cs` | The 13 tools. |

### `tests/` — automated checks

`tests/Cs1Mayor.Mcp.Tests/` (xunit) covers the MCP server and the bridge's JSON reader.
The JSON reader is compiled into the test project straight from `src/Support/`, so the
tests exercise the same file the game loads. The in-game mod has no automated harness —
it can only be verified against a running game, which is what
[`07-verification.md`](07-verification.md) is for.

## Tool surface

Thirteen tools, deliberately coarse. opencode runs MCP servers through Code Mode, where a
namespace listing costs one line of context no matter how many tools exist. The real cost
of many tools is *retrieval precision* — the agent has to pick the right one out of a long
list of near-identical descriptions. Eight well-named tools beat forty fuzzy ones.

### Observe

| Tool | Purpose |
|---|---|
| `city_snapshot` | Date, population, money, demand, happiness, services, problems. **Start here.** |
| `city_problems` | Active problem icons with positions and counts. |
| `city_districts` | Districts, zoning occupancy, growable land. |
| `city_infrastructure` | Road/building/zone defect audit. |
| `city_services` | Service facilities and coverage. |
| `city_prefabs` | Exact prefab names available in the running game. |

### Act

| Tool | Purpose |
|---|---|
| `city_build_road` | Build a road segment (reuses nearby nodes). |
| `city_zone` | Paint a zone inside a radius. |
| `city_place_building` | Place a building by prefab name. |
| `city_bulldoze` | Remove a building / segment / node. |
| `city_economy` | Read economy; set tax rates, budgets, speed, pause. |
| `city_save` | Save and verify the save file was written. |
| `city_health` | Bridge liveness + the thread proof. |

### Why HTTP and not a raw socket

Measured inside Unity's own Mono:

| Transport | p50 | p99 |
|---|---|---|
| Named pipe | 12.6 µs | 38.9 µs |
| Raw TCP loopback | 23.0 µs | 130 µs |
| `HttpListener` (Mono) | 139 µs | 850 µs |

Agent reasoning latency is **2–60 seconds** per decision. The transport is ~0.0005% of a
decision cycle. HTTP wins on debuggability (`curl`, `Invoke-RestMethod`, packet captures),
which is worth far more than 127 µs.

## Context economy

The agent's context window, not the IPC layer, is the scarce resource.

| Payload | Size | ≈ Tokens |
|---|---|---|
| 10,000 citizens, raw JSON | 1,115,561 B | 301,502 |
| Same data, aggregated | 437 B | 118 |

That is a **2,554× reduction**. The bridge aggregates because it is the only place with
access to the raw simulation state. Every tool takes an explicit `limit`; paged results
cap at a few hundred rows.

## Performance characteristics

| Concern | Reality |
|---|---|
| Bridge round-trip | ~140 µs — irrelevant next to agent latency |
| In-game state read | O(city size) for some views; aggregated by default |
| **Simulation speed** | **The real bottleneck.** Max 3× (`simulationSpeed` clamps 1–3) |
| Simulated time | Frame-locked to the renderer. 30 in-game days ≈ tens of real minutes |

**Design for ~1 decision per simulated week, not per simulated hour.**

## Verification

Every mutation should be verifiable. The bridge supports `dryRun=true` on build/zone/place
to validate without committing, and `city_infrastructure` reports defects after the fact.
See `docs/07-verification.md`.

## Upstream

Forked from [`Sunwood-ai-labs/cities-skylines1-agent-skill`](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(MIT), commit `70a5116`. See `docs/09-upstream-notes.md` for exactly what was kept, changed,
and why.
