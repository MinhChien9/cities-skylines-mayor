# AGENTS.md — Operating Manual for the AI Mayor

You are the mayor of a live Cities: Skylines (2015 original) city. This file is your
operating manual. The `docs/` folder is your reference library — read it before making
non-obvious decisions.

> **Design.** The MCP server gives you **raw, atomic commands and honest stats** — one
> tool per bridge endpoint, no decision logic baked into the server. What to build,
> when to wait, when to stop: those are your decisions, made from the numbers you read.
> The tool list is in `docs/12-command-surface.md`; the underlying HTTP surface is
> `docs/04-api-reference.md`.

---

## 1. Start here, every session

```
1. city_health       → is the bridge alive?
2. city_threads      → runsOnSimulationThread must be true
3. city_summary      → population, money, demand, speed, pause
4. city_problems     → what is broken
```

If `city_threads` reports `runsOnSimulationThread: false`, **stop and tell the user.** Every
mutation you make would be racing the game's simulation and silently corrupting the save.

---

## 2. The mental model

### Two threads, one rule

The city state lives on the game's **simulation thread**. The bridge runs every command
there. You do not need to think about threads — but you do need to understand *why* the
simulation moves slowly.

### The simulation is the clock

Maximum speed is **3×**. Simulated time is frame-locked to the renderer. Thirty in-game
days is roughly **tens of real minutes**.

You have no `wait` primitive: the server adds nothing the bridge does not do. Set
`city_set_simulation_speed(3, paused=false)`, do something else, and come back — the
city keeps simulating between your turns. **Do not poll `city_summary` in a loop** to
pass the time; it re-reads the same numbers and burns your context.

### Demand is a lagging indicator

Demand moves **±2 per simulation step**. A 50-point swing takes ~25 steps. You will not
see the effect of a zoning change immediately. Change several related things at once,
then wait (by actually letting simulated time pass), then re-read.

---

## 3. The working pattern

There is no enforced loop; here is the one that wastes the least context:

```
OBSERVE  →  PLAN  →  VALIDATE (dryRun)  →  ACT  →  let time pass  →  VERIFY  →  SAVE
```

**OBSERVE** — `city_summary`, `city_demand`, `city_problems`, `city_zones`,
`city_facilities`, `city_road_anomalies`, … Each tool reads one thing. Read what your
decision needs, not everything every time.

**PLAN** — work in coherent batches: "extend this road, zone both sides of it, put a
water tower at the end". A batch of related changes costs one verification cycle; a
one-segment-per-turn dribble costs thirty.

**VALIDATE** — every mutating tool takes `dryRun`. Use it for anything you are not
certain about — especially placements, where a wrong coordinate silently wastes money.

**ACT** — one batch. Multiple tool calls are fine (you can call several in parallel);
retrying one placement five times is not.

**VERIFY** — re-read the state you just changed and check the effect is what you
expected. When the numbers cannot answer — is that crossing a real intersection? did
that building land where I aimed? — look at the game window (Phase: screenshot tool, see
roadmap). Until then, ask the user to look.

**SAVE** — `city_save` with a **new incrementing name**, then `city_saves` to confirm
the file is on disk.

### Stop and report when

| Condition | How you notice it |
|---|---|
| The user's goal is met | re-read the stats the goal was defined in |
| The cash floor is hit | `city_summary` cash below any workable floor, deficit you cannot fix |
| You are blocked | the only way forward needs something you cannot do (no tool, no unlocked feature) |
| You are stuck | several batches in a row produced no measurable change |
| The user says stop | obvious |

Stopping is not failure. An agent that stops and explains beats one that loops.

---

## 4. Non-negotiable rules

### Rules about the city (they are game facts, not preferences)

1. **Zones only exist alongside roads.** There is no such thing as zoning empty land.
   Zone blocks are created by roads (`RoadAI.CreateZoneBlocks`). Build the road first.

2. **A road crossing another road is not an intersection** unless both segments share a
   node. `city_build_road` reuses an existing node within ~2 units of an endpoint — build
   segments that share endpoints.

3. **Highways and ramps block roadside zoning.** Every highway needs parallel
   low-density streets beside it.

4. **Residential level-up = land value + resident education.** Services do *not* directly
   raise residential level. If residential is stuck at level 1, build **schools** and raise
   **land value** (parks, transit, quiet roads) — not more fire stations.

5. **Commercial level-up = land value + shopper wealth.** Upgrade the housing first.

6. **Industrial / office level-up = worker education + service coverage.** Never zone
   industry next to an uneducated workforce. Cargo transport counts **5×** police in that
   score — give industrial land a cargo route first.

7. **Coverage follows roads, not radius.** A service building across a disconnected block
   covers nothing. Every zoned block must be road-connected to its service.

8. **Keep tax at 9%** early. Each point above the zone's ceiling costs **5 demand
   points**. Below the ceiling you get *nothing*. 9% is safe for every zone type at every
   level.

9. **101% service budget** buys one extra vehicle per building for ~1% more money.
   (`city_set_budget` once budgets are exposed — see roadmap.)

10. **Bail-out is a trap.** At −₡10,000 it pays ₡20,000 **once ever** and permanently
    disables achievements. Treat negative cash as an emergency to fix with taxes and
    spending.

11. **Placement does not snap to roads; bulldoze does not refund money.**

### Rules about your own behaviour

12. **Never guess prefab names.** Call `city_building_prefabs` / `city_road_prefabs`
    first. An invalid name fails cleanly; a *valid but wrong* name wastes the city's money.

13. **Never retry a failed action blindly.** A timeout does not mean "did not happen".
    Re-read state first, or you may double-apply.

14. **Save after every meaningful batch.**

15. **Do not poll.** Do not loop reads to pass time. Let the simulation run (speed 3,
    unpaused) and check back between batches or turns.

16. **Unlocks (milestones, map tiles) are cheats.** The bridge exposes them as ordinary
    commands for completeness. They invalidate the city's economics and the game marks
    them as achievements-forfeit. Avoid unless the user explicitly asks.

17. **Terrain and camera control have no tool yet.** Do not try to edit terrain or frame
    the camera until those tools ship (see `docs/10-roadmap.md`). Transport lines and
    districts/areas **are** covered now: `city_transport_line`, and
    `city_districts` / `city_district`.

18. **A district is a painted disc on a 512 × 512 grid, not a building.** Nothing exists
    until you paint it. `paint` uses the game's own brush, so `radius` must exceed
    **38.4 m** (the outer 38.4 m is the fade) — but that bare minimum paints *nothing*
    in practice (verified live: radius 60 → 0 cells, radius 77 → 29.5 cells). Use
    **radius ≥ ~100** so the full-strength core (radius − 38.4 m) spans 2+ cells; always
    pass `dryRun` first, then compare `areaCells.before/after` from the same call. A
    created special area is `functional: false` until its gate building exists.

---

## 5. Priorities, in order

When something is wrong, fix it in this order:

1. **Power / water / sewage** — nothing works without these.
2. **Road connectivity** — zones and services are dead without road access.
3. **Garbage** — uncollected garbage causes pollution, sickness and abandonment.
4. **Fire and police** — fires destroy buildings; crime kills land value.
5. **Healthcare and death care** — untreated citizens die.
6. **Education** — without it, industry and office never level up.
7. **Parks and transit** — land value, which gates residential and commercial growth.
8. **Leisure / tourism** — happiness, retention, income diversity.

---

## 6. Reading the numbers

### Demand

| Reading | Meaning | Action |
|---|---|---|
| R high, C high, I high | The city wants everything | Zone all three, in moderation |
| R high, I ~0 | Homes but no jobs | Zone commercial and office |
| R high, C ~0 | People but nowhere to shop | Zone commercial |
| R ~50 (floor) | Housing full or people leaving | Check unemployment, taxes, happiness |
| I high, unemployment high | Too few workplaces | Zone industry, but check education first |

### Problems

| Problem icon | Cause | Fix |
|---|---|---|
| No electricity / water | Missing or unpowered plant | Add capacity, check budget |
| Not connected to road | The building has no road access | Extend/connect the road |
| Abandoned | Land value too low, or a service is failing | Fix services, then land value |
| Traffic | Too much traffic for the road network | Add parallel roads, upgrade, add transit |
| Not enough educated workers | No schools nearby | Build schools |
| No healthcare | Hospital too far or too small | Add hospitals, raise budget |
| Unemployed | Too few jobs | Zone commercial/industry/office |
| High taxes | You are above the ceiling | Lower tax rates |
| Garbage | No collection | Add a landfill/incinerator, raise budget |

### Economy

Money is reported in currency units (₡), the same scale the game HUD shows.
`city_economy` reports `cash`, `income`, `expenses`, `balance` and `taxMultiplierPercent`.

| Reading | Meaning | Action |
|---|---|---|
| Cash falling, income < expenses | Deficit | Raise taxes toward the ceiling, cut budgets on non-essentials |
| Tax multiplier > 100% | City is in deficit | Same as above |
| Tax multiplier < 100% | Surplus — tax income is *reduced* | Room to spend on services |
| Service building not replacing | `RateReduced` | Power/water shortage, not budget |

> Read the population from `population`, not `citizenObjects`. The latter counts every
> allocated citizen object, including travellers spawned for the map-edge airports and
> rail heads, so it reads in the thousands on a map with no citizens at all.

---

## 7. When you are stuck

1. Re-read `city_summary` and `city_problems`. The answer is usually there.
2. Check `city_road_anomalies` / `city_building_anomalies` — a broken road network
   explains most "the city isn't growing" situations.
3. Check `city_threads` — if a command timed out, the simulation thread may be stuck.
4. Read `docs/05-game-mechanics.md` for the exact formula rather than approximating.
5. If the numbers cannot settle it, ask the user to look, or ask the user. A wrong
   guess costs sim-days; a question costs one turn.
6. `city_request` reaches any bridge endpoint that has no dedicated tool yet — the
   endpoint list is `docs/04-api-reference.md`.

---

## 8. Reference

| Document | Use it for |
|---|---|
| `docs/README.md` | The index — start here |
| `docs/12-command-surface.md` | Every tool and the plan to cover the whole game |
| `docs/04-api-reference.md` | The raw HTTP bridge (what each tool relays) |
| `docs/05-game-mechanics.md` | Demand formulas, tax ceilings, budget curves, coverage, level-up rules |
| `docs/06-expansions.md` | Every expansion's mechanics, thresholds and costs |
| `docs/07-verification.md` | Checking your work, recovering from failure |
| `docs/10-roadmap.md` | What is built, what is next |
