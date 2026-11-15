# Releasing

A release is a tagged commit on `main` with a GitHub release that carries two assets:

| Asset | What it is | Who needs it |
|---|---|---|
| `cs1-mayor-mod-vX.Y.Z.zip` | `SkylinesAgentBridge.dll` (the in-game bridge) + `INSTALL.txt` | Anyone playing: drop the DLL into the game's mods folder |
| `cs1-mayor-mcp-vX.Y.Z.zip` | The MCP server, `dotnet publish` output (needs the .NET 10 runtime) | Anyone wiring an agent to the game |

The mod is compiled against the game's own assemblies, which cannot be redistributed or
installed on a CI runner, so **releases are cut locally** with `scripts/release.ps1` on a
machine with Cities: Skylines installed.

## Versioning

[Semantic versioning](https://semver.org/), pre-1.0: the minor version goes up for new
tools or endpoints and for any change an agent's prompts would notice (a renamed tool or
field, a changed default); the patch version for fixes. The version lives in
`Directory.Build.props` (`<Version>`), which the MCP server reports as its `serverInfo`
version. One milestone per minor version (`v0.2.0`), holding the issues it ships.

## CHANGELOG

`CHANGELOG.md` follows [Keep a Changelog](https://keepachangelog.com/). Every PR adds its
line under `## [Unreleased]` (Added / Changed / Fixed / Removed), with the issue number.
The release script turns that section into the version's section and uses it as the
release notes, so the notes are reviewed in a PR before they ship.

## Cutting a release

1. Make sure the milestone's issues are closed (or moved to the next milestone).
2. In a PR (`chore(release): vX.Y.Z`): set `<Version>` in `Directory.Build.props`, rename
   `## [Unreleased]` to `## [X.Y.Z] - YYYY-MM-DD` in `CHANGELOG.md` and add a fresh empty
   `## [Unreleased]` above it. Merge it.
3. On an up-to-date, clean `main`:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 -Version X.Y.Z -DryRun   # checks + assets only
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\release.ps1 -Version X.Y.Z           # tag, push, publish
   ```

   The script refuses to run off `main`, with local changes, behind `origin`, with a
   version that does not match `Directory.Build.props`, without a CHANGELOG section for it,
   or when the tag exists. It runs the full check suite (mod build with 0 warnings, tests,
   MCP smoke test, API doc check), builds both assets into `dist/`, creates the annotated
   tag `vX.Y.Z`, pushes it, and publishes the GitHub release with the CHANGELOG section as
   notes.
4. Close the milestone and open the next one.

## Hotfixes

Fix on a branch from `main` as usual (issue → PR), then release the next patch version.
There are no release branches.
