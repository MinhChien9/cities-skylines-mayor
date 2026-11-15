# Development

How the code is laid out, how to build and test it, and how work flows from an issue to a
release.

## Code map

```
src/                 the bridge mod (C# 5, net35, compiled against the game's assemblies)
  Bridge/            HTTP listener, route table, mod entry points, in-game console
  Dispatch/          SimulationDispatcher (the threading rule) and MainThreadQueue
  Commands/          everything that changes the city
  State/             everything that reads the city
  Support/           JSON reader, result envelope, unlock checks (no game state)
mcp-server/          the MCP server (.NET 10)
tests/               xunit tests for the server and the bridge's JSON reader
scripts/             build, install, start/restart the game, smoke test, doc check, release
```

**Bridge (`src/`)**

| File | Owns |
|---|---|
| `Bridge/ApiRoutes.cs` | The whole HTTP surface: one table of `(method, path) → handler` |
| `Bridge/ApiServer.cs`, `Bridge/Http/*` | The TCP listener and the minimal HTTP request/response types |
| `Bridge/AgentBridge*.cs` | Mod entry (`IUserMod`), load/unload hooks, the per-frame pump, the in-game console |
| `Dispatch/SimulationDispatcher.cs` | Runs every game read and write on the simulation thread. **Read this before touching anything** |
| `Dispatch/MainThreadQueue.cs` | The one exception: camera and projection work on the Unity main thread |
| `Commands/RoadCommands.cs` | `build-road`: networks through `NetTool.CreateNode`, auto-splitting at nodes and crossings |
| `Commands/BuildingPlacement.cs`, `BuildingCommands.cs` | The `BuildingTool` placement rules; place, move and toggle buildings |
| `Commands/Collateral.cs`, `GameValidation.cs` | What a build would remove or removed; money checks and error fields |
| `Commands/ZoneCommands.cs` | Zone by segment and the zone brush |
| `Commands/DistrictCommands.cs`, `TransportCommands.cs` | Districts and special areas; transit lines |
| `Commands/EconomyCommands.cs`, `PolicyCommands.cs` | Taxes and budgets; policies |
| `Commands/AreaCommands.cs`, `SaveCommands.cs`, `SimulationCommands.cs`, `BulldozeCommands.cs`, `CameraCommands.cs` | Map tiles, saves, the clock, bulldozing, the camera |
| `State/GameState.cs` | Most `/state/*` and `/prefabs/*` payloads |
| `State/*Collector.cs` | Problems, capabilities, utilities, scenario goals, the map raster, external connections, and the road, zone and building anomaly detectors |
| `Support/JsonUtil.cs` | A small JSON reader (the game has no JSON library) |
| `Support/UnlockCheck.cs` | Milestone gating for prefabs and zones |

**MCP server (`mcp-server/`)**

| File | Owns |
|---|---|
| `Program.cs` | Host, stdio transport, the instructions sent to the agent |
| `CityTools.cs` | Read tools, office levers, `city_overview`, `city_request` with its POST allowlist |
| `BuildTools.cs`, `MapRenderer.cs` | `city_map` (PNG) and the build tools |
| `BridgeClient.cs` | The HTTP client and error translation |
| `ActivityLog.cs`, `ActivityTools.cs` | The activity log written after every mutating call, and `city_activity` |

**Where do I change…**

| I want to… | Go to |
|---|---|
| Add a tool | A handler in `src/Commands/` or `src/State/`, its route in `ApiRoutes.cs`, a row in [api.md](api.md), the tool in `CityTools.cs` or `BuildTools.cs`, a test, and a line in [tools.md](tools.md) |
| Change what the agent is told | `mcp-server/Program.cs` (instructions) and `AGENTS.md` (the manual) |
| Change game knowledge | [game-mechanics.md](game-mechanics.md), [expansions.md](expansions.md) |
| Change the threading model | Don't, unless an issue says so. Read [architecture.md](architecture.md) first |

## Build and test

```powershell
.\scripts\build-mod.ps1 -NoInstall                               # the mod; must report 0 warnings
dotnet build mcp-server/Cs1Mayor.Mcp.csproj -c Release           # the server
dotnet test  tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj  # unit tests
python scripts/smoke-test-mcp.py                                 # MCP handshake + tool list
python scripts/check-api-docs.py                                 # api.md matches the route table
```

After any change under `src/`, run `scripts/restart-game.ps1`: the game loads mods only at
startup. Then check `GET /debug/threads` and reproduce the issue live
([verification.md](verification.md)). CI runs the server build, the tests, the smoke test
and the doc check on every PR. The mod needs the game's assemblies, so it is built locally.

Conventions: most of `src/` uses CRLF line endings and everything else LF. Keep each
file's ending; new `src/` files use CRLF.

## Workflow: issues first

Every change starts as a [GitHub issue](https://github.com/MinhChien9/cities-skylines-mayor/issues)
and ends as a squash-merged PR that closes it. Nothing is noted for later in a doc.

1. **Open** an issue with a form: *Bug report*, *Feature request*, *Game finding* or *Task*.
   One problem per issue. The tool call and its response are worth more than any analysis.
2. **Triage:** one type label (`bug`, `feature`, `finding`, `task`, `documentation`), one or
   more `area:` labels, a priority (`p1` next, `p2` soon, `p3` later), and the milestone if it
   should ship in the next release. If information is missing, add `needs-info`: such issues
   go stale after 3 weeks.
3. **Branch** per issue, at most three words (`pipe-node-snap`). Reproduce, fix, verify.
4. **PR:** title `type(scope): summary`, body with `Closes #n`, what changed and how you
   verified it. The triage workflow labels PRs that miss the title format or the issue link.
5. **Merge** with squash. Add a line under `## [Unreleased]` in [CHANGELOG.md](../CHANGELOG.md)
   in the same PR.
6. **Release** when the milestone is done: [releasing.md](releasing.md).

**While playing:** if a tool misbehaves or you learn something new about the game, open
an issue (`gh issue create -R MinhChien9/cities-skylines-mayor ...`). Don't work around it
silently or add a note to `PLAN.md`. The PR that closes a *finding* records the lesson
where it belongs.

**Done means:** the issue's expected behaviour holds and the PR shows it, the build and
checks above pass, the live thread check passes (for bridge changes), the docs are updated
if a tool or behaviour changed, and the CHANGELOG has a line.

Branch names, commit format and the PR template are in
[CONTRIBUTING](../.github/CONTRIBUTING.md).
