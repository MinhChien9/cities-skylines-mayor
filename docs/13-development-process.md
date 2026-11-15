# Development process — issues first

How work is found, tracked, changed and shipped in this repo. The short version:
**every change starts as a GitHub issue and ends as a squash-merged PR that closes it.**
Nothing is "noted for later" in a doc.

## Where things live

| Kind of information | Lives in | Not in |
|---|---|---|
| Bugs, missing tools, ideas, findings about the game, chores | **GitHub issues** | PLAN.md, roadmap checklists, code comments |
| What will ship next | **Milestones** (`v0.2.0`, …) | — |
| Why a design is the way it is | `PLAN.md` decision log | issues (they close and get buried) |
| How the system works (stable reference) | `docs/` | issues |
| What shipped in each version | `CHANGELOG.md` and the GitHub release | PLAN.md |
| The machine the project runs on | `PLAN.md` "Environment state" | — |

`PLAN.md` keeps the goal, the phase summary, the decision log, the lessons list and the
environment state. Its backlog points here.

## The issue lifecycle

```
found ──▶ issue (template) ──▶ triage ──▶ branch ──▶ PR "Closes #n" ──▶ squash merge ──▶ release
           needs-triage         type + area      one issue      checks       issue closes      CHANGELOG
                                + priority       per branch     + review     automatically
                                + milestone
```

1. **Open** with a template: *Bug report*, *Feature request*, *Game finding* or *Task*
   (blank issues are off). One problem per issue. Include the tool call and the response;
   the reproduction is worth more than the analysis.
2. **Triage** (the maintainer, or the agent when asked): remove `needs-triage`, set one
   type label, one or more `area:` labels, a priority, and a milestone when it should ship
   in the next release. If information is missing: `needs-info` (it goes stale after three
   weeks). Duplicates are closed with a link.
3. **Work**: a branch per issue, named after the issue in at most three words
   (`pipe-node-snap`). Reproduce first, then fix, then verify (see below).
4. **PR**: title `type(scope): summary`, description with `Closes #n`, the problem, the fix
   and how it was verified (`.github/pull_request_template.md`). `triage-pr-standards`
   labels PRs that miss either (`needs:title`, `needs:issue`). Docs-only PRs may skip the
   issue.
5. **Merge**: squash merge. The issue closes from `Closes #n`. Add the change to
   `CHANGELOG.md` under `## [Unreleased]` in the same PR.
6. **Release**: when a milestone is done, follow [`14-releasing.md`](14-releasing.md).

## Labels

| Label | Meaning |
|---|---|
| `bug` | A tool, endpoint or the bridge did the wrong thing |
| `feature` | A new tool, endpoint, game read or lever |
| `finding` | Something learned about the game; the closing PR updates a tool, `docs/05-game-mechanics.md` or the lessons |
| `task` | Work with a clear done-state: verification runs, cleanups, releases |
| `documentation` | Docs only |
| `area:bridge` · `area:dispatch` · `area:mcp` · `area:docs` · `area:ci` | Which part of the code |
| `p1` · `p2` · `p3` | Do next · soon · when convenient |
| `needs-triage` · `needs-info` · `blocked` | Status |
| `contributor` · `needs:title` · `needs:issue` | Set by the triage workflows on PRs |

## For the agent (Claude) — while playing and while coding

- **A tool misbehaves during play:** do not work around it silently. Note the call and the
  response, keep playing if you can, and open a bug:
  `gh issue create -R MinhChien9/cities-skylines-mayor --label bug --label needs-triage --title "..." --body-file <file>`
  using the bug template's headings (Description, Steps to reproduce, Response, Area).
- **You learn how the game works** (a formula, a hidden rule, a limit): open a *Game
  finding* with the evidence. Do not add it to PLAN.md directly; the PR that closes the
  finding records it where it belongs.
- **Starting a coding session:** `gh issue list -R MinhChien9/cities-skylines-mayor --milestone v0.2.0`
  (then `--label p1`), pick one, say which. One issue per branch and PR.
- **Never** close an issue without either a merged PR or a comment explaining why
  (duplicate, won't fix, recorded elsewhere).

## Definition of done

- [ ] The issue's "done when" (or its expected behaviour) holds, shown in the PR
- [ ] `scripts/build-mod.ps1 -NoInstall` — 0 warnings (bridge changes)
- [ ] `dotnet test tests/Cs1Mayor.Mcp.Tests/Cs1Mayor.Mcp.Tests.csproj`
- [ ] `python scripts/smoke-test-mcp.py` and `python scripts/check-api-docs.py`
- [ ] Live: `GET /debug/threads` → `runsOnSimulationThread: true`, plus the repro now passing
- [ ] Docs updated if a tool, endpoint or behaviour changed (`docs/04`, `docs/12`, `AGENTS.md`)
- [ ] `CHANGELOG.md` → `[Unreleased]` has a line for it

## Commands

```powershell
gh issue list -R MinhChien9/cities-skylines-mayor --milestone v0.2.0      # what ships next
gh issue list -R MinhChien9/cities-skylines-mayor --label needs-triage    # what needs sorting
gh issue view 65 -R MinhChien9/cities-skylines-mayor
gh issue edit 65 -R MinhChien9/cities-skylines-mayor --remove-label needs-triage --add-label p2 --milestone v0.2.0
git checkout -b pipe-node-snap
gh pr create --title "fix(bridge): snap pipe ends to pipe nodes only" --body "Closes #65 ..."
gh pr merge <n> --squash
```
