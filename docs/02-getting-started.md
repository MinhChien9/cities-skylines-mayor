# Getting started

From a clean checkout to an agent that is building roads in your city.

---

## 0. Prerequisites

| You need | Why |
|----------|-----|
| Cities: Skylines (2015 original), v1.21.1-f9 or newer | The game the mod runs in |
| The .NET 10 SDK | Builds the MCP server and the mod |
| Python 3.8+ | Runs the smoke test |
| opencode (or Claude Code) | The agent |

All 14 expansions are supported. The bridge discovers what is actually installed at
runtime — see [`06-expansions.md`](06-expansions.md).

---

## 1. Build and install the mod

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1
```

This compiles `src\` against the game's **own** runtime assemblies (in
`Cities_Data\Managed`) and copies the DLL to:

```
%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge\
```

The game path defaults to `D:\Epic Games\CitiesSkylines`. If yours is elsewhere:

```powershell
.\scripts\build-mod.ps1 -GamePath "D:\Games\CitiesSkylines"

# or once per session
$env:CITIES_SKYLINES_PATH = "D:\Games\CitiesSkylines"
```

> The mod **auto-loads**. It is picked up as an internal mod without needing to be
> enabled in the content manager. The bridge starts listening as soon as the game
> launches and reports `levelLoaded:false` until a city is loaded.

`build-mod.ps1` is a thin wrapper around a normal MSBuild build of
`src/SkylinesAgentBridge.csproj`. You can also build it from an IDE or directly:

```powershell
dotnet build .\src\SkylinesAgentBridge.csproj -c Release
```

The project file disables the default framework references and points at the game's
own `mscorlib`, `System`, `System.Core`, `ICities`, `Assembly-CSharp`,
`ColossalManaged` and `UnityEngine` instead. That is what makes the output loadable
by the game's Mono runtime.

---

## 2. Build the MCP server

```powershell
dotnet build .\mcp-server\Cs1Mayor.Mcp.csproj -c Release
```

Or the whole solution, which also builds the mod and the tests:

```powershell
dotnet build .\Cs1Mayor.sln -c Release
```

> The solution includes the mod project, so it needs a local Cities: Skylines
> installation. Build `mcp-server` on its own if you do not have one.

> If the MCP server is currently running (it is, if opencode has it connected), the
> build will fail with "file is locked". Restart opencode, or build in Debug, while
> it is running.

---

## 3. Verify the bridge

Start Cities: Skylines and load a city, then:

```powershell
Invoke-RestMethod http://127.0.0.1:32123/health
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

`/debug/threads` is the **go/no-go gate**. It must report:

```json
{
  "runsOnSimulationThread": true,
  "runsOnMainThread": false
}
```

`probeThreadId` must equal `simulationThreadId` and must **not** equal
`mainThreadId`. If it does not, stop — see
[`01-architecture.md`](01-architecture.md#the-one-thing-you-must-understand-threads).

> Call `/debug/threads` twice. The Unity main thread's id is learned on the next
> frame, so the first call can report `mainThreadId: 0`.

---

## 4. Wire it into opencode

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

Then:

```
opencode mcp list     →  ✓ cs1-mayor  connected
```

Full details, including the Claude Code variant and the schema notes:
[`08-opencode-setup.md`](08-opencode-setup.md).

---

## 5. Smoke test

```powershell
python .\scripts\smoke-test-mcp.py
```

This drives the MCP server over stdio exactly like a real client:
`initialize` → `tools/list` → `tools/call`. It works with the game closed, and it
fails loudly if a tool disappears or the handshake breaks.

---

## 6. Run the game

```powershell
.\scripts\start-game.ps1
```

Starts Cities: Skylines, waits for the bridge to answer `/health`, and asserts the
thread proof before you let the agent touch anything.

---

## 7. Let the mayor work

Open opencode in this directory and say something like:

> You are the mayor. Start with `city_health` and `city_snapshot`. Tell me what is
> wrong with this city before you change anything.

The agent's operating manual is [`../AGENTS.md`](../AGENTS.md). Read it if you want to
know what it is allowed to do, and why.

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
│
├─ mcp-server/                the MCP server (C#, .NET 10) — 13 tools over stdio
├─ tests/                     xunit tests for the MCP server and the bridge's JSON reader
├─ scripts/                   build, install, launch, smoke test
├─ docs/                      this folder — start at docs/README.md
├─ .github/                   contributing guide, PR template, issue templates, CI
├─ AGENTS.md                  the operating manual for the AI mayor
└─ Cs1Mayor.sln               the solution: mod + MCP server + tests
```

Every file is explained in [`03-code-map.md`](03-code-map.md).

---

## Common problems

| Symptom | Cause | Fix |
|---------|-------|-----|
| `GET /health` refuses the connection | The game is not running, or the mod DLL is not in the mods folder | Start the game; re-run `build-mod.ps1` |
| `/health` works but `/state/summary` returns 409 | No city is loaded yet | Load a city |
| `runsOnSimulationThread` is false | The dispatcher fell back to the wrong thread | This is a bug — see [`07-verification.md`](07-verification.md) |
| `opencode mcp list` shows the server as failed | The DLL path is wrong, or the build is locked | Rebuild, restart opencode, re-check the path |
| The build says the managed assemblies were not found | Wrong game path | Pass `-GamePath` or set `$env:CITIES_SKYLINES_PATH` |
| `dotnet build` fails with "file is locked" | The MCP server is running from that output folder | Restart opencode or build in Debug |
