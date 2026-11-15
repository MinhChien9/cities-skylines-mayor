# Getting started

From nothing to an agent that builds in your city in about ten minutes.

## Requirements

| | |
|---|---|
| OS | Windows 10 or 11 (the build scripts are PowerShell) |
| Game | Cities: Skylines (2015) 1.21.1 or newer, Steam or Epic |
| Runtime | [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0): runtime to run the server, SDK to build it |
| Agent | Any MCP client: Claude Code, opencode, … |
| Optional | Python 3.8+ for the smoke test and the doc check |

## 1. Install the mod

Download `cs1-mayor-mod-<version>.zip` from the
[latest release](https://github.com/MinhChien9/cities-skylines-mayor/releases/latest) and copy
`SkylinesAgentBridge.dll` to:

```
%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge\
```

Start the game. Make sure **Skylines Agent Bridge** is enabled in **Content Manager → Mods**.
The bridge listens on `http://127.0.0.1:32123`, on localhost only. It answers `/health`
from the main menu, and every other endpoint once a city is loaded.

In a city, the mod shows a small **API console**: a floating log of the bridge calls.
`_` folds it to its title bar, `CLR` empties it and `X` hides it completely (remembered
across restarts). To show it again, tick **Show the API console** in
**Options → Skylines Agent Bridge**.

## 2. Install the MCP server

Unpack `cs1-mayor-mcp-<version>.zip` from the same release, for example to
`C:\tools\cs1-mayor-mcp`. The server is `Cs1Mayor.Mcp.dll`, and it runs with `dotnet`.

## 3. Connect your agent

**Claude Code**

```powershell
claude mcp add cs1-mayor -- dotnet C:\tools\cs1-mayor-mcp\Cs1Mayor.Mcp.dll
```

You can also put it in a project's `.mcp.json`:

```json
{
  "mcpServers": {
    "cs1-mayor": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["C:/tools/cs1-mayor-mcp/Cs1Mayor.Mcp.dll"]
    }
  }
}
```

**opencode**: add this to `~/.config/opencode/opencode.json`, or to `opencode.json` in a project:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "cs1-mayor": {
        "type": "local",
        "command": ["dotnet", "C:\\tools\\cs1-mayor-mcp\\Cs1Mayor.Mcp.dll"]
      }
    }
  }
}
```

Run `opencode mcp list` and check that it shows `✓ cs1-mayor connected`.

**Settings** (all optional, set as environment variables of the server process):

| Variable | Default | Meaning |
|---|---|---|
| `CS1_BRIDGE_URL` | `http://127.0.0.1:32123` | Where the bridge listens |
| `CS1_ACTIVITY_DIR` | `activity/` in the repo, else the current folder | The activity log (one JSON line per build or office action) |
| `CS1_MAP_DIR` | `maps/` next to it | Where `city_map saveAs=...` writes PNGs |

## 4. Check it works

Load a city, then:

```powershell
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

`runsOnSimulationThread` must be `true`, and `probeThreadId` must equal
`simulationThreadId`. The first call after loading can show `mainThreadId: 0`; call it
again. If the check fails, stop: commands would race the simulation and could damage
the save ([architecture](architecture.md#the-threading-rule)).

Then ask the agent to run `city_health`, `city_threads` and `city_overview`.

## 5. Play

Give the agent the manual: point it at [AGENTS.md](../AGENTS.md) (Claude Code loads it
automatically in this repo through `CLAUDE.md`). Then say something like:

> You are the mayor. Start with `city_overview` and `city_map`. Tell me what is wrong with
> this city before you change anything.

[Tools](tools.md) lists what the agent can do.

## Build from source

You need the .NET 10 SDK and the game installed, because the mod compiles against the
game's own assemblies.

```powershell
git clone https://github.com/MinhChien9/cities-skylines-mayor.git
cd cities-skylines-mayor

# the mod: builds against <game>\Cities_Data\Managed and installs into the mods folder
.\scripts\build-mod.ps1                          # -GamePath "D:\Games\CitiesSkylines" if needed

# the MCP server
dotnet build mcp-server/Cs1Mayor.Mcp.csproj -c Release
```

The game path defaults to `D:\Epic Games\CitiesSkylines`. Pass `-GamePath`, or set
`CITIES_SKYLINES_PATH`. In this repo, `.mcp.json` already points Claude Code at the
Release build.

Helper scripts:

| Script | What it does |
|---|---|
| `scripts/start-game.ps1` | Starts the game and waits until the bridge passes the thread check |
| `scripts/restart-game.ps1` | Saves, rebuilds and installs the mod, relaunches, and resumes the save. Use it after any change under `src/`: the game only loads mods at startup |
| `scripts/smoke-test-mcp.py` | Drives the server over stdio like a client. Works with the game closed |

## Troubleshooting

| Symptom | Fix |
|---|---|
| `/health` refuses the connection | The game is not running, or the mod is not installed or not enabled |
| Everything except `/health` returns HTTP 409 | No city is loaded yet |
| `runsOnSimulationThread` is `false` | A broken bridge build. Reinstall the release DLL and [open an issue](https://github.com/MinhChien9/cities-skylines-mayor/issues/new/choose) |
| "simulation thread is not available" | The game is loading or unloading a map. Wait, then re-read the state before retrying |
| The client says the server failed to start | Check the path to `Cs1Mayor.Mcp.dll` and that `dotnet --list-runtimes` shows .NET 10. In opencode, use the full path to `dotnet.exe` if you see `spawn dotnet ENOENT` |
| `dotnet build` says "file is locked" | The running MCP server holds the DLL. Restart the client, or build in Debug |
| The mod build cannot find the managed assemblies | Wrong game path: pass `-GamePath` |
