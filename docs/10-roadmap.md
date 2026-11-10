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

## Phase 4 — goal-driven loop

The agent needs a *goal*, not a toolbelt. Proposed first goals:

1. **Grow to 1,000 people** without negative cash and with no service coverage below 90%.
2. **Zero road anomalies** and no disconnected crossings.
3. **Full milestone chain** to Megalopolis (100k) — exercises every expansion.

Design points:

- The **deterministic controller** (threshold policy, no LLM in the hot loop) should run
  between LLM planning passes, not instead of them. The LLM proposes a plan once per
  simulated month; the controller executes it as bounded, validated actions.
- Every action must be **idempotent or guarded**.
- A **decision journal** (JSONL) should record every action, its rung, its cost, and its
  verification result.

## Phase 5 — capability expansion

In rough priority order:

| Area | Why |
|---|---|
| **Transport lines** | Bus/metro lines are the biggest remaining unlock for city quality. Partly UI-driven. |
| **Policies** | ~90 policies affect demand, income and happiness. Currently only readable. |
| **Service budgets** | Setter exists for tax but not for per-service budgets. The 101% trick needs it. |
| **Milestone unlocking** | Needed to exercise later expansions (Campus, Airport, Finance, Hotels). |
| **Area (tile) unlocking** | `IAreas.UnlockArea(x, z)` — needed for large maps. |
| **Districts** | Painting districts and specialisations unlocks high-density zones and policy levers. |
| **Props / landscaping** | Trees, benches, streetlights — land value and noise control. |
| **Screenshot** | `Camera.Render` → `RenderTexture` → `ReadPixels` → PNG. **Operator thumbnail only**, never a control input. |
| **Industry chains** | Specialized areas, extractors, processors. The deepest content in the game. |
| **Airport / Campus** | Their own area types and building chains. |

## Phase 6 — evaluation

- [ ] Headless-ish harness: run at 3× for N simulated months, then score
- [ ] Metrics: population, cash curve, service coverage %, road connectivity, problem
      icons by type, milestone progress
- [ ] **Save integrity check** — reload the save and diff the snapshot. A city that scores
      well but produces a corrupt save has failed.
- [ ] Compare against a hand-built reference city

## Known open questions

| Question | Notes |
|---|---|
| Does the simulation thread step while the game is paused? | `SimulationStep()` drains the action queue **before** the pause check, and `FixedUpdate()` keeps pulsing the lock — so it likely does. **Verify empirically.** If it does not, paused-mode commands need the game unpaused. |
| Can a command be cancelled once running? | `IThreading` has no cancellation hook. Cancellation is best-effort: queued jobs can be dropped, running jobs finish. |
| Should milestones be unlockable? | It is a cheat that invalidates economic evaluation. Decide explicitly. |
| Is `System.IO.Pipes` worth it after all? | Measured ~12.6 µs vs ~139 µs. Not worth the loss of `curl` debuggability. Revisit only if HTTP ever becomes a bottleneck (it will not). |
