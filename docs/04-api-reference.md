# Bridge HTTP API Reference

The in-game bridge listens on **127.0.0.1:32123**. Every endpoint executes on the game's
**simulation thread**.

The MCP tools relay these endpoints one to one (`mcp-server/CityTools.cs`); the
`city_request` tool is a raw passthrough to any of them, so this document is also the
reference for what the agent can reach.

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

Read through the supported `ICities` surface, captured once from
`ThreadingExtensionBase.OnCreated` (R0 of the roadmap).

```json
{
  "ok": true,
  "iManagersCaptured": true,
  "version": "1.17.1-f4",
  "installedExpansions": ["AfterDark", "Snowfall", "..."],
  "supportedExpansions": ["AfterDark", "Snowfall", "..."],
  "milestones": ["Basic City 1", "Basic City 2", "..."],
  "currentMilestone": "Busy City 1",
  "unlockedFeatures": ["Zoning", "Districts", "..."],
  "lockedFeatures": ["Ferry", "Trolleybus", "..."],
  "clock": {
    "simulationTime": "2027-03-19T13:28:50",
    "simulationFrame": 104233,
    "simulationTick": 52116,
    "simulationPaused": false,
    "simulationSpeed": 3,
    "simulationDayTimeHour": 13.4,
    "simulationNightTime": false,
    "renderDayTimeHour": 13.4
  }
}
```

| Field | Meaning |
|---|---|
| `supportedExpansions` | The `ICities.Expansion` values the loaded install reports as supported. |
| `milestones` / `currentMilestone` | Milestone names as the game knows them — the exact strings `unlock-milestone` takes. `currentMilestone` is what the city is working towards. |
| `unlockedFeatures` / `lockedFeatures` | `UnlockManager.Feature` state: the gate list for milestone-gated gameplay (GameAreas, ParkAreas, CampusAreas, Ferry, Trolleybus, PedestrianZones, …). Ask here before assuming transit or special areas are available. |
| `clock` | The `ICities.IThreading` simulation clock: a first-class timestamp plus frame/tick counters. Read it twice and diff to know how much **simulated** time actually elapsed mid-build — never conclude anything from a zero-sim-time pair of reads. |

However, the fallback must be honest in its response: `iManagersCaptured: true` means
the `ICities` handle is carrying live data for this game session (via `OnCreated` or
the `ManagersWrapper` fallback); `false` means construction failed and the other
fields will be missing.

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
| POST | `/commands/save` | `name` |
| POST | `/commands/repair-zones-to-growables` | Repair blocks to their growable zone. |
| POST | `/commands/repair-zone-clusters` | Repair mottled zones. `preferGrowableZone?` |
| POST | `/commands/disable-blocked-assets` | Disable the broken "Block Services - …" prefab family. |
| POST | `/commands/batch` | Run several commands in sequence. |

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
