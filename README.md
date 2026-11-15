# CS1 Mayor

[![CI](https://github.com/MinhChien9/cities-skylines-mayor/actions/workflows/ci.yml/badge.svg)](https://github.com/MinhChien9/cities-skylines-mayor/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/MinhChien9/cities-skylines-mayor)](https://github.com/MinhChien9/cities-skylines-mayor/releases/latest)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Let an AI agent play **Cities: Skylines** (the 2015 original) as the city's mayor. An
[MCP](https://modelcontextprotocol.io) server gives the agent 47 tools to read the city,
pull every lever and build. Building goes through the game's own tool code, so the agent
needs no cursor and no screenshots.

```
AI agent ──MCP (stdio)──▶ MCP server (.NET 10) ──HTTP, localhost──▶ bridge mod (inside the game)
```

- **See:** `city_overview` returns the whole pulse in one call. `city_map` draws a top-down
  map from game data, with world coordinates on it.
- **Build:** roads, pipes, power lines, buildings, zoning and bulldozing use the same rules
  as the player's tools. A refused build returns the game's own error and a hint.
- **Run the city:** time, taxes, budgets, policies, transit lines, districts, map tiles,
  scenario goals and saves.
- **Safe for the save file:** every game read and write happens on the game's simulation
  thread, and `city_threads` proves it.

## Quick start

You need Windows, Cities: Skylines 1.21+, the [.NET 10 runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
and an MCP client (Claude Code, opencode, …).

1. **Install the mod.** Download `cs1-mayor-mod-*.zip` from the
   [latest release](https://github.com/MinhChien9/cities-skylines-mayor/releases/latest) and copy
   `SkylinesAgentBridge.dll` to
   `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Addons\Mods\SkylinesAgentBridge\`.
   Then enable it in **Content Manager → Mods**.
2. **Unpack the MCP server** (`cs1-mayor-mcp-*.zip`) anywhere, e.g. `C:\tools\cs1-mayor-mcp`.
3. **Connect your agent.** For Claude Code:

   ```powershell
   claude mcp add cs1-mayor -- dotnet C:\tools\cs1-mayor-mcp\Cs1Mayor.Mcp.dll
   ```

4. **Load a city** and check the bridge: `Invoke-RestMethod http://127.0.0.1:32123/debug/threads`
   must report `"runsOnSimulationThread": true`.
5. Ask the agent: *"You are the mayor. Start with `city_overview` and `city_map`, and
   tell me what this city needs."*

To set up opencode, build from source, or troubleshoot, see
[docs/getting-started.md](docs/getting-started.md).

## Documentation

| | |
|---|---|
| [Getting started](docs/getting-started.md) | Install, connect Claude Code or opencode, build from source, troubleshooting |
| [Tools](docs/tools.md) | All 47 tools, grouped by job, and how building works |
| [AGENTS.md](AGENTS.md) | The operating manual the AI mayor follows |
| [Game mechanics](docs/game-mechanics.md) | Demand, taxes, coverage, level-up and happiness, from the game's code |
| [Architecture](docs/architecture.md) | How the pieces fit, and the threading rule |
| [Bridge API](docs/api.md) | The HTTP endpoints behind the tools |
| [Development](docs/development.md) | Code map, build, test, and the issue-first workflow |

The full index is in [docs/README.md](docs/README.md).

## Related

[cities-skylines-video](https://github.com/MinhChien9/cities-skylines-video) records the
AI mayor's play and turns it into videos. It reads this server's activity log
(`activity/<session>.jsonl`).

## Contributing

Every change starts as an [issue](https://github.com/MinhChien9/cities-skylines-mayor/issues).
Read [CONTRIBUTING](.github/CONTRIBUTING.md) before opening a PR. The release history is in
[CHANGELOG.md](CHANGELOG.md), and security reports go through [SECURITY.md](SECURITY.md).

## Credits and license

MIT ([LICENSE](LICENSE)). This project is forked from
[Sunwood-ai-labs/cities-skylines1-agent-skill](https://github.com/Sunwood-ai-labs/cities-skylines1-agent-skill)
(MIT, [LICENSE.upstream](LICENSE.upstream)). The HTTP server and the first state and command
code come from there; the threading model, the MCP server and validated building are new.
Cities: Skylines is © Colossal Order / Paradox Interactive. This project is not affiliated
with or endorsed by them.
