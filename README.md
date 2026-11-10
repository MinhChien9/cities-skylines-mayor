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
| Knowledge base | ✅ `docs/GAME-MECHANICS.md`, `docs/EXPANSIONS.md` |
| opencode wiring | ✅ `✓ cs1-mayor connected` |

**Verified against a live city (10,552 citizens, 1,802 road segments):** state reads,
road building with node reuse, bulldozing with orphan cleanup, zone painting and reverting,
building placement, simulation speed control, and anomaly detection — all correct, and
fully reversible. Full log in `docs/VERIFICATION.md`.

---

## Quick start

### 1. Build and install the mod

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1
```

Compiles `src\*.cs` with Roslyn against the game's own runtime assemblies and copies the
DLL to:

```
%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge\
```

> The mod **auto-loads** — it is picked up as an internal mod without needing to be
> enabled in the content manager. The bridge starts listening as soon as the game
> launches, and reports `levelLoaded:false` until a city is loaded.

If your game lives somewhere else:

```powershell
.\scripts\build-mod.ps1 -GamePath "D:\Epic Games\CitiesSkylines"
```

### 2. Build the MCP server

```powershell
dotnet build mcp-server\Cs1Mayor.Mcp.csproj -c Release
```

### 3. Verify the bridge

```powershell
Invoke-RestMethod http://127.0.0.1:32123/health
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

`debug/threads` must report `runsOnSimulationThread: true`. (Call it twice — the Unity
main thread's id is learned on the next frame.)

### 4. Wire it into opencode

Add to `C:\Users\chichi\.config\opencode\opencode.json`:

```json
{
  "mcp": {
    "servers": {
      "cs1-mayor": {
        "type": "local",
        "command": [
          "dotnet",
          "C:\\Users\\chichi\\Desktop\\projects\\cs1-mayor\\mcp-server\\bin\\Release\\net10.0\\Cs1Mayor.Mcp.dll"
        ],
        "environment": { "CS1_BRIDGE_URL": "http://127.0.0.1:32123" },
        "codemode": true
      }
    }
  }
}
```

Then `opencode mcp list` → `✓ cs1-mayor  connected`.

Full details: `docs/OPENCODE-SETUP.md`.

### 5. Smoke test

```powershell
python scripts\smoke-test-mcp.py
```

Drives the MCP server over stdio exactly like a real client: initialize → tools/list →
tools/call. Works with the game closed.

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

| Document | Contents |
|---|---|
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | System design, the two threads, and why the fix matters |
| [`docs/GAME-MECHANICS.md`](docs/GAME-MECHANICS.md) | **The knowledge base** — demand formulas, tax limits, budgets, coverage, level-up rules, milestones |
| [`docs/EXPANSIONS.md`](docs/EXPANSIONS.md) | All 14 expansions: Industry, Campus, Sunset Harbor, Airports, Plazas, Financial Districts, Hotels, Parklife, After Dark, Snowfall, Natural Disasters, Green Cities, Race Day |
| [`docs/API-REFERENCE.md`](docs/API-REFERENCE.md) | Every bridge HTTP endpoint |
| [`docs/VERIFICATION.md`](docs/VERIFICATION.md) | How to check the agent's work and recover from failure |
| [`docs/UPSTREAM-NOTES.md`](docs/UPSTREAM-NOTES.md) | What was forked, what was changed, and why |
| [`docs/OPENCODE-SETUP.md`](docs/OPENCODE-SETUP.md) | Wiring into opencode |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | What is built, what is next |
| [`AGENTS.md`](AGENTS.md) | The operating manual for the AI mayor itself |

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

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#the-one-thing-you-must-understand-threads).

---

## Repository layout

```
cs1-mayor/
├─ src/                    in-game bridge mod (C#, .NET 3.5 profile)
│  ├─ SimulationDispatcher.cs   ← the threading fix
│  ├─ AgentBridgeThreading.cs   ← thread identity capture
│  ├─ AgentBridge.cs
│  ├─ ApiServer.cs              HTTP routing + /debug/threads
│  ├─ GameState.cs              state views
│  └─ *Commands.cs              the mutations
├─ mcp-server/             MCP server (C#, .NET 10, ModelContextProtocol 2.4.0)
│  ├─ Program.cs
│  ├─ BridgeClient.cs
│  └─ CityTools.cs              the 13 tools
├─ docs/                   architecture, mechanics, expansions, API, verification
├─ scripts/                build, install, smoke test
└─ upstream-skyline-agent-skill/   the forked-from source, kept for reference
```

---

## Credits

Forked from
[`Sunwood-ai-labs/cities-skylines1-agent-skill`](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(MIT, © 2026 Sunwood-ai-labs). The HTTP server, the 121 KB state builder, and the command
implementations are that project's work; the threading model, the MCP server, and the
knowledge base are new. Full details in `docs/UPSTREAM-NOTES.md`.

Cities: Skylines is © Colossal Order / Paradox Interactive. This project is not endorsed by
or affiliated with them.

## License

MIT — see `LICENSE` (and `LICENSE.upstream` for the original).
