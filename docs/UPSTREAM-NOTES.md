# Upstream Notes

This project is a **fork and continuation** of
[`Sunwood-ai-labs/cities-skylines1-agent-skill`](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(MIT licensed), pinned at commit `70a5116215a4c83820134cca870aae1934e92d87` ("Finalize
v0.3.0 release QA inventory").

The upstream is genuinely good work and saved a lot of time. This document records exactly
what was kept, what was changed, and why — so the divergence stays auditable.

---

## Why fork instead of build from scratch

The upstream already had, working and battle-tested:

- A complete HTTP server + routing (`ApiServer.cs`, 24 KB) with a hand-rolled HTTP/1.1 parser
- A **very** large state builder (`GameState.cs`, 121 KB) covering problems, facilities,
  networks, road anomalies, building anomalies, zone anomalies, growables, chirps, economy,
  demand, zones, saves, external connections, and prefab catalogues
- Command implementations for road building, zoning, building placement/move, bulldozing,
  economy, simulation speed, assets, and saving
- A JSON helper (`JsonUtil.cs`) written for the .NET 3.5 profile
- A PowerShell script suite (build, start, resume, inspect, repair, save, smoke test)
- An in-game API console overlay

Rebuilding all of that from scratch would have cost weeks and added no value. The **one**
thing that was wrong was the threading model, and that was fixable in a focused way.

---

## What was kept

| File | Note |
|---|---|
| `ApiServer.cs` | Kept, minus the queue call site (see below). Added `/debug/threads`. |
| `GameState.cs` | Kept verbatim. The state-building logic is thread-agnostic. |
| `JsonUtil.cs`, `StringUtil.cs`, `CommandResult.cs`, `AssetPolicy.cs`, `ZoneHelpers.cs` | Kept verbatim. |
| `RoadCommands.cs` | Kept. |
| `ZoneCommands.cs` | Kept. |
| `BuildingCommands.cs` | Kept, minus the `GameThreadHelpers` call. |
| `BulldozeCommands.cs` | Kept, minus the `GameThreadHelpers` calls. |
| `EconomyCommands.cs` | Kept. |
| `SaveCommands.cs`, `SimulationCommands.cs`, `AssetCommands.cs`, `BatchCommands.cs` | Kept. |
| `AgentBridgeNotifier.cs` | Kept. |

The upstream's `scripts/` were kept as reference but **rewritten**, because they hard-code a
Steam install path and use `csc.exe` from .NET Framework 3.5/4.0 rather than Roslyn.

---

## What was changed

### 1. The threading model — the load-bearing fix

**Upstream:**

```
HTTP worker  →  CommandQueue.RunSync()  →  enqueue + block on ManualResetEvent
Unity main   →  ThreadingExtensionBase.OnUpdate  →  queue.Process(4)  →  EXECUTES HERE
```

`SimulationManager.Update()` calls `m_ThreadingWrapper.OnUpdate(...)`, and `Update()` is a
Unity callback — so the upstream executed **city mutations on the Unity main thread**, while
the **simulation thread** concurrently mutated the same `FastList` buffers.

**This fork:**

```
HTTP worker  →  SimulationDispatcher.RunSync()  →  IThreading.QueueSimulationThread(job)
simulation  →  SimulationManager.AddAction queue  →  drained in SimulationStep()  →  EXECUTES HERE
```

**Why it matters.** The failure mode is not a crash. It is:

- torn reads of `ZoneBlock` structs (a `FastList<ZoneBlock>` where `ZoneBlock` is a struct)
- `FastList` buffers reallocated while being iterated
- corrupted `m_nextGridBlock` linked lists — the game itself logs `"Invalid list detected!"`
  from `ZoneTool.ApplyZoning` when this happens
- corrupted `m_buildIndexHistory`, which the game uses to decide whether a build index is
  recent

Result: a city that is subtly wrong and a save that cannot be diagnosed.

### 2. Deleted the reflection bypass

Upstream `GameThreadHelpers.cs` did this:

```csharp
catch (InvalidOperationException ex) {
    if (!IsSameThreadException(ex)) throw;
    MethodInfo method = typeof(BuildingManager).GetMethod(
        "ReleaseBuildingImplementation", BindingFlags.Instance | BindingFlags.NonPublic, ...);
    method.Invoke(manager, new object[] { id });
}
```

`IsSameThreadException` matches `"Already in the same thread. Call directly"`, which is
thrown by `ColossalFramework.Threading.Dispatcher.CheckAccessLimitation()`:

```csharp
protected override void CheckAccessLimitation() {
    if (m_CurrentDispatcher == this)
        throw new InvalidOperationException("Already in the same thread. Call directly");
}
```

That exception was a **symptom** of calling from the wrong thread. The upstream reflected
into private `*Implementation` methods to route around the game's own guard — which both
bypassed validation *and* confirmed the design was wrong.

**This fork deletes `GameThreadHelpers.cs` entirely.** On the correct thread the public API
works directly:

```csharp
manager.ReleaseBuilding(id);
manager.ReleaseSegment(id, keepNodes);
```

This is also why the upstream needed `repair-road-anomalies.ps1` and
`repair-service-overlap.ps1` — scripts that repair damage caused by placing objects on the
wrong thread. Those scripts are the fingerprint of the bug.

### 3. New: `SimulationDispatcher`

A dedicated class that owns the dispatch policy, with:

- `RunSync(Func<CommandResult>, timeoutMs)` — run on the simulation thread, block the caller
- `Post(Func<CommandResult>)` — fire-and-forget on the simulation thread
- `PostMainThread(Action)` — for Unity work (UI, camera, rendering)
- `Pump()` — called from the per-frame hook; only *transfers* ownership, never mutates
- Thread identity capture (`SimulationThreadId`, `MainThreadId`) for the diagnostics endpoint

It also **never blocks the Unity main thread** waiting for a result — the HTTP worker thread
blocks instead — so the frame loop cannot deadlock.

### 4. New: `/debug/threads`

The Phase 1 go/no-go gate. Returns the managed ids of the main thread, the simulation
thread, the HTTP worker thread, and the thread that actually executed a probe, plus a
boolean `runsOnSimulationThread`.

Exposed to the agent as `city_health`.

### 5. New: build script

`scripts/build-mod.ps1` compiles with **Roslyn** (`dotnet csc.dll`) against the game's own
runtime assemblies in `Cities_Data\Managed\`.

The upstream used `C:\Windows\Microsoft.NET\Framework\v3.5\csc.exe` (or `v4.0.30319`),
which is the .NET Framework compiler and produces an assembly that may not match Unity's
runtime expectations. Compiling with Roslyn against `Cities_Data\Managed\mscorlib.dll` and
friends produces an assembly that links against exactly the BCL the game loads.

### 6. New: the MCP server

The upstream exposes **only** an HTTP API and ships a **Codex skill** (`SKILL.md`) — it
targets OpenAI Codex, not MCP.

This fork adds `mcp-server/`: a real MCP server over stdio using
`ModelContextProtocol` 2.4.0, with 13 action-shaped tools, so it works with opencode,
Claude Code, and any other MCP client.

### 7. New: the knowledge base

The upstream has no game-mechanics reference; the agent is expected to know the game or
look it up. This fork adds `docs/GAME-MECHANICS.md` and `docs/EXPANSIONS.md`, both derived
from decompiled game code, with the exact formulas and thresholds.

---

## Deliberately NOT done

| Idea | Why not |
|---|---|
| Screenshot-based state reading | The upstream's own README says *"stop relying on screenshots for city state"*. Reading demand bars off a PNG is strictly worse than reading the underlying float. Vision is reserved for aesthetics. |
| A general-purpose "raw internal API" bridge | Exposing arbitrary internal calls over HTTP turns every agent mistake into save corruption. The tool surface stays intent-shaped. |
| Named pipes instead of HTTP | Measured ~12.6 µs vs ~139 µs — irrelevant against 2–60 s agent latency. HTTP's debuggability wins. |
| Vision as a control input | Off by default; operator thumbnail only. |
| UI automation | The upstream already had to P/Invoke `mouse_event` to click the launcher's Play button. Brittle and low value. |

---

## Attribution

The upstream is MIT licensed, copyright © 2026 Sunwood-ai-labs. The MIT notice is retained
in `LICENSE.upstream`. This fork remains MIT.
