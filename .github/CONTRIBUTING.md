# Contributing

Thanks for helping. Fixes, new tools or endpoints, environment fixes (game paths, mod
loading) and docs are all welcome. For a large feature, open an issue to discuss it first.

## Start from an issue

Every change starts as a [GitHub issue](https://github.com/MinhChien9/cities-skylines-mayor/issues/new/choose)
(bug, feature, game finding or task), and the PR that resolves it says `Closes #<number>`.
Docs-only PRs may skip the issue. Problems with recording or the video pipeline belong to
[cities-skylines-video](https://github.com/MinhChien9/cities-skylines-video).

The full workflow (triage, labels, milestones, definition of done) is in
[docs/development.md](../docs/development.md). Releases are in
[docs/releasing.md](../docs/releasing.md).

## Branches, commits, PRs

The conventions follow the [opencode](https://github.com/anomalyco/opencode) project.

- **Branch:** at most three words, hyphenated, named after the issue, based on `main`. No
  slashes and no type prefixes: `pipe-node-snap` ✅, `fix/pipe-node-snap` ❌.
- **Commit and PR title:** `type(scope): summary`, imperative, under about 70 characters.
  Types: `feat`, `fix`, `docs`, `chore`, `refactor`, `test`. Scopes, if they help: `bridge`,
  `mcp`, `dispatch`, `docs`, `scripts`, `github`.
- **Commit body:** the *why*, which the diff does not show.
- **PR:** fill in [the template](pull_request_template.md). Describe the problem, then the
  fix, in two short paragraphs, and say how you verified it. Keep one logical change per
  PR. Add a line to `CHANGELOG.md` under `[Unreleased]`. Don't paste AI-generated walls of
  text.

## Verify

```powershell
dotnet build mcp-server/Cs1Mayor.Mcp.csproj -c Release
dotnet test  tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj
python scripts/smoke-test-mcp.py
python scripts/check-api-docs.py
.\scripts\build-mod.ps1 -NoInstall        # bridge changes; needs the game installed; 0 warnings
```

For bridge changes, also run the live checks in [docs/verification.md](../docs/verification.md),
starting with `GET /debug/threads`. CI runs everything except the mod build, which needs the
game's own assemblies.

## The rule that is not negotiable

**Every read and write of game state happens on the simulation thread**, through
`src/Dispatch/SimulationDispatcher.cs`. Code that touches game state from the HTTP worker or
the Unity main thread races the simulation and silently corrupts saves. If you are unsure,
ask in the issue before opening the PR.

## Code style

- Match the file you are editing. `src/` targets C# 5 (the game's Mono runtime);
  `mcp-server/` uses modern C#.
- Prefer small, well-named files: one concept per file.
- Give a public type a short doc comment when its name does not say what it is for.
- Explain any new dependency in the PR.

By contributing you agree that your work is licensed under the [MIT license](../LICENSE).
