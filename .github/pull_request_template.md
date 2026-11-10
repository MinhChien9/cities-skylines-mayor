### Issue for this PR

Closes #

### Type of change

- [ ] Bug fix
- [ ] New feature
- [ ] Refactor / code improvement
- [ ] Documentation
- [ ] Build / tooling

### What does this PR do?

Describe the problem, the change, and why it works. A couple of short paragraphs is
enough — please write it in your own words.

_If you paste a large clearly AI generated description here your PR may be IGNORED or
CLOSED!_

### Why is this the right approach?

<!-- Optional. What did you consider instead? -->

### How did you verify your code works?

<!--
For bridge changes, state that you ran:
  - scripts/build-mod.ps1 -NoInstall   (compiles against the real game assemblies)
  - GET /debug/threads                 (runsOnSimulationThread: true)
  - docs/07-verification.md            (the dry-run / read-back / save checks)

For MCP-only changes, state that you ran:
  - dotnet build ./mcp-server/Cs1Mayor.Mcp.csproj -c Release
  - dotnet test  ./tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj
  - python ./scripts/smoke-test-mcp.py
  - python ./scripts/check-api-docs.py
-->

### Screenshots / recordings

<!-- If this is a UI or in-game overlay change, include a screenshot. -->

### Checklist

- [ ] I have tested my changes locally
- [ ] I have not included unrelated changes in this PR
- [ ] Every game-state read and write still happens on the simulation thread
      (`GET /debug/threads` reports `runsOnSimulationThread: true`)
- [ ] I have updated the docs if I changed a command, tool, or endpoint

_If you do not follow this template your PR will be automatically rejected._
