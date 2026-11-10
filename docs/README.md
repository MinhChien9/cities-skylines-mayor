# CS1 Mayor — Documentation

Everything you need to read, run, change and verify this project, in the order you
should read it.

## The reading path

| # | Document | Read it when you want to… |
|---|----------|---------------------------|
| 01 | [Architecture](01-architecture.md) | understand how the agent, the MCP server and the in-game bridge fit together, and why the simulation thread is the whole ballgame |
| 02 | [Getting started](02-getting-started.md) | build the mod, run the game, wire the MCP server into opencode, and smoke-test the lot |
| 03 | [Code map](03-code-map.md) | find the file that owns the behaviour you want to change |
| 04 | [API reference](04-api-reference.md) | call the bridge over HTTP, or add an endpoint |
| 05 | [Game mechanics](05-game-mechanics.md) | know how demand, taxes, coverage and level-up actually work before making a mayorial decision |
| 06 | [Expansions](06-expansions.md) | use one of the 14 DLCs |
| 07 | [Verification](07-verification.md) | prove your change did not corrupt the city |
| 08 | [opencode setup](08-opencode-setup.md) | connect the MCP server to opencode or Claude Code |
| 09 | [Upstream notes](09-upstream-notes.md) | understand what was forked from `cities-skylines1-agent-skill` and what changed |
| 10 | [Roadmap](10-roadmap.md) | see what is built and what is next |

Two more files live outside this folder:

| File | What it is |
|------|-----------|
| [`../AGENTS.md`](../AGENTS.md) | The operating manual the AI mayor follows. Read it if you are writing prompts for the agent or changing the tools. |
| [`../.github/CONTRIBUTING.md`](../.github/CONTRIBUTING.md) | Branch naming, commit format, PR rules. |

## If you only read three things

1. [`01-architecture.md`](01-architecture.md) — *The one thing you must understand:
   threads*. Everything else depends on it.
2. [`03-code-map.md`](03-code-map.md) — which file owns what.
3. [`../AGENTS.md`](../AGENTS.md) — the rules the agent plays by.

## The three commands that prove it works

```powershell
# 1. The mod compiles against the real game assemblies
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-mod.ps1 -NoInstall

# 2. The MCP server builds and the unit tests pass
dotnet build .\mcp-server\Cs1Mayor.Mcp.csproj
dotnet test  .\tests\Cs1Mayor.Mcp.Tests\Cs1Mayor.Mcp.Tests.csproj

# 3. The MCP protocol handshake works (game may be closed)
python .\scripts\smoke-test-mcp.py
```

`dotnet build .\Cs1Mayor.sln` builds all three projects at once, but it includes the mod
project, which needs a local Cities: Skylines installation.

## The one rule

**Every read and write of game state happens on the CS1 simulation thread.**

If a change mutates the city from the HTTP worker thread or the Unity main thread, it
races the simulation and silently corrupts the save. `src/Dispatch/SimulationDispatcher.cs`
is the only sanctioned way in, and `GET /debug/threads` is the runtime proof.

Nothing in this repository may break that rule without a very good reason and a very
clear explanation.
