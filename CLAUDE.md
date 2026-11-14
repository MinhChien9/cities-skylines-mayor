# CLAUDE.md

**Read `PLAN.md` first, every session.** It holds the goal, the phases, the decision
log and the environment state, and its session protocol is binding: work the next
unchecked item, update `PLAN.md` in the same PR, and ask the user to `/compact` at
every task boundary.

@PLAN.md

This repository has two audiences, and both instruction sets live in `AGENTS.md` and
`docs/`:

- **Playing the city** (the `cs1-mayor` MCP tools are connected): follow the operating
  manual in `AGENTS.md`. Start every session with `city_health`, `city_threads`,
  `city_overview` and `city_map`.
- **Changing the code**: read `docs/README.md` for the reading order,
  `.github/CONTRIBUTING.md` for branch, commit and PR rules, and
  `docs/01-architecture.md` before touching `src/` — every game-state read and write
  must stay on the simulation thread.

@AGENTS.md

## Working on this repo

- The MCP server is wired through `.mcp.json` (project scope). It runs the Release
  build, so build it first: `dotnet build mcp-server/Cs1Mayor.Mcp.csproj -c Release`.
- After any change under `src/`, the game must reload the mod:
  `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\restart-game.ps1`
  (saves the city, rebuilds and installs the mod, relaunches, resumes the save).
- Line endings are mixed on purpose: most of `src/` is CRLF, everything else is LF.
  Keep each file's existing ending (`git ls-files --eol`); new `src/` files use CRLF.
- Verify before a PR: `scripts/build-mod.ps1 -NoInstall` (0 warnings),
  `dotnet test tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj`,
  `python scripts/smoke-test-mcp.py`, `python scripts/check-api-docs.py`, and a live
  check against the running game (`GET /debug/threads`).
