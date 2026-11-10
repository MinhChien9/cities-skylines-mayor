# The Play Loop — "I talk to it, it plays"

> **Status: target design.** Nothing in this document is implemented yet. It is the
> specification the rest of the build follows. What *is* built today is listed in
> [`10-roadmap.md`](10-roadmap.md).

---

## 1. The product in one paragraph

The user opens opencode (or any MCP client), connects `cs1-mayor`, and says something
like *"build me a city of 20,000 people with good transport and no crime"*. The agent then
**plays the game on its own** — planning, building, zoning, waiting for the simulation,
checking its work, correcting mistakes — until the goal is met or it hits something it
must not decide alone. The user's job is to set the goal and read the report.

This is **not** a chat where you request one building at a time. That was considered and
rejected: the simulation is far too slow for turn-by-turn chat to be pleasant, and the
interesting part of the product is the agent's judgement over time, not its ability to
click a button you could click yourself.

---

## 2. The three decisions that shape everything

| Decision | Choice | Consequence |
|---|---|---|
| Interaction model | **Autonomous goal mode only** | The server needs durable goal state, a journal, and a way to wait. The agent needs a "stop condition" contract. |
| Where the loop lives | **The MCP client drives; the server is tools + memory** | The server never plans. It remembers, observes, and acts. The LLM stays in the client, where it belongs. |
| Vision | **The agent sees; the user does not** | A screenshot tool returns an image the agent can inspect. It is for *self-verification* ("did my road actually connect?"), not for a spectator view. |

---

## 3. The session model

### 3.1 States

```
IDLE ──city_goal(set)──▶ PLANNING ──▶ EXECUTING ──▶ WAITING ──┐
                            ▲                                    │
                            └────────────────────────────────────┘
                                        │
        STOPPED ◀── goal met │ cash floor │ blocked │ error ─────┘
```

The client owns the state machine. The server owns the *memory* that makes it survive a
context compaction or a restart.

### 3.2 What the server remembers

Stored under `%LOCALAPPDATA%\cs1-mayor\sessions\<sessionId>.json` (plus an append-only
`.jsonl` journal), so a session survives the MCP server restarting, the client
compacting its context, or the game closing and reopening.

| Store | Contents |
|---|---|
| **Goal** | Free-text goal, target metrics (population, cash floor, coverage %, etc.), a checklist of steps, status. |
| **Journal** | One JSONL line per action: simulated timestamp, tool, arguments, cost, result, verification outcome, and a free-text note. |
| **Last snapshot** | The metric set used to compute deltas. |
| **Session settings** | Sim speed, auto-resume, `allowCheats` (always false — see §6). |

`city_goal` and `city_journal` are the two tools that touch this. Everything else is
stateless.

### 3.3 The loop the agent runs

```
1. OBSERVE   city_snapshot · city_problems · city_infrastructure
2. PLAN      update the checklist with city_goal
3. ACT       one coherent batch of changes (roads → zones → services), dryRun first
4. WAIT      city_wait(days=N) — blocks, then returns what changed
5. VERIFY    read the delta; did the change do what the plan expected?
6. RECORD    city_journal(append, ...) — always, including failures
7. REPEAT    until a stop condition trips
```

**Rule: batch, don't dribble.** A batch is "extend this road, zone both sides, put a
water tower at the end" — not one segment per tool call. The simulation is the clock;
every tool round-trip the agent spends is a decision it does not get to make later.

### 3.4 Stop conditions

The agent stops and reports when **any** of these is true:

| Condition | How it is detected |
|---|---|
| Goal met | The goal's target metrics are satisfied (`city_goal(check)`) |
| Cash floor | `cash` below the goal's floor, or a deficit the agent cannot fix |
| Blocked | The only way forward needs a capability the agent is forbidden to use (§6) |
| Stalled | N consecutive batches produce no measurable change |
| Explicit stop | The user says stop |

Stopping is **not** a failure. An agent that stops and explains beats one that loops.

---

## 4. Waiting, and the delta

The simulation runs at 3× and thirty in-game days is tens of real minutes. The agent
cannot poll `city_snapshot` every few seconds — that is the single biggest context
waste in the whole design.

### `city_wait(days | until, timeoutSeconds)`

- The **server** polls the bridge (`GET /state/summary`, ~140 µs) until the simulated
  date passes the target or the timeout expires. The game is never blocked; the sim
  thread is never held.
- It returns:

```jsonc
{
  "ok": true,
  "reached": true,              // did the target date pass?
  "gameTime": "2030-04-01T09:14:02",
  "daysRemaining": 0,
  "delta": {                    // what changed since the last check
    "population": { "from": 1840, "to": 1902 },
    "cash":       { "from": 51200, "to": 48940 },
    "demand":     { "from": { "residential": 96, "commercial": 12, "workplace": 4 },
                    "to":    { "residential": 88, "commercial": 19, "workplace": 9 } },
    "problems":   { "new": [ { "type": "NotConnected", "count": 3, "position": {...} } ],
                    "resolved": [ { "type": "NoElectricity", "count": 2 } ] }
  },
  "stopped": null               // "paused" | "no-game" | "timeout" when applicable
}
```

- `days: 0` means "do not wait, just diff me" — the cheap way to check progress.
- If the simulation is paused, the server **auto-resumes** (speed 3) when the session
  says so, and reports `stopped: "paused"` if it could not.
- The delta is computed against a **curated metric set**, not a structural diff of the
  whole snapshot. Structural diffs are noisy; the mayor cares about population, money,
  demand, happiness, problems, and milestones.

### Why not MCP notifications

The MCP server could push a notification when the target date is reached. It does not:
notification delivery is uneven across clients, and a blocked tool call with a bounded
timeout is understood everywhere. The agent loops `city_wait` — each call costs one
round trip and returns something worth reading.

---

## 5. Vision — the agent sees, the user does not

`city_view` returns a PNG as MCP image content. The agent uses it to check what the
numbers cannot tell it:

- Did the new road actually meet the other road? (a crossing is not an intersection
  unless they share a node)
- Is the industrial district next to the housing, or did it land in the middle of it?
- Is the water tower on the road, or floating next to it?

**Implementation constraint — the one exception to the thread rule.** A screenshot is
rendering, not simulation. It must run on the **Unity main thread**: frame the camera,
render into a `RenderTexture`, `ReadPixels`, encode PNG.

Framing is a **supported** call: `ICities.ICamera.SetCameraTarget(...)`,
`SetPosition`, `MoveCamera`, `GetCameraPosition` **[V]**. The capture is standard
Unity — `Camera.Render()` → `RenderTexture` → `ReadPixels` → `Texture2D.EncodeToPNG()`.

This is the only sanctioned path that touches the main thread, and it may **not** mutate
city state. `SimulationDispatcher` keeps its "no main-thread fallback" rule; a separate,
explicitly-named main-thread queue carries render work.

The image is expensive in context. Use it when the numbers are ambiguous — not as a
routine "look at my city" habit.

---

## 6. Milestones are earned, never unlocked

A player *can* click "unlock" in the milestone tree. The agent must not.

- **Read** milestone state freely: current milestone, progress toward it, population
  target, which features are unlocked (`UnlockManager.GetCurrentMilestone()`,
  `Unlocked(Feature)`, `MilestoneInfo.GetProgress()` **[V]**).
- **Unlocking** exists in the bridge for player parity, but the server **refuses the
  call** unless the session explicitly sets `allowCheats = true`, which the agent may
  not do. `UnlockManager.UnlockAllProgressionMilestones()` **[V]** is the call it would
  be — and the server will not make it.

Map tiles work the same way: `GameAreaManager.UnlockArea(int)` **[V]** exists, and the
server refuses it. Tiles come with earned milestones.

The rule in [`../AGENTS.md`](../AGENTS.md) §4.15 stays. This section only adds the
mechanism that enforces it instead of merely asking for it.

---

## 7. Capability parity — the short version

The full matrix is in [`12-capability-parity.md`](12-capability-parity.md). The short
version: **the agent must be able to do anything a player can do**, by one of four
routes:

| Tier | Route | Use when | Risk |
|---|---|---|---|
| **0** | **`ICities`** — the game's supported modding API | Always, if it covers it | Lowest |
| **1** | Public game API through the bridge | The API exists and is safe | Low |
| **2** | Reflection into private state through the bridge | The API is private but the state is well understood | Medium |
| **3** | UI automation of the game window | The feature is UI-only (the transport-line editor, the district brush) | Slow, brittle — but it is *exactly what the player does* |

The big discovery is **tier 0**: Cities: Skylines ships a supported modding interface
set in `ICities.dll` covering terrain, natural resources, saving and loading, map tiles,
milestones, the camera, building placement and the simulation clock. The bridge already
implements `ICities.ThreadingExtensionBase`, so capturing `IManagers` is a one-field
change that unlocks most of the parity surface with **no reflection and no UI
automation**.

Tier 3 already has a working prototype in `scripts/game-ui.ps1` (OS-level mouse and
keyboard injection, with the ALT-window-focus workaround and the screenshot-coordinate
scaling fix). The MCP server runs on the player's machine, so it can drive that directly
and expose it as another tool. **This is what makes the parity promise real** rather
than aspirational: if the game's API does not expose a feature, the agent can still
use the feature — through the same clicks you would.

---

## 8. The target tool surface

Thirteen coarse tools today. Parity needs more surface, so the doctrine changes —
deliberately, and with the mitigation that keeps it workable:

> **Rule: one tool per *domain*, not per endpoint.** Each tool takes an `action`
> parameter for the variants. Descriptions stay keyword-dense and under ~120 characters
> because opencode's inline Code Mode listing truncates them.

| Tool | Domain | Bridge calls |
|---|---|---|
| `city_health` | Liveness + thread proof | `/health`, `/debug/threads` |
| `city_snapshot` | The overview | `/state/summary` + `/state/demand` + `/state/economy` |
| `city_problems` | Problem icons | `/state/problems` |
| `city_districts` | Districts, zones, growables | `/state/zones`, `/state/growables` |
| `city_infrastructure` | Defect audit | `/state/road-anomalies`, `/state/building-anomalies`, `/state/zone-anomalies` |
| `city_services` | Facilities + coverage | `/state/facilities` |
| `city_prefabs` | Exact prefab names | `/prefabs/*` |
| **`city_wait`** | **Advance sim time, return the delta** | polls `/state/summary` |
| **`city_view`** | **Screenshot for the agent** | `POST /commands/render-view` (main thread) |
| **`city_progress`** | **Milestones and unlocks** | `/state/progress` |
| **`city_transport`** | **Read lines, stops, vehicles** | `/state/transport` |
| **`city_policy`** | **Read/write policies** | `/state/policies`, `POST /commands/set-policy` |
| **`city_budget`** | **Read/write service budgets** | `/state/budgets`, `POST /commands/set-budget` |
| **`city_terrain`** | **Read/edit terrain** | `/state/terrain`, `POST /commands/edit-terrain` |
| `city_build_road` | Roads | `POST /commands/build-road` |
| `city_zone` | Zoning | `POST /commands/set-zone` |
| `city_place_building` | Buildings | `POST /commands/place-building` |
| `city_bulldoze` | Removal | `POST /commands/bulldoze` |
| `city_economy` | Tax, speed, pause | `/state/economy`, `POST /commands/set-tax-rate`, `set-simulation-speed` |
| **`city_transport_line`** | **Create/edit transport lines** | `POST /commands/transport-line` |
| **`city_district`** | **Create/paint/specialize districts** | `POST /commands/district` |
| **`city_ui`** | **UI fallback — click the game** | OS-level input injection, locally |
| **`city_goal`** | **Session goal + checklist** | local (session store) |
| **`city_journal`** | **Decision journal** | local (session store) |
| `city_save` | Save and verify | `POST /commands/save`, `/state/saves` |

Bold = new. Twenty-four tools, of which six are session-memory tools that never touch
the game.

### Cost discipline

opencode's Code Mode listing costs one line per tool and truncates descriptions at 120
characters. The mitigation is that the **high-traffic tools stay few and obvious** —
`city_snapshot`, `city_wait`, `city_build_road`, `city_zone`, `city_place_building`,
`city_problems` — and the long tail is reachable through `search(...)` with a
keyword-dense description. The reference material behind the long tail lives in
`docs/05-game-mechanics.md` and `docs/06-expansions.md`, which the agent reads on
demand.

---

## 9. What the agent is told to do

The server's `instructions` (sent in the MCP `initialize` result) and `AGENTS.md` are
the agent's operating system. In goal mode they must encode:

1. **Start with the goal.** Read it with `city_goal(get)` before touching the city.
2. **Plan in batches.** A batch is a coherent group of changes, not one call.
3. **Dry run anything you are not certain about.** `dryRun=true` is free.
4. **Wait, do not poll.** Use `city_wait`. Never loop `city_snapshot` to pass time.
5. **Verify against the delta.** The delta tells you what actually happened.
6. **Record everything.** `city_journal(append)` after every batch, including failures.
7. **Stop on a stop condition** and report. Do not improvise past one.
8. **Never unlock milestones or map tiles.**
9. **Never accept a bail-out.** Fix the economy instead.
10. **Save often**, with incrementing names.

The current `Program.cs` instructions cover rules 2–10 in spirit but predate goal mode,
the wait contract, and the journal. They are rewritten as part of this work.

---

## 10. Non-goals

| Not doing | Why |
|---|---|
| A spectator view for the user | The user chose "agent sees, user does not". The image is verification, not entertainment. |
| The server planning anything | The LLM lives in the client. A planner in the server would be a second, weaker brain. |
| Unlocking milestones or tiles | Explicitly rejected. |
| A standalone chat CLI | The MCP client is the chat surface. |
| Cheating the simulation clock | There is no time compression beyond 3×. The design plans around it instead of through it. |

---

## 11. Open questions

| Question | Notes |
|---|---|
| How long should `city_wait` block by default? | Long enough to matter, short enough that the client does not time out. 60 s is the current proposal; opencode's execution timeout is 12 h by default. |
| Should the journal live in the game save instead of a sidecar file? | A sidecar survives a crashed save and is readable by a human. In-save means the agent's memory dies with the city. **Sidecar, for now.** |
| Does the delta need to be configurable? | The curated metric set may need to grow (milestone progress, service coverage %). Start fixed; extend when the agent asks for it. |
| What happens when the game is closed mid-session? | `city_wait` returns `stopped: "no-game"`. The session stays on disk and resumes when the game comes back. |
