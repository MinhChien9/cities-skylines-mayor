# Bridge HTTP API Reference

The in-game bridge listens on **127.0.0.1:32123**. Every endpoint executes on the game's
**simulation thread** — with one named exception: the camera endpoints
(`/commands/set-camera`, `/commands/project-point`) execute on the **Unity main thread**,
the thread that owns the camera and the renderer (see the `MainThreadQueue` class in
`src/Dispatch/`).

> **Pivot note (2026-11-11).** This document describes the *full* HTTP surface, and it
> is still accurate. But the agent no longer reaches all of it: the MCP tools relay the
> reads and the office levers, and `city_request` GETs any endpoint but POSTs only the
> office allowlist (clock, taxes, budgets, policies, saves, line/district/building-active
> management). The build endpoints below (`build-road`, `set-zone`, `place-building`,
> `bulldoze`, `batch`, ...) stay in the bridge for completeness — spatial work is done
> by the agent's cursor through the game's own UI. See `AGENTS.md`.

```powershell
Invoke-RestMethod http://127.0.0.1:32123/state/summary
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/build-road -Body $body -ContentType "application/json"
```

> The route table lives in one place: `src/Bridge/ApiRoutes.cs`. Every row below is
> registered there, next to a one-line description used by the in-game console.
> `scripts/check-api-docs.py` fails the build if this table and that file disagree.

---

## Read endpoints

| Method | Path | Purpose |
|---|---|---|
| GET | `/health` | Bridge liveness. Works before a city is loaded. |
| GET | `/debug/threads` | **Thread proof.** See below. |
| GET | `/state/capabilities` | What the running game supports. See below. |
| GET | `/state/summary` | Compact city overview. |
| GET | `/state/problems` | Problem icons. `?limit=` (default 200). |
| GET | `/state/demand` | RCI demand values. |
| GET | `/state/chirps` | Citizen chatter. `?limit=` (default 50). |
| GET | `/state/zones` | Zoning summary. |
| GET | `/state/economy` | Money, income, expenses, tax rates, budgets. |
| GET | `/state/budgets` | Per-service day/night budget percentages. See below. |
| GET | `/state/policies` | The policy catalogue with loaded/set state. See below. |
| GET | `/state/transport` | Transport lines: stops, vehicles, active state. `?limit=`. See below. |
| GET | `/state/districts` | Districts and special areas (names, types, extents, painted cells). See below. |
| GET | `/state/facilities` | Service facilities. `?limit=`, `?service=`, `?includeMapObjects=`. |
| GET | `/state/growables` | Growable buildings (land available to develop). `?limit=`. |
| GET | `/state/networks` | Roads and networks. `?limit=`, `?service=`. |
| GET | `/state/road-anomalies` | Road defects. `?limit=`, `?nearMissDistance=`, `?shortSegmentLength=`, `?includeDeadEnds=`. |
| GET | `/state/building-anomalies` | Building placement defects. `?limit=`. |
| GET | `/state/zone-anomalies` | Zone defects. `?limit=`, `?minMinorityCells=`, `?minUnzonedCells=`, `?includeUnzonedHoles=`. |
| GET | `/state/external-connections` | Highway / rail / sea connections. `?limit=`. |
| GET | `/state/saves` | Saved games on disk. |
| GET | `/prefabs/buildings` | Available building prefabs. `?service=`. |
| GET | `/prefabs/roads` | Available road prefabs. |
| GET | `/prefabs/networks` | Available network prefabs. `?service=`. |
| GET | `/prefabs/transport` | Transport line prefabs (bus, metro, …) and their vehicles. `?type=`. |

### `/state/summary` — the headline numbers

```json
{
  "ok": true,
  "gameTime": "2027-03-19T13:28:50",
  "buildIndex": 4628,
  "simulation": { "paused": true, "selectedSpeed": 1, "finalSpeed": 0 },
  "network": { "nodes": 1415, "segments": 1412, "lanes": 4521 },
  "population": 0,
  "cash": 65002,
  "happiness": 0,
  "citizenObjects": 1472,
  "demand": { "residential": 100, "commercial": 0, "workplace": 0 }
}
```

| Field | Meaning |
|---|---|
| `population` | **Residents.** The same number the game's own population label shows. |
| `cash` | City treasury in currency units (₡), the same scale the game HUD shows. Negative means a deficit. |
| `happiness` | Average citizen happiness, 0–100. |
| `citizenObjects` | Citizen objects the simulation has allocated. **Not** population — this includes the dummy travellers the outside-connection AI spawns for the map-edge airports, harbours and rail heads, so it reads in the thousands on a map with no citizens at all. Use `population`. |

### `/state/capabilities` — what the running game supports

Read through the supported `ICities` surface (R0 of the roadmap). Values below are from
a verified run on 2026-11-11.

```json
{
  "ok": true,
  "iManagersCaptured": true,
  "version": "1.21.1-f9-epic-win",
  "knownExpansions": ["AfterDark", "Snowfall", "..."],
  "supportedExpansions": ["AfterDark", "Snowfall", "..."],
  "milestones": ["Basic City 1", "Basic City 2", "..."],
  "currentMilestone": "Milestone1",
  "unlockedFeatures": ["Zoning", "Economy", "..."],
  "lockedFeatures": ["Ferry", "Trolleybus", "..."],
  "clock": {
    "simulationTime": "2027-03-19T13:28:50",
    "simulationFrame": 104233,
    "simulationTick": 52116,
    "simulationPaused": true,
    "simulationSpeed": 1,
    "simulationDayTimeHour": 13.4,
    "simulationNightTime": false,
    "renderDayTimeHour": 13.4
  }
}
```

| Field | Meaning |
|---|---|
| `knownExpansions` / `supportedExpansions` | Every `ICities.Expansion` value except `None`, and the subset the install reports as supported (`SupportsExpansion`). |
| `milestones` / `currentMilestone` | Milestone names as the game knows them, and what the city is currently working towards. |
| `unlockedFeatures` / `lockedFeatures` | `UnlockManager.Feature` state: the gate list for milestone-gated gameplay (GameAreas, ParkAreas, CampusAreas, Ferry, Trolleybus, PedestrianZones, …). Ask here before assuming transit or special areas are available. |
| `clock` | The `ICities.IThreading` simulation clock. Read it twice and diff to know how much **simulated** time actually elapsed mid-build — never conclude anything from a zero-sim-time pair of reads. |

`iManagersCaptured: false` means the ICities handle could not be constructed; the other
fields will be missing and the game log has the reason.

### `/state/economy` — the money block

```json
{
  "ok": true,
  "cash": 65002,
  "income": 0,
  "expenses": 272,
  "balance": -272,
  "taxMultiplierPercent": 183,
  "aggregateTaxRates": [ ... ],
  "taxRates": [ ... ]
}
```

Money is reported in the same currency units the game HUD uses. The game stores money
in hundredths and divides by 100 for display, so the default bailout limit of
`-1000000` shows as **−₡10,000** — the figure AGENTS.md quotes.

`taxMultiplierPercent` is the plain percentage the mayor should reason about: **100 is
break-even, above 100 means the city is in deficit**. The game stores the raw value in
units of 1/10000.

### `/state/budgets` — service day/night budgets

```json
{
  "ok": true,
  "cash": 65002, "income": 0, "expenses": 272, "balance": -272, "taxMultiplierPercent": 183,
  "budgetCount": 10,
  "budgets": [
    { "service": "Electricity", "serviceValue": 10, "subService": "None", "day": 100, "night": 100 },
    { "service": "Water",       "serviceValue": 11, "subService": "None", "day": 100, "night": 100 }
  ]
}
```

| Field | Meaning |
|---|---|
| `budgets[].day` / `night` | The service's budget as a percentage, read through the public `EconomyManager.GetBudget`. **100 is the baseline**; 101% buys one extra vehicle per building for ~1% more spend. Day covers 05:00–20:00; night is a separate value the day slider does not touch. |
| Row filtering | A service with `0` on both sides has no budget entry (it cannot spend) and is not listed. |

### `/state/transport` — the line list

```json
{
  "ok": true,
  "lineCount": 1,
  "returned": 1,
  "limit": 251,
  "lines": [
    {
      "id": 1,
      "lineNumber": 1,
      "name": "Blue Line",
      "transportInfo": "Bus",
      "transportType": "Bus",
      "vehicleType": "Bus",
      "service": "PublicTransport",
      "subService": "PublicTransportBus",
      "level": "Level1",
      "complete": true,
      "dayActive": true,
      "nightActive": true,
      "totalLength": 1234.5,
      "ticketPrice": 10,
      "stopCount": 4,
      "stops": [ { "index": 0, "node": 8101, "position": { "x": 240.5, "y": 12, "z": -40.9 } } ],
      "vehicleCount": 3,
      "selectedVehicle": "Bus",
      "vehicles": [ { "id": 10444, "vehicle": "Bus" } ],
      "passengers": { "temp": 0, "final": 0, "average": 0 }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `complete` | The line's stop chain is closed (a circular route) — vehicles only run complete lines. |
| `dayActive` / `nightActive` | The game's service switches (`GetActive` out readback); `false` means the corresponding `DisabledDay`/`DisabledNight` flag is set. |
| `stops[].node` | The stop's own net node id (also visible in `/state/networks`). |
| `selectedVehicle` | The vehicle asset the line will deploy; `null` means the game's default for the line type. |
| `passengers` | The line's own rider counters (temp / final / average), summed over the game's citizen groups. **Ridership accumulates with simulated time** — compare two reads after letting the city run. |

### `/state/policies` — the catalogue is the point

```json
{
  "ok": true,
  "policyCount": 127,
  "loadedCount": 118,
  "cityWideCount": 1,
  "districtCount": 0,
  "districts": [ { "id": 1, "name": "Downtown" } ],
  "policies": [
    { "name": "Recycling", "value": 37, "group": "Services",
      "loaded": true, "cityWide": false, "districts": [] },
    { "name": "TaxRaiseResLow", "value": 64, "group": "Taxation",
      "loaded": true, "cityWide": false, "districts": [] },
    { "name": "Forest", "value": 0, "group": "Specialization",
      "loaded": true, "cityWide": false, "districts": [] },
    { "name": "AnimalEthics", "value": 192, "group": "Park",
      "loaded": false, "cityWide": false, "districts": [] }
  ]
}
```

The enum contains **127 values**: district specializations (`Forest` … `Financial`,
raw values 0–12), then the game's grouped boolean/level policies. `group` is derived
from the game's own grouping enums (`Services`, `Taxation`, `CityPlanning`, `Park`,
`Event`); anything not in a group is reported as `Specialization`, `Reserved` or
`Other`.

| Field | Meaning |
|---|---|
| `loaded` | `IsPolicyLoaded` — the policy exists in this city. Setting an **unloaded** policy is a silent no-op in the game, so `set-policy` refuses it. |
| `cityWide` | `IsCityPolicySet` — set for the whole city. |
| `districts` | The district ids where `IsDistrictPolicySet` is true. Park-group policies live in the **park id space** (`m_parks`), which arrives with the districts release. |
| `districts[]` / `districtCount` | Every created district, with its name. District ids are separate from building ids. |

### `/state/districts` — districts and special areas

Districts and the special areas are two id spaces over the same mechanism: a **512 × 512
grid of 19.2 m cells** (368.64 m² each), where every cell holds up to four `(id, alpha)`
pairs so the game can blend the edges of two adjacent districts. Districts live in
`m_districts` / `m_districtGrid`; special areas live in `m_parks` / `m_parkGrid` and are
told apart by `DistrictPark.ParkType`.

```json
{
  "ok": true,
  "grid": { "resolution": 512, "cellSizeMeters": 19.2, "cellAreaSquareMeters": 368.64,
            "originMeters": -4915.2, "cellCount": 262144, "brushFadeMeters": 38.4 },
  "districtCount": 1,
  "paintedDistrictCells": 118,
  "districts": [
    { "id": 1, "name": "Downtown", "customName": true,
      "areaCells": 96.4, "population": 0, "happiness": 0,
      "specializations": ["Forest"],
      "extentCells": { "minX": 262, "minZ": 238, "maxX": 279, "maxZ": 252 },
      "extent": { "minX": 115.2, "minZ": -345.6, "maxX": 460.8, "maxZ": -76.8 },
      "center": { "x": 288, "z": -211.2 } }
  ],
  "parkCount": 1,
  "paintedParkCells": 64,
  "parks": [
    { "id": 1, "name": "Skyport", "customName": true,
      "parkType": "Airport", "parkLevel": "Level1", "parkGroup": "Airport",
      "isPark": false, "isIndustry": false, "isCampus": false, "isAirport": true,
      "isPedestrianZone": false,
      "mainGate": 0, "functional": false,
      "areaCells": 55.2, "sizeInCells": 6, "areaFactor": 0,
      "extentCells": { "minX": 262, "minZ": 238, "maxX": 279, "maxZ": 252 },
      "extent": { "minX": 115.2, "minZ": -345.6, "maxX": 460.8, "maxZ": -76.8 },
      "center": { "x": 288, "z": -211.2 } }
  ],
  "notes": "One cell is 19.2 m square (368.64 m2). Districts and special areas are separate id spaces. areaCells is the game's own m_totalAlpha / 255, where one fully painted cell counts as 1."
}
```

| Field | Meaning |
|---|---|
| `areaCells` | `m_totalAlpha / 255` — how many cells the entity covers, counting partial brush-edge cells as a fraction. The same number the district panel works from. |
| `paintedDistrictCells` / `paintedParkCells` | Cells on the whole grid that carry any district/area. Compare two reads to prove a paint (or an erase) changed the map. |
| `extentCells` / `extent` | The bounding box, in grid cells and in world metres. A fully unpainted entity reports `null`. |
| `specializations` | The district's `m_specializationPolicies` bits, named. The game keeps one specialization per family (resource / commercial / residential / office). |
| `parkType` / `parkGroup` | `Generic`, `AmusementPark`, `Zoo`, `NatureReserve`, `Industry`, `Farming`, `Forestry`, `Ore`, `Oil`, `GenericCampus`, `TradeSchool`, `LiberalArts`, `University`, `Airport`, `PedestrianZone`. The group is the expansion family (`ParkLife`, `Industry`, `Campus`, `Airport`, `PedestrianZone`). |
| `functional` | `mainGate != 0`. An area created by the bridge has **no main gate yet**, so it is listed and paintable but does not produce visitors/income until its gate building is placed. |

Districts also appear with their names in `/state/policies`; this endpoint is the one that
carries the grid extents, so it is the place to confirm a paint.

### `/debug/threads` — the thread proof

```json
{
  "ok": true,
  "probeOk": true,
  "probeThreadId": 17,
  "simulationThreadId": 17,
  "mainThreadId": 5,
  "httpWorkerThreadId": 33,
  "runsOnSimulationThread": true,
  "runsOnMainThread": false,
  "pendingJobs": 0,
  "gameTime": "2026-03-14T09:41:00",
  "buildIndex": 104233,
  "levelLoaded": true,
  "notes": "probeThreadId must equal simulationThreadId and must NOT equal mainThreadId"
}
```

**`probeThreadId` must equal `simulationThreadId` and must NOT equal `mainThreadId`.**

`pendingJobs` counts the commands currently running on the simulation thread. It should
return to zero as soon as a command completes; if it does not, the simulation thread is
stuck and every later command will time out.

Call this endpoint twice after starting the game: the Unity main thread's id is learned
on the next frame, so the first call can report `mainThreadId: 0`.

---

## Command endpoints

All accept a JSON body. All support `"dryRun": true` where validation is meaningful.

| Method | Path | Body |
|---|---|---|
| POST | `/commands/build-road` | `roadPrefab`, `start:{x,z}`, `end:{x,z}`, `name?`, `dryRun?` |
| POST | `/commands/build-network` | Alias for `build-road`, kept for older agent scripts. |
| POST | `/commands/set-zone` | `zone`, `center:{x,z}`, `radius`, `preserveOccupied?`, `dryRun?` |
| POST | `/commands/place-building` | `buildingPrefab`, `position:{x,z}`, `angleDegrees?`, `dryRun?` |
| POST | `/commands/move-building` | `id`, `position:{x,z}`, `angleDegrees?`, `dryRun?` |
| POST | `/commands/set-building-active` | `id`, `active` |
| POST | `/commands/bulldoze` | `entityType` (`building`\|`netSegment`\|`netNode`), `id`, `keepNodes?`, `dryRun?` |
| POST | `/commands/set-simulation-speed` | `speed` (1–3), `paused` |
| POST | `/commands/set-tax-rate` | `service` (`Residential`\|`Commercial`\|`Industrial`\|`Office`), `rate` (0–20) |
| POST | `/commands/set-budget` | `service`, `budget?` (day, 0–300), `nightBudget?`, `subService?`, `dryRun?` |
| POST | `/commands/set-policy` | `policy`, `set?` (default true), `district?` (0 = city-wide), `dryRun?` |
| POST | `/commands/transport-line` | `action` (`create`\|`add-stop`\|`remove-stop`\|`activate`\|`rename`\|`set-vehicle`), plus action fields. See below. |
| POST | `/commands/district` | `action` (`create`\|`rename`\|`paint`\|`erase`\|`specialize`\|`release`\|`state`), `layer?` (`district`\|`park`), plus action fields. See below. |
| POST | `/commands/save` | `name` |
| POST | `/commands/set-camera` | `x?`, `y?`, `z?`, `distance?`, `verticalAngle?`, `horizontalAngle?`, `edgeScroll?`. See below. |
| POST | `/commands/project-point` | `x`, `z`, `y?`. See below. |
| POST | `/commands/repair-zones-to-growables` | Repair blocks to their growable zone. |
| POST | `/commands/repair-zone-clusters` | Repair mottled zones. `preferGrowableZone?` |
| POST | `/commands/disable-blocked-assets` | Disable the broken "Block Services - …" prefab family. |
| POST | `/commands/batch` | Run several commands in sequence. |

### `/commands/transport-line` — the line editor

One command, six actions — the same things the in-game transport line editor does:

| action | fields | effect |
|---|---|---|
| `create` | `transportInfo` (exact name from `/prefabs/transport`), `newNumber?` | Creates an empty line, numbered per type. |
| `add-stop` | `line`, `position:{x,y,z}` (y optional — terrain is sampled), `index?` (`-1` = append), `fixedPlatform?` | Adds a stop. **A stop placed within ~2.5 units of the line's first stop closes the line into a complete loop.** `dryRun:true` answers the game's own `CanAddStop`. |
| `remove-stop` | `line`, `index` (`-1` = last) | Removes a stop (and its segments), reconnecting the chain. |
| `activate` | `line`, `day?`, `night?`, `active?` | The day/night service switches (`SetActive`): `active=false` disables everything. |
| `rename` | `line`, `name` | Names the line (verified through `GetLineName`). |
| `set-vehicle` | `line`, `vehicle` (asset name) | Assigns the vehicle the line deploys, mapped through the game's own `GetVehicleIndex`. |

Every mutating action returns the live game readback for the line (`lineData`), so act →
re-read → compare happens in one call. Stop positions do **not** snap to roads: pass the
road node positions you built (the same coordinates `/state/networks` reports).

### `/commands/district` — the district and area editor

One command for both id spaces: `layer` picks `district` (default) or `park` (a special
area), and `id` is that space's id. `id` may also be sent as `district` or `park`.

| action | fields | effect |
|---|---|---|
| `create` | `name?`, `layer?`; for an area also `parkType` + `parkLevel?` | `CreateDistrict` / `CreatePark` (`ParkType`, `ParkLevel`). Ids start at 1; 0 is the city-wide pseudo-district. |
| `rename` | `id`, `name` | `SetDistrictName` / `SetParkName` (one-step coroutines, enumerated synchronously). An **empty name clears the custom name** and the game falls back to its generated one. |
| `paint` | `id`, `center:{x,z}`, `radius` | `DistrictTool.ApplyBrush` — the game's own brush. Full strength inside `radius − 38.4 m`, fading to nothing at `radius`; the outer 38.4 m is the two-cell fade. **Practical floor: radius ≥ ~100.** A radius just above 38.4 (e.g. 60) paints *zero* cells because the full-strength core (21.6 m) is smaller than one grid cell; radius 77 is the smallest verified to paint (29.5 cells). |
| `erase` | `center:{x,z}`, `radius`, `id?` | The same brush with id 0. `id` targets one district/area, which is what `release` uses internally; plain `erase` clears whatever is inside the disc. |
| `specialize` | `id`, `specialization`, `set?` (default true) | `SetDistrictPolicy` / `UnsetDistrictPolicy`. The game keeps one specialization per family (resource / commercial / residential / office); setting a competing one replaces it. Districts only — an area's specialization is its `parkType`. |
| `release` | `id` | Removes the entity's cells from the grid (compacting the blended entries, and calling `AreaModified`/`ParksAreaModified`), then `ReleaseDistrict` / `ReleasePark`. The game's release calls do **not** touch the grid themselves. |
| `state` | `id?` | One entity's readback, or the whole `/state/districts` payload when no id is given. |

```json
{
  "ok": true, "dryRun": false, "action": "paint", "layer": "district", "id": 1,
  "center": { "x": 240, "y": 0, "z": -40 }, "radius": 80,
  "areaCells": { "before": 0, "after": 12.6 },
  "cellsInBrush": { "before": 0, "after": 13 },
  "atCenter": { "district": 1, "park": 0, "sampleMask": 2 }
}
```

| Field | Meaning |
|---|---|
| `areaCells` | The entity's `m_totalAlpha / 255` before and after — the game's own area counter. |
| `cellsInBrush` | How many cells inside the brush's bounding box belong to `id` (for `erase`, how many carry any district). A paint that changes nothing shows equal before/after. |
| `atCenter.district` / `atCenter.park` | `GetDistrict` / `GetPark` at the brush centre — the dominant id of that cell. |
| `atCenter.sampleMask` | `SampleDistrict` / `SamplePark`, the game's own **bilinear** sample: a bitmask where bit *n* is set when district *n* is present in the four cells around the point. The district overlay and `GetDistrict` read the dominant cell; the mask shows the blend at the edge. |

Every mutating action returns the live readback. A **created area is not functional**: it has
no main gate (`functional: false`), so place the gate building to give it visitors, income or
production. Painting does not cost money — the game's tool charges the Landscaping resource,
not `ApplyBrush`.

### `/commands/set-camera` and `/commands/project-point` — the office's eye and aiming hand

The glue between office numbers and cursor clicks. Both run on the **Unity main thread**
(the camera and the renderer are Unity objects; city state stays on the simulation thread).

**Which camera:** `ICities.ICamera` (`IManagers.camera` → `CameraWrapper`) turned out to
drive the game's *cinematic* camera — framing through it changes nothing on screen
(verified live 2026-11-11). The camera the cursor sees is **`CameraController`**, the free
camera, whose state is private fields: `m_targetPosition` / `m_currentPosition`,
`m_targetAngle` / `m_currentAngle` (`Vector2`, x = vertical°, y = horizontal°),
`m_targetSize` / `m_currentSize` (zoom distance) and the `m_*Velocity` inertia terms.
`set-camera` writes those fields directly — snap (`instant: true`, default) or glide,
so the office's aim is deterministic. Edge scrolling toggles live through the game's own
`CameraController.m_edgeScrolling` `SavedBool` (no restart).

`set-camera` body — every field optional:

| Field | Meaning |
|---|---|
| `x`, `z` | World position to frame. Given together, or not at all. |
| `y` | World height of the target (default 0 = sea level). |
| `distance` | Camera distance to the target (default: keep current zoom; clamped 100–4000). |
| `verticalAngle` / `horizontalAngle` | Degrees; default to the **current** framing, so re-aiming does not spin or tilt the view. |
| `instant` | `true` (default) snaps the camera to the target the same frame; `false` lets it glide. |
| `edgeScroll` | `true`/`false` to toggle edge scrolling — the ergonomics fix: with edge scrolling on, the camera drifts whenever the cursor touches the window edge. |

With no coordinates and no `edgeScroll` the command is a pure camera-state read.

```json
{
  "ok": true, "action": "target",
  "requested": { "x": 240, "y": 0, "z": -40, "distance": 400, "verticalAngle": 63.2, "horizontalAngle": 45, "instant": true },
  "camera": { "position": { "x": 240, "y": 0, "z": -40 },
              "target": { "x": 240, "y": 0, "z": -40 },
              "angle": { "vertical": 63.2, "horizontal": 45 },
              "distance": 400,
              "edgeScrolling": false },
  "screen": { "width": 1920, "height": 1080 },
  "note": "The camera snapped to the target; the projection is valid immediately."
}
```

`project-point` body: `x`, `z` (required), `y` (optional, default 0).

```json
{
  "ok": true,
  "point": { "x": 240, "y": 0, "z": -40 },
  "unity": { "x": 959.8, "y": 540.2, "z": 388.1, "inFront": true },
  "screen": { "width": 1920, "height": 1080, "onScreen": true },
  "window": { "x": 0, "y": 0, "width": 1920, "height": 1040, "known": true },
  "client": { "x": 959.8, "y": 499.8 },
  "cursor": { "x": 959.8, "y": 499.8 },
  "note": "cursor is absolute desktop pixels, origin top-left, ready for a CUA click; re-project after every camera move."
}
```

| Field | Meaning |
|---|---|
| `unity` | The raw `Camera.WorldToScreenPoint` — origin **bottom-left** of the game window, `z` = distance in front of the camera. |
| `window` | The game window's client rectangle on the desktop (`known: false` when the OS would not tell). |
| `client` | The point in window pixels, origin top-left. |
| `cursor` | **The one to click** — absolute desktop pixels, origin top-left of the primary monitor. |
| `onScreen` | In front of the camera and inside the game window. |

### Examples

**Build a road:**

```powershell
$body = @{
  roadPrefab = "Basic Road"
  start = @{ x = 400; z = 300 }
  end   = @{ x = 423.614; z = 554.945 }
  name  = "Agent Highway Link"
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/build-road -Body $body -ContentType "application/json"
```

**Paint a zone:**

```powershell
$body = @{
  zone = "ResidentialLow"
  preserveOccupied = $true
  center = @{ x = 240; z = -40 }
  radius = 70
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-zone -Body $body -ContentType "application/json"
```

**Create a bus line with two stops (then close the loop with a third near the first):**

```powershell
# 1. the empty line
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/transport-line `
  -Body '{"action":"create","transportInfo":"Bus"}' -ContentType "application/json"
# → { ok: true, line: 1, ... }

# 2. stops, one call each, positions along a road you built
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/transport-line `
  -Body '{"action":"add-stop","line":1,"position":{"x":240,"z":-40}}' -ContentType "application/json"
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/transport-line `
  -Body '{"action":"add-stop","line":1,"position":{"x":420,"z":180}}' -ContentType "application/json"

# 3. ridership leaves after simulated time — compare /state/transport reads
```

**Create a district, paint it, and check the paint:**

```powershell
# 1. the district
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/district `
  -Body '{"action":"create","layer":"district","name":"Downtown"}' -ContentType "application/json"
# → { ok: true, district: 1, state: { ... } }

# 2. paint a disc around a point (radius in metres; the outer 38.4 m fades)
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/district `
  -Body '{"action":"paint","id":1,"center":{"x":240,"z":-40},"radius":80}' -ContentType "application/json"
# → areaCells: { before: 0, after: 12.6 }

# 3. confirm from the grid, not from the paint response
Invoke-RestMethod http://127.0.0.1:32123/state/districts
```

**Aim the cursor at a problem (frame → project → click):**

```powershell
# 1. read a problem's world position from the office
$problem = (Invoke-RestMethod http://127.0.0.1:32123/state/problems).items[0]

# 2. frame the camera on it (and stop edge scrolling from drifting the view)
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-camera `
  -Body ('{"x":' + $problem.position.x + ',"z":' + $problem.position.z + ',"edgeScroll":false}') `
  -ContentType "application/json"

# 3. project the point to cursor pixels (wait a beat for the camera to settle first)
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/project-point `
  -Body ('{"x":' + $problem.position.x + ',"z":' + $problem.position.z + '}') `
  -ContentType "application/json"
# → cursor: { x: ..., y: ... } — the CUA click target
```

**Place a building:**

```powershell
$body = @{
  buildingPrefab = "Water Tower"
  position = @{ x = 120; z = -220 }
  angleDegrees = 0
} | ConvertTo-Json -Depth 5

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/place-building -Body $body -ContentType "application/json"
```

**Set tax and simulation speed:**

```powershell
$body = @{ service = "Commercial"; rate = 9 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-tax-rate -Body $body -ContentType "application/json"

$body = @{ paused = $false; speed = 3 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-simulation-speed -Body $body -ContentType "application/json"
```

**The 101% budget trick and a city-wide policy:**

```powershell
$body = @{ service = "PoliceDepartment"; budget = 101 } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-budget -Body $body -ContentType "application/json"

$body = @{ policy = "Recycling" } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:32123/commands/set-policy -Body $body -ContentType "application/json"
```

Read the budget or policy state straight back to confirm the game accepted it —
`set-budget` reports `day: {before, after}` and `night: {before, after}`, and
`set-policy` reports `before` / `after` as the game itself answers `IsCityPolicySet`,
`IsDistrictPolicySet` or `IsParkPolicySet`. Never trust the "ok" alone.

> **Taxation policies do not change `city_economy`'s tax rates.** `TaxRaise*` /
> `TaxLower*` shift the *effective* rate by ±2 points through
> `GetTaxRate(service, subService, level, districtTaxPolicies)` (the effect path,
> `05-game-mechanics.md` §2). The raw slider array that `/state/economy` reports stays
> untouched, so a set taxation policy plus an unchanged "rate" is the expected state,
> not a bug.

---

## Status codes

| Code | Meaning |
|---|---|
| 200 | Success. Body is the command's JSON result. |
| 404 | Unknown path. |
| 409 | No city is loaded. |
| 500 | The command failed. Body contains `{"ok":false,"error":"..."}`. |

---

## Response shape

Success:

```json
{ "ok": true, "dryRun": false, "buildingId": 4211, "buildingPrefab": "Water Tower", "position": {"x":120,"y":0,"z":-220} }
```

Failure:

```json
{ "ok": false, "error": "Road prefab was not found: Basic Roads" }
```

---

## Notes and gotchas

- **`build-road` reuses nodes.** It looks for an existing node within ~2 units of each
  endpoint and attaches to it instead of creating a duplicate. That is how you get real
  intersections.
- **Zone blocks are created by roads.** If `set-zone` reports it changed nothing, there is
  probably no road within the painted radius.
- **`preserveOccupied` defaults to `true`.** It will not repaint cells that already have a
  developed building. Set it to `false` only when you deliberately want to repaint.
- **Dead ends are not errors.** `includeDeadEnds` defaults to `false` for that reason.
- **Building placement does not snap to roads automatically.** A service building needs
  road access to work; check `city_problems` after placing.
- **`bulldoze` does not refund money.** The refund is applied by the game's tool path,
  which the bridge deliberately does not use.
- **A 409 means "no city is loaded", not "wrong endpoint".** Every endpoint that touches
  city state is refused until a level is loaded. `/health` and `/debug/threads` still work.
- **A 500 with "the simulation thread is not available" means the game is mid-load or
  mid-teardown.** It is deliberately *not* a fallback to running the command on the HTTP
  thread — see `src/Dispatch/SimulationDispatcher.cs`.
