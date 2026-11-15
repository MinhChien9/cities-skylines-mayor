# Architecture

## The pieces

```
AI agent (Claude Code, opencode, …)
   │  MCP over stdio (JSON-RPC 2.0)
   ▼
MCP server: mcp-server/, .NET 10
   │  47 tools: thin relays that aggregate and shape the bridge's answers;
   │  renders city_map to PNG; writes the activity log
   │  HTTP + JSON, 127.0.0.1:32123 (localhost only)
   ▼
Bridge mod: src/, C# 5 / .NET 3.5, loaded by the game
   ├ HTTP listener (worker threads; never touches game state)
   ├ SimulationDispatcher → runs every read and write on the simulation thread
   ├ MainThreadQueue      → camera and screen projection only
   └ the game's managers (Net, Building, Zone, District, Economy, …)
```

| Part | Responsibility |
|---|---|
| **Bridge** (`src/`) | Every game read and write. Build commands call the game's own tool code. Large reads are aggregated in the game, so payloads stay small |
| **MCP server** (`mcp-server/`) | The tool surface the agent sees, the map PNG, the activity log. Holds no game state |
| **Tests** (`tests/`) | The MCP server against a stubbed bridge, plus the bridge's JSON reader |

## The threading rule

Cities: Skylines keeps all city state on a **simulation thread**, separate from Unity's main
(render) thread. The game's own tools never write city state directly: they queue the work
with `SimulationManager.AddAction`, and the simulation thread runs it.

> **Every game read and write runs on the simulation thread.**
> Only camera and screen-projection work runs on the main thread, through `MainThreadQueue`,
> and it never touches city state.

`SimulationDispatcher` enforces this with `IThreading.QueueSimulationThread`. The HTTP
worker hands work over and waits. If the simulation thread is unavailable (the game is
loading), the request fails; it is never run inline.

Breaking the rule does not crash the game. It corrupts the city slowly: torn structs,
broken linked lists (the game logs `Invalid list detected!`), and a save that is wrong in
ways you cannot trace. `GET /debug/threads` (`city_threads`) proves the rule at runtime:
`probeThreadId == simulationThreadId != mainThreadId`.

## Building through the game's tools

The agent does not write roads or buildings into the game's data structures. It calls the
same code the player's tools call, on the thread those tools use:

- **Networks:** `NetTool.CreateNode` with the tool's control points, as a test pass and then
  a build. Ends snap to nodes and segments. A straight road is split where it crosses existing
  roads, so each crossing becomes a real junction. Ground roads under water are refused.
- **Buildings:** a port of `BuildingTool`'s placement rules: zone-grid snapping,
  road-facing, shoreline snapping, `CheckSpace`, slope and money. Sub-buildings are included.
- **Zoning:** a road's own zone blocks, by side and depth, or a brush with the zone tool's
  no-overwrite rule.
- **Guards the game's tools leave to the UI:** the real treasury is checked even with the
  Unlimited Money mod, milestone-locked prefabs and zones are refused, and builds report
  what they would demolish (`wouldRemove`) or removed (`removed`).

As a result, a refusal comes back as the game's own error names, and anything the agent can
build, a player could have built.

## Design choices

- **HTTP, not a socket or a pipe.** A round trip costs about 0.14 ms; an agent decision takes
  seconds. Being able to debug with `curl` is worth far more.
- **Aggregate in the game.** Raw citizen data for a 10k city is about 300k tokens; the
  aggregated summary is about 100. Every list takes a `limit`.
- **Simulation speed is the real limit.** It tops out at 3×, so thirty game days take tens of
  real minutes. Plan about one decision per game week, not per hour.
- **The activity log is a plain file.** One JSON line per mutating call. Other programs
  (the video studio) follow it without coupling to the server.

## History

This repo is forked from
[Sunwood-ai-labs/cities-skylines1-agent-skill](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(commit `70a5116`). The fork kept the HTTP server and the first state readers and commands.
It then:

- ran all commands on the simulation thread, replacing a render-thread queue and its
  reflection bypass, plus the "repair" scripts that tried to fix the damage afterwards
- added the MCP server and the knowledge base (`docs/game-mechanics.md`, `docs/expansions.md`)
- replaced direct data writes with building through the game's own tool code (after a
  cursor-driven phase), and added the semantic map

The full story is in the git history and the decision log in `PLAN.md`.
