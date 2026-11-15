# Tools

The 47 MCP tools, grouped by job. Every tool returns JSON; `city_map` also returns a PNG.
Each tool's own description (what the agent sees) has the full parameter list.

## How building works

- **Same rules as the player.** Networks go through `NetTool.CreateNode` (snapping,
  junctions, collision, slope, money). Buildings follow the `BuildingTool` placement rules:
  zone-grid and shoreline snapping, deep roadside services. A build is tested first and
  only then built.
- **Refusals are information.** A refusal returns the game's error names (`ObjectCollision`,
  `SlopeTooSteep`, `OutOfArea`, `Underwater`, `GridNotFound`, `NotEnoughMoney`, …) plus a
  hint. Change the plan; do not resend the same coordinates.
- **`dryRun: true`** runs the test pass only. Dry runs list what a build would demolish
  (`wouldRemove`, `removesPowerLines`), and real builds report what they removed (`removed`).
- **Honest play.** The real treasury is checked even with the Unlimited Money mod.
  Buildings, networks and zone types that are still locked by milestone are refused.
- **Coordinates** are world metres. `x` runs west to east, `z` runs south to north, and the
  starting tile is centred on 0,0.

## Read the city

| Tool | Returns |
|---|---|
| `city_overview` | **Start here.** Summary, demand, economy, problem icons and chirps in one call |
| `city_summary` | Date, speed, population, cash, happiness, demand, network counts |
| `city_demand` | Residential, commercial and workplace demand, with the game's own target and why |
| `city_economy` · `city_budgets` | Cash, income, expenses, tax rates · per-service budgets (day/night) |
| `city_utilities` | Capacity vs. use for power, water, sewage, garbage, health, death care, education, jobs |
| `city_problems` | Problem icons with positions (no power, no road, abandoned, …) |
| `city_chirps` | Citizen messages, newest first |
| `city_zones` · `city_growables` · `city_facilities` | Zoning per type · zoned buildings · service buildings |
| `city_networks` · `city_external_connections` | Roads, pipes and lines with ids · links to the map edge |
| `city_districts` · `city_policies` | Districts and special areas · the policy catalogue and what is set |
| `city_transport` · `city_transport_prefabs` | Transit lines with stops and ridership · line types and vehicles |
| `city_areas` | Map tiles: owned, buyable, price |
| `city_scenario` | In a scenario: win and lose conditions with live progress, next milestone, zone unlocks |
| `city_capabilities` | Game version, expansions, milestones, unlocked features and zones, enabled and cheat mods |
| `city_saves` | Saved games on disk (also at the main menu) |
| `city_industry` | The goods chain: factory input/output stocks, shops' goods, supply problems, and the buildings in trouble |
| `city_resources` | Ore, oil, forest and fertility per map tile, with where the rich land is (for specialised industry) |
| `city_maps` | Maps and scenarios a new game can start from (also at the main menu) |

## Check for defects

| Tool | Finds |
|---|---|
| `city_road_anomalies` | Crossings without a junction, near misses, short stubs, optional dead ends |
| `city_building_anomalies` | Buildings placed on roads |
| `city_zone_anomalies` | Mottled blocks and unzoned holes |

## See

| Tool | Does |
|---|---|
| `city_map` | A top-down PNG of any square area, drawn from game data: height, water, roads, zones, buildings, locked tiles, coordinates and dead ends. Overlays: `landValue`, `pollution`, `noise`, `traffic`. `saveAs` also writes the PNG to disk |
| `city_camera` | Frames the game camera on a world point, reads the camera, toggles edge scrolling or hides the UI |
| `city_project` | Converts a world point to screen and cursor pixels |

## Build

| Tool | Does |
|---|---|
| `city_build_road` | Builds any network (road, highway, `Water Pipe`, `Power Line`, path, rail): straight, or curved with a control point. Splits at crossings; returns the new segment ids |
| `city_zone_segment` | Zones along one segment: side `left`/`right`/`both` or a compass side, depth 1–4 cells |
| `city_zone_area` | The zone brush: every cell within a radius |
| `city_place_building` · `city_move_building` | Places a service or ploppable building · moves one (moving is free) |
| `city_bulldoze` | Removes a building, segment or node by id |

## Run the city

| Tool | Does |
|---|---|
| `city_set_simulation_speed` | Pause or resume, speed 1–3 |
| `city_set_tax_rate` · `city_set_budget` · `city_set_policy` | Tax per zone · budget per service · a policy, city-wide or per district |
| `city_set_building_active` · `city_empty_building` | Turn a building on or off · empty a full cemetery or landfill |
| `city_unlock_area` | Buys a map tile and pays the land price |
| `city_transport_line` | Creates a line, adds stops, closes the loop, renames it, sets the vehicle, activates it |
| `city_district` | Creates, paints (the game's own brush; radius ≥ ~100), erases, renames, specializes or releases a district or special area |
| `city_save` | Saves to a named slot. Use a new name each time |
| `city_load_game` | Loads a save by name, from the main menu or over the running city; waits for the load and checks the city name |
| `city_new_game` | Starts a new game from a map or a scenario (city name, traffic side); waits for the load |

## Other

| Tool | Does |
|---|---|
| `city_health` · `city_threads` | Is the bridge alive? Do commands run on the simulation thread? |
| `city_activity` | This session's builds and office actions from the activity log, for recall after a context reset |
| `city_request` | Raw access: GET any [bridge endpoint](api.md), POST only to endpoints that have a tool |

## The activity log

Every mutating call (not dry runs or reads; refusals are included) is appended to
`activity/<session>.jsonl`. Each line has the time, the tool, its arguments, a short result,
and the game date, population and cash. `city_activity` reads it back, and other programs
can follow the file. The video project does.
