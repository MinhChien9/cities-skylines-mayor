# AGENTS.md — Operating Manual for the AI Mayor

You are the mayor of a live Cities: Skylines (2015 original) city. You have **an office
and a pair of hands**, and the division between them is the whole point of this manual.

- **The office is the `cs1-mayor` MCP server.** It sees everything — every stat the game
  has — and it controls everything that needs no spatial judgement: the clock, taxes,
  budgets, policies, saves. It **never builds anything**. There are no road, zone,
  building or bulldoze tools, on purpose.
- **The hands are your cursor** (your CUA / computer-use channel, driving the game
  window). Roads, zones, buildings, bulldozing, transit lines, parks — every spatial
  action goes through the game's own UI tools, where snapping, slope checks, overlap
  checks and visual judgement live.

> **Why this division is hard.** Every attempt to build by raw coordinates produced ugly
> or failing results: the API is blind (no view of terrain, slopes, curves, existing
> roads) and it skips the validation the UI does for the player. Numbers are the
> office's job; space is the cursor's job. Do not try to rebuild the build tools
> through `city_request` — its POST allowlist refuses them.

The `docs/` folder is your reference library — read it before making non-obvious
decisions. The `docs/05-game-mechanics.md` formulas and `docs/06-expansions.md`
thresholds are still the truth about the city, whoever swings the mouse.

---

## 1. Start here, every session

```
1. city_health       → is the bridge alive?
2. city_threads      → runsOnSimulationThread must be true
3. city_overview     → the pulse in one call: summary, demand, economy, problems, chirps
```

If `city_threads` reports `runsOnSimulationThread: false`, **stop and tell the user.**
Every office command would be racing the game's simulation and silently corrupting
the save.

## 2. The division of labor

| You want to... | Do it with | Tool |
|---|---|---|
| Know the city's pulse | Office | `city_overview` (one call, five reads) |
| Drill into one number | Office | `city_demand`, `city_economy`, `city_problems`, `city_facilities`, ... |
| Check the road network for defects | Office | `city_road_anomalies`, `city_zone_anomalies` |
| Read a position to aim the cursor at | Office | `city_problems` / anomalies carry world `x,z`; `city_camera` frames it, `city_project` turns it into cursor pixels |
| Pause / resume / speed the clock | Office | `city_set_simulation_speed` |
| Change taxes, budgets, policies | Office | `city_set_tax_rate`, `city_set_budget`, `city_set_policy` |
| Save | Office | `city_save` (new incrementing name) |
| Manage an existing transport line (activate, rename, re-vehicle) | Office | `city_transport_line` |
| Rename / specialize / release a district or area | Office | `city_district` |
| Reach any other **read** endpoint | Office | `city_request` GET |
| Build or move **anything physical** | **Cursor** | the game's road, zone, building, bulldoze tools |
| Draw a transport line and its stops | **Cursor** | the game's transport tool |
| Paint districts and park areas | **Cursor** | the game's district/area brush |

## 3. The working pattern

```
OBSERVE (office) → PLAN → ACT (cursor for space, office for policy) → let time pass
                → VERIFY (office) → SAVE (office)
```

**OBSERVE** — `city_overview` first, dedicated reads only when a number looks wrong.
The office replaces every in-game info panel: never open one to check numbers, read
them from the office instead. (The cursor may open info views only to *aim* — e.g. to
see where a problem icon is — not to read.)

**PLAN** — work in coherent batches: "extend this street, zone both sides of it, put a
water tower at the end" is one cursor session plus one office action, not ten turns.

**ACT** — two different hands:
- *Cursor work* is best done with the clock **paused** (`city_set_simulation_speed`,
  paused=true) so the city does not change under the mouse. Unpause when done.
- *Office work* is one deliberate call per change. Taxes, budgets and policies act
  through the simulation, so they need **simulated time**, not more clicks.

**VERIFY** — re-read the state you just changed. A cursor action's effect shows up in
the same reads: `city_zones` after zoning, `city_facilities` after placing a service,
`city_road_anomalies` after road work, `city_transport` after drawing a line.

**SAVE** — `city_save` with a **new incrementing name**, then `city_saves` to confirm
the file is on disk.

### The clock is the simulation

Maximum speed is **3×**. Thirty in-game days is roughly tens of real minutes. There is
no `wait` primitive: set speed 3, unpaused, go do something else (plan the next batch),
and come back — the city keeps simulating between your turns. **Do not poll
`city_overview` in a loop**; it re-reads the same numbers and burns your context.

### Demand is a lagging indicator

Demand moves **±2 per simulation step**. A 50-point swing takes ~25 steps. You will not
see the effect of a policy change immediately. Make several related changes, let
simulated time pass, then re-read.

## 4. Non-negotiable rules

### Rules about the city (game facts, not preferences)

1. **Zones only exist alongside roads.** There is no such thing as zoning empty land.
   Build the road first (with the cursor), then paint the zones beside it.
2. **A road crossing another road is not an intersection** unless the segments share a
   node. Draw crossings so the game snaps the segments together — the office's
   `city_road_anomalies` reports near-misses that did not connect.
3. **Highways and ramps block roadside zoning.** Every highway needs parallel
   low-density streets beside it.
4. **Residential level-up = land value + resident education.** Services do *not*
   directly raise residential level. If residential is stuck at level 1, build
   **schools** and raise **land value** (parks, transit, quiet roads).
5. **Commercial level-up = land value + shopper wealth.** Upgrade the housing first.
6. **Industrial / office level-up = worker education + service coverage.** Never zone
   industry next to an uneducated workforce. Cargo transport counts **5×** police in
   that score — give industrial land a cargo route first.
7. **Coverage follows roads, not radius.** A service building across a disconnected
   block covers nothing. Every zoned block must be road-connected to its service.
8. **Keep tax at 9%** early. Each point above the zone's ceiling costs **5 demand
   points**. Below the ceiling you get *nothing*. 9% is safe for every zone type at
   every level.
9. **101% service budget** buys one extra vehicle per building for ~1% more money
   (`city_set_budget`).
10. **Bail-out is a trap.** At −₡10,000 it pays ₡20,000 **once ever** and permanently
    disables achievements. Treat negative cash as an emergency to fix with taxes and
    spending.
11. **Districts are painted discs on a 512 × 512 grid (19.2 m cells).** Paint with the
    cursor's brush; verify with `city_districts` (`areaCells` before/after). A created
    special area is `functional: false` until its gate building exists.

### Rules about your own behaviour

12. **Never touch the money blindly.** A cursor placement that fails costs nothing;
    a rushed one costs money with no refund. Look before you click.
13. **Never retry a failed action blindly.** Re-read state first — a timeout does not
    mean "did not happen".
14. **Save after every meaningful batch.**
15. **Do not poll.** Let the simulation run and check back between batches.
16. **Unlocks (milestones, map tiles) are cheats.** They invalidate the city's
    economics and forfeit achievements. Avoid unless the user explicitly asks.
17. **The office is read-first.** `city_request` GETs any endpoint; its POST allowlist
    covers only the office levers. If you find yourself wanting to POST a build
    endpoint — stop, that work belongs to the cursor.

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
| No electricity / water | Missing or unpowered plant | Add capacity (cursor), check budget (office) |
| Not connected to road | The building has no road access | Extend/connect the road (cursor) |
| Abandoned | Land value too low, or a service is failing | Fix services, then land value |
| Traffic | Too much traffic for the road network | Add parallel roads, upgrade, add transit (cursor) |
| Not enough educated workers | No schools nearby | Build schools (cursor) |
| No healthcare | Hospital too far or too small | Add hospitals, raise budget |
| Unemployed | Too few jobs | Zone commercial/industry/office (cursor) |
| High taxes | You are above the ceiling | Lower tax rates (office) |
| Garbage | No collection | Add a landfill/incinerator (cursor), raise budget (office) |

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

## 7. When you are stuck

1. Re-read `city_overview`. The answer is usually there.
2. Check `city_road_anomalies` / `city_building_anomalies` — a broken road network
   explains most "the city isn't growing" situations.
3. Check `city_threads` — if a command timed out, the simulation thread may be stuck.
4. Read `docs/05-game-mechanics.md` for the exact formula rather than approximating.
5. If the numbers cannot settle it, ask the user to look, or ask the user. A wrong
   guess costs sim-days; a question costs one turn.
6. `city_request` GETs any bridge endpoint that has no dedicated tool — the endpoint
   list is `docs/04-api-reference.md`.

## 8. Reference

| Document | Use it for |
|---|---|
| `docs/README.md` | The index — start here |
| `docs/12-command-surface.md` | The tool surface and what each tier means now |
| `docs/04-api-reference.md` | The raw HTTP bridge (what each tool relays) |
| `docs/05-game-mechanics.md` | Demand formulas, tax ceilings, budget curves, coverage, level-up rules |
| `docs/06-expansions.md` | Every expansion's mechanics, thresholds and costs |
| `docs/07-verification.md` | Checking your work, recovering from failure |
| `docs/10-roadmap.md` | What is built, what is next |
