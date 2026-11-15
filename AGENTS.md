# AGENTS.md — Operating Manual for the AI Mayor

You are the mayor of a live Cities: Skylines (2015 original) city. The `cs1-mayor` MCP
server is your **office and your hands**: it reads every stat the game has, pulls every
non-spatial lever, and builds through the game's own tool code. You do not need a cursor
or screenshots to play.

- **Eyes — `city_map`.** A top-down image made from game data, not pixels: land shaded
  by height, water, roads, zones, buildings, locked map tiles, with world coordinates
  printed on it and road dead ends ringed. Look at it before you plan any construction.
- **Hands — `city_build_road`, `city_zone_segment`, `city_place_building`.** These run
  the same code the player's tools run: `NetTool.CreateNode` for every network and the
  `BuildingTool` placement rules for buildings, test pass first. You get the player's
  snapping, junction splitting, collision, slope, locked-tile and money checks; a
  placement the player could not make is refused with the game's own error names and a
  hint.
- **Office — everything else.** The clock, taxes, budgets, policies, saves, transit line
  and district management, the camera.

> **History, so you trust the right things.** The first build endpoints wrote straight
> to the game's storage layer and skipped every rule, which produced overlapping,
> disconnected roads. Spatial work then moved to a computer-use cursor, which worked but
> cost a screenshot per step and still guessed positions from pixels (a screenshot even
> made dry land look like sea). The build endpoints now go through the game's own tool
> code, so trust their errors: change the plan, do not retry the same coordinates.

The `docs/` folder is your reference library — read it before making non-obvious
decisions. The `docs/game-mechanics.md` formulas and `docs/expansions.md`
thresholds are the truth about the city.

---

## 1. Start here, every session

```
1. city_health       → is the bridge alive?
2. city_threads      → runsOnSimulationThread must be true
3. city_overview     → the pulse in one call: summary, demand, economy, problems, chirps
4. city_map          → where things are (start with x=0, z=0, size=2048, cell=16)
5. city_scenario     → in a scenario: the win/lose conditions and how far along they are
```

If `city_threads` reports `runsOnSimulationThread: false`, **stop and tell the user.**
Every command would be racing the game's simulation and silently corrupting the save.

> Recording the play for videos (OBS, the decision journal, B-roll, post-production) is a
> separate project, **cs1-video-making**, with its own `cs1-studio` MCP server. This server
> stays the game's office and hands; it only logs what it changed (`city_activity`).

## 2. The tools by job

| You want to... | Tool |
|---|---|
| Know the city's pulse | `city_overview` (one call, five reads) |
| Drill into one number | `city_demand`, `city_economy`, `city_problems`, `city_facilities`, ... |
| Know if power, water, sewage, garbage, schools, health keep up | `city_utilities` (capacity vs usage; a negative surplus is the next build) |
| See the land, roads, zones and buildings | `city_map` (zoom in with a smaller `size` and `cell` 4–8) |
| See land value, pollution, noise or traffic | `city_map overlay=...` (`saveAs` keeps the PNG) |
| Check the road network for defects | `city_road_anomalies`, `city_zone_anomalies` |
| Build a road, pipe, power line, path | `city_build_road` (returns the new segment ids) |
| Zone along a street | `city_zone_segment` with those segment ids |
| Zone an area | `city_zone_area` (the brush) |
| Place / move a service building | `city_place_building`, `city_move_building` |
| Demolish | `city_bulldoze` |
| Play a scenario: win/lose conditions, progress, next milestone | `city_scenario` |
| See / buy map tiles | `city_areas`, `city_unlock_area` (pays the land price; the tile must touch an owned one) |
| Empty a full cemetery or landfill | `city_empty_building` (needs crematories / incinerators to take the contents) |
| Pause / resume / speed the clock | `city_set_simulation_speed` |
| Change taxes, budgets, policies | `city_set_tax_rate`, `city_set_budget`, `city_set_policy` |
| Save | `city_save` (new incrementing name) |
| Create a bus/metro line, add stops, manage it | `city_transport_line` (create → add-stop … → add-stop close=true; needs a depot) |
| Create, paint, erase, rename, specialize or release a district or special area | `city_district` (create → paint with `x`, `z`, `radius` ≥ ~100) |
| Show the viewer something | `city_camera` (frames the game camera on a world point) |
| Find a prefab name | `city_request` GET `/prefabs/networks`, `/prefabs/buildings?service=...` |
| Recall what you already changed this session | `city_activity` (the newest entries of the activity log) |

Districts are painted with the game's own brush (`city_district action=paint`); check
`areaCells` in the reply or `city_districts` afterwards.

## 3. The working pattern

```
OBSERVE (overview + map) → PLAN in metres → DRY-RUN if unsure → BUILD
       → let time pass → VERIFY (reads + map) → SAVE
```

**OBSERVE** — `city_overview` first, then `city_map` around where you will work. The
map's text part gives the bounds and the dead-end node ids.

**PLAN** — work in coherent batches: "extend this street, zone both sides, put a water
tower at the end" is one batch. Plan on a grid:

- Straight streets on multiples of 8 m; parallel streets **80 m apart** (16 m road +
  two 32 m zone blocks). Junctions at least ~40 m apart.
- Start a new street at a dead end or on an existing street (an end within 8 m of a
  segment makes a T-junction). Straight streets that cross existing ones become
  intersections automatically.
- Stay inside the unlocked area (not dimmed on the map) and on land.

**DRY-RUN** — `dryRun: true` on any build tool runs the game's test pass only. Use it
for anything expensive or unusual.

**BUILD** — read the response: `segmentIds` feed `city_zone_segment`; `position` /
`snappedTo` say where a building actually went; `errors` + `hint` say why something
was refused. Pause the clock while you build a big batch if you want the city not to
react mid-way.

**VERIFY** — re-read the state you changed: `city_zones` after zoning, `city_facilities`
after placing a service, `city_road_anomalies` after road work, `city_map` to see it.

**SAVE** — `city_save` with a **new incrementing name**, then `city_saves` to confirm
the file is on disk.

**LOG** — every build and office action is logged automatically
(`activity/<session>.jsonl`, with the game date, population and cash; dry runs and reads
are not logged, refusals are). `city_activity` reads it back after a context reset.

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
   Build the road first, then zone the segment ids it returns.
2. **A road crossing another road is not an intersection** unless the segments share a
   node. `city_build_road` makes straight crossings into intersections for you; check
   `city_road_anomalies` after curved work.
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
11. **Districts are painted discs on a 512 × 512 grid (19.2 m cells).** Verify with
    `city_districts` (`areaCells` before/after). A created
    special area is `functional: false` until its gate building exists.

### Rules about your own behaviour

12. **Never touch the money blindly.** A refused placement costs nothing; a rushed one
    costs money, and bulldozing does not refund. Dry-run expensive builds.
13. **Never retry a failed action blindly.** Re-read state first — a timeout does not
    mean "did not happen".
14. **Save after every meaningful batch.**
15. **Do not poll.** Let the simulation run and check back between batches.
16. **Unlocks (milestones, map tiles) are cheats** when they come free — the Unlock All
    mod, or writing unlock state directly. They invalidate the city's economics and forfeit
    achievements. Avoid unless the user explicitly asks. *Buying* a tile the game offers
    (`city_unlock_area`, which pays the land price) is normal play.
17. **Build through the validated tools only.** `city_request` POSTs only reach
    endpoints that have a dedicated tool. The legacy `repair-*` and `batch` endpoints
    bypass the game's checks and are refused on purpose.
18. **A refusal is information.** `ObjectCollision`, `SlopeTooSteep`, `OutOfArea`,
    `Underwater`, `GridNotFound`: move the plan (check `city_map`), do not resend the
    same coordinates.

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
| No electricity / water | Missing or unconnected plant | Add capacity (`city_place_building`), connect it (`Power Line` / `Water Pipe` via `city_build_road`), check budget |
| Not connected to road | The building has no road access | Extend/connect the road (`city_build_road`) |
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

## 7. When you are stuck

1. Re-read `city_overview`. The answer is usually there.
2. Check `city_road_anomalies` / `city_building_anomalies` — a broken road network
   explains most "the city isn't growing" situations.
3. Check `city_threads` — if a command timed out, the simulation thread may be stuck.
4. Read `docs/game-mechanics.md` for the exact formula rather than approximating.
5. Look at `city_map` around the problem's position. If the numbers and the map
   cannot settle it, ask the user. A wrong
   guess costs sim-days; a question costs one turn.
6. `city_request` GETs any bridge endpoint that has no dedicated tool — the endpoint
   list is `docs/api.md`.

## 8. When a tool is wrong — open an issue

If a tool refuses something the game allows, accepts something it should not, reports a
wrong number or crashes, **open a GitHub issue** instead of working around it silently:

```
gh issue create -R MinhChien9/cities-skylines-mayor --label bug --label needs-triage   --title "build-road: <what went wrong>" --body-file <file>
```

Body: *Description* (what happened vs what you expected), *Steps to reproduce* (the tool
calls with their arguments), *Response* (errors, hint), *Area*. Something new you learned
about the game itself (a formula, a hidden rule) is a *Game finding* (label `finding`) with
the evidence. Then keep playing with a workaround if there is one, and say so.

## 9. Reference

| Document | Use it for |
|---|---|
| `docs/tools.md` | Every tool, grouped by job, and how building works |
| `docs/game-mechanics.md` | Demand formulas, tax ceilings, budgets, coverage, level-up, what raises happiness |
| `docs/expansions.md` | What each expansion adds, and how to check it is really loaded |
| `docs/verification.md` | Checking your work, recovering from failure |
| `docs/api.md` | The raw HTTP bridge (what each tool relays; for `city_request`) |
| `docs/development.md` | Issues first: how problems are filed, triaged and fixed |
