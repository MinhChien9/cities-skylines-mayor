# CS1 Mayor

An MCP server that lets an AI agent play the mayor of a live Cities: Skylines (2015)
city — reading every stat, pulling every lever, and **building through the game's own
tool code**, without a cursor or screenshots.

- **Eyes:** `city_map` renders a top-down map made of game data (land shaded by height,
  water, roads, zones, buildings, locked tiles) with world coordinates on it.
- **Hands:** `city_build_road`, `city_place_building`, `city_zone_segment` run the same
  code the player's tools run — `NetTool.CreateNode` for every network, the
  `BuildingTool` placement rules for buildings — test pass first. Snapping, junctions,
  collision, slope, locked tiles and money are checked exactly as for the player; a
  refusal comes back as the game's own error names plus a hint.
- **Office:** the clock, taxes, budgets, policies, saves, line and district management.

```
Claude Code / opencode ──MCP stdio──▶ Cs1Mayor.Mcp (.NET 10) ──HTTP──▶ SkylinesAgentBridge.dll (in-game)
   plays the city                       41 tools, PNG map renderer        NetTool / BuildingTool rules,
                                                                          simulation-thread dispatch
                                                       Cities: Skylines 1.21.1-f9 · Unity 5.6.7f1
```

---

## Status

| Component | State |
|---|---|
| In-game bridge mod | ✅ **Verified running in a live city** |
| Threading fix | ✅ **Verified** — `probeThreadId == simulationThreadId`, `runsOnMainThread: false` |
| MCP server | ✅ **Verified end-to-end** against the live game |
| Tool surface | ✅ **41 tools** — all stats, the semantic map, game-validated building (roads, networks, zones, buildings, bulldoze), the clock, policies, taxes, budgets, saves |
| Validated building | ✅ **Verified live** — a street grid with T-junctions and crossings, zoning, water/power/sewage, and the first 115 citizens, with no cursor |
| Knowledge base | ✅ `docs/05-game-mechanics.md`, `docs/06-expansions.md` |
| Automated tests | ✅ 53 xunit tests over the MCP server and the bridge's JSON reader |
| opencode wiring | ✅ `✓ cs1-mayor connected` |

**Verified against a live city (10,552 citizens, 1,802 road segments):** state reads,
simulation speed control, taxes, budgets, policies, transport and district management,
and anomaly detection — all correct, and fully reversible. Full log in
[`docs/07-verification.md`](docs/07-verification.md).

---

## Quick start

```powershell
# 1. Build and install the mod
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1

# 2. Build the MCP server and run the tests
dotnet build .\Cs1Mayor.sln -c Release
dotnet test  .\Cs1Mayor.sln

# 3. Smoke test the MCP protocol (works with the game closed)
python .\scripts\smoke-test-mcp.py
```

**Claude Code** picks the server up from the project's `.mcp.json` (approve it on first
launch; it runs the Release build above), and `CLAUDE.md` loads `AGENTS.md` as its
operating manual. Run `claude` in this directory and say "you are the mayor".

Or wire it into opencode:

```json
{
  "mcp": {
    "servers": {
      "cs1-mayor": {
        "type": "local",
        "command": ["dotnet", "C:\\Users\\chichi\\Desktop\\projects\\cs1-mayor\\mcp-server\\bin\\Release\\net10.0\\Cs1Mayor.Mcp.dll"],
        "environment": { "CS1_BRIDGE_URL": "http://127.0.0.1:32123" },
        "codemode": true
      }
    }
  }
}
```

Full walkthrough, including the game path and the thread gate:
[`docs/02-getting-started.md`](docs/02-getting-started.md).

---

## The tools

**41 tools.** All reads are one call each; `city_overview` bundles the pulse
(summary, demand, economy, problems, chirps) so the agent never opens an in-game info
panel; `city_map` is the spatial read. The build tools run the game's own tool code and
always test before they build. `city_request` reaches any GET and the POST endpoints that
have a dedicated tool; the legacy unvalidated `repair-*` / `batch` endpoints are refused.

| Tool | Bridge endpoint |
|---|---|
| `city_health` · `city_threads` | `/health` · `/debug/threads` |
| `city_overview` | `/state/summary` + `/demand` + `/economy` + `/problems` + `/chirps` in one call |
| `city_summary` · `city_demand` · `city_problems` · `city_chirps` · `city_zones` | `/state/summary` · `/demand` · `/problems` · `/chirps` · `/zones` |
| `city_economy` · `city_budgets` · `city_policies` · `city_facilities` · `city_growables` · `city_networks` | matching `/state/*` reads |
| `city_road_anomalies` · `city_building_anomalies` · `city_zone_anomalies` · `city_external_connections` · `city_saves` | matching `/state/*` reads |
| `city_capabilities` · `city_districts` · `city_transport` · `city_transport_prefabs` | `/state/capabilities` · `/state/districts` · `/state/transport` · `/prefabs/transport` |
| `city_set_simulation_speed` · `city_set_tax_rate` · `city_set_budget` · `city_set_policy` · `city_set_building_active` · `city_save` | matching `/commands/*` office levers |
| `city_transport_line` (activate / rename / set-vehicle / remove-stop) | `/commands/transport-line` |
| `city_district` (rename / specialize / release / state) | `/commands/district` |
| `city_map` | `/state/map`, rendered to PNG |
| `city_build_road` · `city_place_building` · `city_move_building` | `/commands/build-road` · `/place-building` · `/move-building` (game-validated) |
| `city_zone_segment` · `city_zone_area` · `city_bulldoze` | `/commands/zone-segment` · `/set-zone` · `/bulldoze` |
| `city_request` | GET any endpoint; POST endpoints that have a dedicated tool |

Full list with parameters in `mcp-server/CityTools.cs` and `mcp-server/BuildTools.cs`; the plan to cover the whole game
in [`docs/12-command-surface.md`](docs/12-command-surface.md).

---

## Documentation

Start at **[`docs/README.md`](docs/README.md)** — it lists every document in the order you
should read it.

| Document | Contents |
|---|---|
| [`docs/README.md`](docs/README.md) | The index and the reading path |
| [`docs/01-architecture.md`](docs/01-architecture.md) | System design, the two threads, and why the fix matters |
| [`docs/02-getting-started.md`](docs/02-getting-started.md) | Build, run, wire up, smoke test |
| [`docs/03-code-map.md`](docs/03-code-map.md) | **Which file owns what** |
| [`docs/04-api-reference.md`](docs/04-api-reference.md) | Every bridge HTTP endpoint |
| [`docs/05-game-mechanics.md`](docs/05-game-mechanics.md) | **The knowledge base** — demand formulas, tax limits, budgets, coverage, level-up rules, milestones |
| [`docs/06-expansions.md`](docs/06-expansions.md) | All 14 expansions: Industry, Campus, Sunset Harbor, Airports, Plazas, Financial Districts, Hotels, Parklife, After Dark, Snowfall, Natural Disasters, Green Cities, Race Day |
| [`docs/07-verification.md`](docs/07-verification.md) | How to check the agent's work and recover from failure |
| [`docs/08-opencode-setup.md`](docs/08-opencode-setup.md) | Wiring into opencode |
| [`docs/09-upstream-notes.md`](docs/09-upstream-notes.md) | What was forked, what was changed, and why |
| [`docs/10-roadmap.md`](docs/10-roadmap.md) | What is built, what is next |
| [`docs/12-command-surface.md`](docs/12-command-surface.md) | The master table of tools and endpoints, and the plan to cover the whole game |
| [`AGENTS.md`](AGENTS.md) | The operating manual for the AI mayor itself |
| [`.github/CONTRIBUTING.md`](.github/CONTRIBUTING.md) | Branch naming, commit format, PR rules |

---

## The one thing that matters most

Cities: Skylines runs city state on a **dedicated simulation thread**, separate from the
Unity render thread. Every mutation the game itself performs goes through
`SimulationManager.AddAction`, which that thread drains.

**Every read and write in this bridge happens on that thread.** The upstream project this
was forked from drained its command queue on the *render* thread, which races the
simulation and silently corrupts the city — the symptom being the game's own
`"Invalid list detected!"` warning and a need for separate road/overlap *repair* scripts.

This fork deletes that workaround. `GET /debug/threads` proves it at runtime.

See [`docs/01-architecture.md`](docs/01-architecture.md#the-one-thing-you-must-understand-threads).

---

## Repository layout

```
cs1-mayor/
├─ src/                       the in-game bridge mod (C# 5, net35)
│  ├─ Bridge/                 HTTP listener, route table, mod hooks, in-game console
│  │  └─ Http/                the hand-rolled HTTP request/response types
│  ├─ Dispatch/               the simulation-thread dispatcher  ← the load-bearing file
│  ├─ Commands/               every mutation the agent can perform
│  ├─ State/                  every read of city state, plus the anomaly detectors
│  └─ Support/                JSON reader and the command result envelope
├─ mcp-server/                the MCP server (C#, .NET 10) — 41 tools over stdio, PNG map renderer
├─ tests/                     xunit tests for the MCP server and the bridge's JSON reader
├─ scripts/                   build, install, launch, smoke test, doc check
├─ docs/                      start at docs/README.md
├─ .github/                   contributing guide, PR template, issue templates, CI
├─ AGENTS.md                  the operating manual for the AI mayor
└─ Cs1Mayor.sln               the solution: mod + MCP server + tests
```

Every file is explained in [`docs/03-code-map.md`](docs/03-code-map.md).

---

## Credits

Forked from
[`Sunwood-ai-labs/cities-skylines1-agent-skill`](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(MIT, © 2026 Sunwood-ai-labs). The HTTP server, the 121 KB state builder, and the command
implementations are that project's work; the threading model, the MCP server, and the
knowledge base are new. Full details in [`docs/09-upstream-notes.md`](docs/09-upstream-notes.md).

Cities: Skylines is © Colossal Order / Paradox Interactive. This project is not endorsed by
or affiliated with them.

## License

MIT — see `LICENSE` (and `LICENSE.upstream` for the original).
