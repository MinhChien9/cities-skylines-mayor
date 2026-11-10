# CS1 Mayor

An MCP server that lets an AI agent be the **mayor of a live Cities: Skylines (2015) city**.

The agent reads the city, plans, and acts — building roads, zoning land, placing services,
setting taxes, and saving — through a purpose-built in-game bridge that executes every
command on the game's own simulation thread.

```
opencode / Claude Code  ──MCP stdio──▶  Cs1Mayor.Mcp (.NET 10)  ──HTTP──▶  SkylinesAgentBridge.dll (in-game)
                                                                                 │
                                                               Cities: Skylines 1.21.1-f9 · Unity 5.6.7f1
```

---

## Status

| Component | State |
|---|---|
| In-game bridge mod | ✅ **Verified running in a live city** |
| Threading fix | ✅ **Verified** — `probeThreadId == simulationThreadId`, `runsOnMainThread: false` |
| MCP server | ✅ **Verified end-to-end** against the live game |
| All 13 tools | ✅ Verified: read, build, zone, place, bulldoze, economy, save, health |
| Knowledge base | ✅ `docs/05-game-mechanics.md`, `docs/06-expansions.md` |
| Automated tests | ✅ 42 xunit tests over the MCP server and the bridge's JSON reader |
| opencode wiring | ✅ `✓ cs1-mayor connected` |

**Verified against a live city (10,552 citizens, 1,802 road segments):** state reads,
road building with node reuse, bulldozing with orphan cleanup, zone painting and reverting,
building placement, simulation speed control, and anomaly detection — all correct, and
fully reversible. Full log in [`docs/07-verification.md`](docs/07-verification.md).

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

Then wire it into opencode:

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

| Tool | What it does |
|---|---|
| `city_snapshot` | Date, population, money, demand, happiness, services, problems. **Start here.** |
| `city_problems` | Active problem icons with positions and counts. |
| `city_districts` | Districts, zoning occupancy, growable land. |
| `city_infrastructure` | Road / building / zone defect audit. |
| `city_services` | Service facilities and coverage. |
| `city_prefabs` | Exact prefab names available in the running game. |
| `city_build_road` | Build a road segment (reuses nearby nodes). |
| `city_zone` | Paint a zone inside a radius. |
| `city_place_building` | Place a building by prefab name. |
| `city_bulldoze` | Remove a building / segment / node. |
| `city_economy` | Read economy; set tax rates, budgets, speed, pause. |
| `city_save` | Save and verify the save file was written. |
| `city_health` | Bridge liveness + the thread proof. |

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
├─ mcp-server/                the MCP server (C#, .NET 10) — 13 tools over stdio
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
