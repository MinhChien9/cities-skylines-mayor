# Contributing to CS1 Mayor

Thanks for helping build CS1 Mayor. This guide covers the conventions for branches,
commits, and pull requests. They are deliberately the same as the
[opencode](https://github.com/anomalyco/opencode) project, so that contributions move
between the two codebases without friction.

## The kinds of change we accept

- Bug fixes
- New bridge commands or MCP tools
- Performance improvements
- Environment-specific fixes (game paths, SDK versions, mod loading)
- Documentation improvements

Large product-level features need a design conversation first. If you are unsure whether
a change fits, open an issue and ask before writing the code.

## Branch names

Use a short branch name of **at most three words, separated by hyphens**. Do not use
slashes, and do not use type prefixes such as `feat/` or `fix/`.

```
project-structure        ✅ two words, hyphenated
fix-road-node-reuse      ✅ three words
feat/road-tooling        ❌ slash and type prefix
docs-update-readme       ✅
```

Branches are based on `main`. Rebase rather than merge when you are asked to update.

## Commits and PR titles

Use conventional commit-style messages and PR titles: `type(scope): summary`.

| Type       | Use for                                          |
| ---------- | ------------------------------------------------ |
| `feat`     | A new bridge command, MCP tool, or capability   |
| `fix`      | A bug fix                                        |
| `docs`     | Documentation only                               |
| `chore`    | Build, CI, tooling, dependency housekeeping     |
| `refactor` | Restructuring with no behaviour change           |
| `test`     | Tests only                                       |

Scopes are optional. Use the affected area when it helps: `src`, `mcp`, `docs`, `scripts`,
`bridge`, `dispatch`.

```
docs: reorganise the documentation into a reading order
refactor(src): split game state builders into focused modules
fix(dispatch): never execute game work off the simulation thread
feat(mcp): expose city_services coverage numbers
```

Keep the summary in the imperative mood and under ~70 characters.

### Commit body

Anything that is not obvious from the diff goes in the body. For this project, that
usually means *why* — the simulation thread rules in `AGENTS.md` are not obvious and a
future reader will need the reason.

```
refactor(src): split game state builders into focused modules

GameState.cs had grown to 2,800 lines and mixed three unrelated
concerns: the snapshot builders, the road anomaly detector, and the
problem collector. Splitting them makes each file readable on its own
and gives each detector a name you can grep for.

No behaviour change: every JSON payload is byte-identical.
```

## Pull requests

Complete the [pull request template](./pull_request_template.md). It is short on purpose.

- **Keep it focused.** One logical change per PR. Unrelated cleanups belong in their own
  PR — the linked-issue bot and human reviewers both work better with small diffs.
- **Link issues.** Use `Fixes #123` or `Closes #123` when the PR resolves an issue.
  Documentation and refactor PRs are exempt.
- **Explain the problem first, then the fix.** Two short paragraphs is plenty.
- **Say how you verified it.** For bridge changes, that means the thread gate in
  `docs/07-verification.md` plus the specific commands you ran. For MCP changes, the
  smoke test is enough.
- **Do not paste AI-generated walls of text.** Long templated descriptions get ignored.

### Verifying your change

```bash
# 1. The MCP server and the tests build, and the tests pass
dotnet build ./mcp-server/Cs1Mayor.Mcp.csproj -c Release
dotnet test  ./tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj

# 2. The MCP protocol handshake still works (the game may be closed)
python ./scripts/smoke-test-mcp.py

# 3. The API reference still matches the route table
python ./scripts/check-api-docs.py

# 4. The mod still compiles against the real game assemblies
#    (needs Cities: Skylines installed; see -GamePath)
powershell -NoProfile -ExecutionPolicy Bypass -File ./scripts/build-mod.ps1 -NoInstall
```

`dotnet build ./Cs1Mayor.sln` also works, but it includes the mod project, which needs a
local Cities: Skylines installation. CI builds the MCP server and the test project
individually for exactly that reason.

If your change touches the in-game bridge, also read
[`docs/07-verification.md`](../docs/07-verification.md) — it is the playbook for proving
a change did not corrupt the city.

## The one rule that is not negotiable

**Every read and write of game state happens on the simulation thread.**

Cities: Skylines owns city state on a dedicated simulation thread. If a change mutates
game state from the HTTP worker thread or the Unity main thread, it will race the
simulation and silently corrupt the save. `src/Dispatch/SimulationDispatcher.cs` is the
only sanctioned way in, and `GET /debug/threads` is the runtime proof.

If you are not sure whether your change respects this, stop and ask before opening the
PR.

## Code style

- Match the file you are editing. `src/` targets C# 5 (the game's Mono runtime) and uses
  explicit types; `mcp-server/` targets .NET 10 and uses modern C#.
- Prefer many small, well-named files over few large ones. `src/State/GameState.cs` is
  the counter-example that motivated this rule.
- A public type whose name does not explain it gets a short XML doc comment saying what
  it is for and, where it is not obvious, what breaks if you get it wrong.
- Do not add a dependency without saying why in the PR body.

## License

By contributing you agree that your contribution is licensed under the MIT license in
[`LICENSE`](../LICENSE).
