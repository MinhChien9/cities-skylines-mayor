# Verification & Safety

An AI mayor mutating a live city needs two things: a way to check its work, and a way to
recover when it goes wrong.

---

## 1. The thread gate (do this first) — ✅ VERIFIED PASSING

Before anything else, prove the bridge executes commands on the simulation thread.

```powershell
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

**Actual output from the verified run against a live city:**

```json
{
  "ok": true,
  "probeOk": true,
  "probeThreadId": 2,
  "simulationThreadId": 2,
  "mainThreadId": 1,
  "httpWorkerThreadId": 6,
  "runsOnSimulationThread": true,
  "runsOnMainThread": false,
  "pendingJobs": 0,
  "gameTime": "2030-03-25T09:30:42",
  "buildIndex": 10756,
  "levelLoaded": true
}
```

**Pass criteria — all confirmed:**

- ✅ `probeThreadId == simulationThreadId` (2 == 2)
- ✅ `probeThreadId != mainThreadId` (2 != 1)
- ✅ `runsOnSimulationThread: true`
- ✅ `runsOnMainThread: false`
- ✅ `pendingJobs: 0` (no queue leak)

> **Note:** `mainThreadId` is `0` on the *first* call. The Unity main thread's id is
> learned by dispatching a one-shot probe onto it, which runs on the next frame. Call
> `/debug/threads` a second time to see it. This is cosmetic — the critical value,
> `probeThreadId == simulationThreadId`, is correct on the very first call.

Via MCP: `city_health`.

**If `probeThreadId == mainThreadId`, stop.** Every mutation is racing the simulation
thread and will silently corrupt the city. See `docs/01-architecture.md`.

---

## 1a. End-to-end verification log — ✅ ALL PASSED

Every action was exercised against a live city (10,552 citizens, 1,802 segments) and
verified by re-reading state afterwards.

| # | Action | Result |
|---|---|---|
| 1 | `GET /state/summary` | 10,552 citizens · 1,712 nodes · 1,802 segments · demand R96/C0/W3 ✅ |
| 2 | `GET /state/demand` | Full RCI breakdown with bar colours ✅ |
| 3 | `GET /state/zones` | 2,069 zone blocks · 46,904 cells · 18,704 zoned ✅ |
| 4 | `GET /state/networks` | 1,071 road segments with node ids and positions ✅ |
| 5 | `GET /state/growables` | 902 growable buildings by service ✅ |
| 6 | `GET /state/building-anomalies` | 0 anomalies on a healthy city ✅ |
| 7 | `GET /state/road-anomalies` | 4 pre-existing anomalies, correctly typed ✅ |
| 8 | `GET /prefabs/roads` / `/prefabs/buildings` | 668 building prefabs, exact names ✅ |
| 9 | `POST /commands/build-network` (dryRun) | Validated without mutating ✅ |
| 10 | `POST /commands/build-network` | Created segment 20974, **reused existing node 14960**, created node 6830 ✅ |
| 11 | Build index discipline | 10726 → 10729 = **+1 node, +2 segment** — exactly the game's own rule ✅ |
| 12 | `GET /state/road-anomalies` after build | Correctly flagged the new road as `deadEndNearRoad` ✅ |
| 13 | `POST /commands/bulldoze` | Removed segment **and** its orphan node; anomalies returned to the pre-existing 4 ✅ |
| 14 | `POST /commands/set-zone` (dryRun) | Reported matching blocks and occupied skips ✅ |
| 15 | `POST /commands/set-zone` (real) | 68 cells changed; Office 336 → 404, ResidentialLow 12160 ✅ |
| 16 | `POST /commands/set-zone` (revert) | Office restored to exactly 336 ✅ |
| 17 | `POST /commands/place-building` | Water Tower created (id 27182, service Water) ✅ |
| 18 | `GET /state/building-anomalies` after placement | 0 overlaps — the placement was clean ✅ |
| 19 | `POST /commands/bulldoze` (building) | Removed cleanly, 0 anomalies ✅ |
| 20 | `POST /commands/set-simulation-speed` | `paused:false, selectedSpeed:3, finalSpeed:4` ✅ |
| 21 | Full loop **through MCP** | dryRun → build → snapshot → bulldoze → snapshot restored to the original counts ✅ |

> **Revert caveat:** reversing a zone change restores the *target* zone exactly but cannot
> restore cells that were `Unzoned` before the first change — they become the reverted
> zone. In the verified run, 12 cells that were `Unzoned` became `ResidentialLow`. Treat
> zone changes as one-way unless you snapshot `/state/zones` first.

---

## 1b. Automated checks — ✅ 54 TESTS PASSING

These need no game running:

```powershell
dotnet test .\tests\Cs1Mayor.Mcp.Tests\Cs1Mayor.Mcp.Tests.csproj   # 54 xunit tests
python .\scripts\smoke-test-mcp.py   # MCP protocol handshake
python .\scripts\check-api-docs.py  # API reference matches the route table
```

| Suite | Covers |
|---|---|
| `tests/Cs1Mayor.Mcp.Tests/BridgeClientTests.cs` | JSON parsing, error translation, unreachable-bridge handling |
| `tests/Cs1Mayor.Mcp.Tests/CityToolsTests.cs` | Every tool's endpoint mapping, limit clamping, request bodies, error shape |
| `tests/Cs1Mayor.Mcp.Tests/JsonUtilTests.cs` | The bridge's regex JSON reader (linked straight from `src/Support/`) |

What automated tests **cannot** cover: anything that touches real game state. The
in-game mod has no test harness — it is verified by the live-city procedure above.

---

## 1c. Bugs found and fixed

The Phase 1 and Phase 2 runs were repeated on a fresh map against the live game. Both
phases pass. The run surfaced two data bugs, now fixed.

| Bug | What was wrong | Fix |
|---|---|---|
| `/state/summary` reported the wrong population | It reported `CitizenManager.m_citizenCount`, which counts allocated citizen *objects*. That includes the dummy travellers the outside-connection AI spawns for the map-edge airports and rail heads, so a map with no citizens read 1410 while the HUD, save panel and load panel all read 0. It also drifted on its own between reads of the same map. | Report `District.m_populationData.m_finalCount` as `population` — the exact value the game's own label uses. The raw count stays available as `citizenObjects`. |
| `/state/economy` had no money in it | It returned tax rates only, so the cash, income, expenses and tax multiplier that AGENTS.md's economy table asks for were unreadable. | Report `cash`, `income`, `expenses`, `balance` and `taxMultiplierPercent`. |

Two unit details had to be got right, and each was checked against the game's own code
rather than guessed:

- The game stores money in hundredths and divides by 100 for display, so the default
  bailout limit of `-1000000` shows as **−₡10,000** — the figure AGENTS.md quotes. The
  bridge divides by 100 to match.
- The tax multiplier is an integer in units of 1/10000, where 10000 is 100%. It is
  private, so it is read by reflection, as `m_taxRates` already is.

Verified against the live game: `population: 0` matches the HUD's population label,
`cash: 65002` and `balance: -272` match the HUD's **−€272**.

---

## 1d. R0 verification — the `ICities` surface — ✅ VERIFIED LIVE (2026-11-11)

`GET /state/capabilities` against a live city (San Vegas, paused at 2027-03-19):

| Check | Result |
|---|---|
| `iManagersCaptured` | `true` |
| `version` | `1.21.1-f9-epic-win` — matches the installed build |
| `supportedExpansions` | all 14 installed DLC reported || `milestones` / `currentMilestone` | hundreds of names as the game knows them; `currentMilestone: "Milestone1"` — correct for a fresh city |
| `unlockedFeatures` / `lockedFeatures` | `Zoning, Economy, Budget, GameAreas, Bulldozer, InfoViews` unlocked; 47 features locked — matches a city at Milestone 1 |
| `clock` | `simulationTime`, `simulationFrame`, `simulationTick`, `simulationPaused: true`, `simulationSpeed`, day-time hour |

**One architecture finding, caught by this verification:** for a mod dropped into the
Mods folder the game loads the assembly and constructs `IUserMod`, but *never
instantiates the extension bases* — neither `ThreadingExtensionBase.OnCreated` nor
`OnLevelLoaded` fired (the game log shows both silent). So the sanctioned capture hook
cannot be the only route, and `AgentBridge.EnsureICitiesManagers()` lazily constructs
the game's own public `ManagersWrapper` on the simulation thread as a fallback. The
game log confirms exactly that path:

```
[SkylinesAgentBridge] ICities surface captured via ManagersWrapper (OnCreated did not run).
```

**Clock discipline (the point of the `clock` block):**

| Read | `simulationTime` | `simulationTick` |
|---|---|---|
| paused, read 1 | 2027-03-19T13:28:50 | 2909421 |
| paused, read 2 | 2027-03-19T13:28:50 (frozen) | 2909424 (still advancing) |
| running 3× for ~3 s | 2027-03-20T19:10:34 | 2909607 |
| re-paused | 2027-03-20T19:30:15 | — |

Two conclusions, both measured rather than assumed: a pair of reads with identical
`simulationTime` means **zero simulated time elapsed** (never conclude anything from
them), and **the simulation thread does step its action queue while the game is
paused** — `simulationTick` kept advancing across two paused reads, confirming the
dispatcher's commands are drained even paused (the first row of the roadmap's known
open questions).

The city was restored to its exact paused state after the 3-second run.

---

## 1e. R1 verification — budgets & policies — ✅ VERIFIED LIVE (2026-11-11, PR #7)

Fresh game restart with the new DLL (`cities-resume` save through the Epic DLC launch).
Thread proof repeated first: `probeThreadId 2 == simulationThreadId 2 != mainThreadId 1`.

### `GET /state/budgets` and `POST /commands/set-budget`

| Check | Result |
|---|---|
| Catalogue | 20 services listed, all at `day:100 night:100` — the filtering rule (hide zero-budget services) works: exactly the services that have a budget row appear ✅ |
| 101% trick | `Electricity` day 100 → 101 via `set-budget`; **independent** `/state/budgets` readback shows `day:101` ✅ |
| Day/night split | `Water` with `budget:105, nightBudget:90` → `day:105 night:90` ✅ |
| `dryRun` | Reported `after` equal to `before` (100/100), Water untouched afterwards ✅ |
| Night untouched by a day-only call | Electricity `night` stayed 100 ✅ |
| Baselines restored | Electricity and PoliceDepartment back to 100/100; confirmed by readback ✅ |

### `GET /state/policies` and `POST /commands/set-policy`

| Check | Result |
|---|---|
| Catalogue | `policyCount:126` (127 minus `None`), `loadedCount:116`, groups derived as designed — `Recycling`→Services, `TaxRaiseResLow`→Taxation, `SmallBusiness`/`EncourageBiking`→CityPlanning, `Forest`→Specialization, `Reserved`→Reserved, `AnimalEthics`→Park ✅ |
| `loaded:false` set | `Recycling` with `loaded:true` set city-wide: `before:false → after:true`; the **independent** catalogue readback shows `cityWide:true, cityWideCount:1`; unset restored `after:false` ✅ |
| `dryRun` | Set reported `set:false` and changed nothing ✅ |
| Unloaded policy refusal | `StuddedTires` (loaded:false) → clean error: *"setting it would be a silent no-op"* — no state touched ✅ |
| Nonexistent district refusal | `district:1` with 0 districts created → clean error listing every created district ✅ |
| Taxation policies | `TaxRaiseResLow` set/unset works through the state machine, **but the aggregate tax rate in `/state/economy` stays 9** — by design: taxation policies shift ±2 points through `GetTaxRate(service, subService, level, districtTaxPolicies)` (`05-game-mechanics.md` §2), the *effect* path, not the raw slider array that `city_economy` reads. Do not conclude a tax policy "did not work" from `city_economy` alone. ✅ |

Park-group policies accept a *park* id (`IsParkPolicySet` family), which arrives with the
R3 districts release. Verified: the state save `R1-verify-budgets-policies.crp` (5.28 MB)
is on disk after the run.

---

## 1f. R2 verification — transport lines — ⏳ IMPLEMENTED, LIVE RUN PENDING

*(Code shipped 2026-11-11; the mod DLL is installed and the endpoints compile against
the live game assemblies. The live run below follows the same act → re-read → compare
discipline, and needs the game restarted with the new DLL first.)*

| Check | How |
|---|---|
| Prefab catalogue | `GET /prefabs/transport` enumerates the `TransportInfo` assets (the R2 open question about `PrefabCollection<TransportInfo>`) — bus, metro, tram … with their classes, economics and vehicle lists. |
| Create | `transport-line create` → the response `line` id plus `lineData`; an **independent** `GET /state/transport` shows the line with `complete:false`, default `selectedVehicle`. |
| Stop-chain | `add-stop` → `lineData.stopCount` grows; the stop node positions read back from `NetManager`. `dryRun` answers `CanAddStop` and mutates nothing. |
| Loop closing | A stop within ~2.5 units of the first stop → `complete:true` (the game closes the chain itself). |
| Stop removal | `remove-stop` → `stopCount` drops, the chain reconnects. |
| Day/night service | `activate` day/night → `GetActive` readback flips, independently confirmed in `/state/transport`. |
| Rename | `rename` → `GetLineName` before/after in the same response. |
| Vehicle | `set-vehicle` → `GetLineVehicle` readback flips; `GetVehicleIndex` refuses unknown names with the compatible list. |
| Ridership | With a complete line and its depot connected, let simulated time run (speed 3, unpaused) → `vehicleCount` leaves from 0 and `passengers` counters start climbing. |
| Unemployment effects | Optional cross-check: `/state/growables` and land value in the zoned blocks the line serves. |
| Save the state | `city_save R2-verify-transport` once the line runs. |

Because a bus depot must exist for vehicles to spawn (the game's spawn AI needs a depot
of the same service/starvation class), the live run starts on a save that has a bus depot
or builds one first with `city_place_building` — if `vehicleCount` stays 0 with a
`complete` line, `city_facilities?service=PublicTransport` is the next read, not a retry.

---

## 2. Dry runs

Every mutating tool accepts `dryRun=true`:

```jsonc
// city_build_road
{ "roadPrefab": "Basic Road", "startX": 400, "startZ": 300,
  "endX": 423.6, "endZ": 554.9, "dryRun": true }
```

The bridge validates the prefab exists, the length is sane, and the terrain can be sampled
— without mutating anything. Use it before every non-trivial build.

---

## 3. Read-back verification

The core loop is **act → re-read → compare**. The bridge returns structured state; the
agent should diff against what it expected.

| After this action | Re-read this |
|---|---|
| `city_build_road` | `city_road_anomalies` — is the new segment present? Any new anomaly? |
| `city_zone` | `city_zones` — did occupancy change? |
| `city_place_building` | `city_problems` — did the building immediately acquire a problem (no power, no road)? |
| `city_set_tax_rate` | `city_economy` — did the tax rate and cash actually change? |
| `city_bulldoze` | `city_road_anomalies` / `city_building_anomalies` — is the object gone? Did it leave orphaned nodes? |

### The anomaly checks the bridge provides

- **`/state/road-anomalies`** — disconnected crossings, near-misses (segments that pass
  close without sharing a node), short stubs, visual overlaps, dead ends (opt-in),
  terrain cliffs, sunken roads.
- **`/state/building-anomalies`** — buildings that overlap roads or each other, buildings
  with no road access.
- **`/state/zone-anomalies`** — mottled blocks, mixed zones in one block, unzoned holes.

`nearMissDistance` (default 18) and `shortSegmentLength` (default 32) tune the road checks.

> **The single most common CS1 building mistake:** a road that visually crosses another
> road is **not** an intersection unless both segments share a node. Build segments that
> share endpoints so the bridge reuses the existing node.

---

## 4. Save discipline

```jsonc
// city_save
{ "name": "AgentAutoSave" }
```

The tool returns both the save result and the current save list, so you can confirm the
`.crp` appeared. Saves land in:

```
%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Saves\
```

**Rules:**

- Save after any meaningful change.
- Verify the file appeared — do not assume.
- Use **incrementing names** (`AgentAutoSave-001`, `-002`). CS1 save files are fragile and
  overwriting is how you lose a city.
- Never load a save while the game is mid-save or mid-load.

---

## 5. Failure modes and recovery

| Symptom | Cause | Action |
|---|---|---|
| Bridge unreachable | Game not running, or no city loaded | Start the game, load a city, retry |
| `HTTP 409 No city is loaded` | Level not loaded yet | Load a city |
| Tool returns `ok:false` with a manager exception | Invalid prefab / bad coordinates / occupied space | Read the message, adjust, retry |
| Command times out | Simulation busy or game loading | Wait, then re-read state to find out whether it applied |
| `runsOnSimulationThread:false` | Broken build | Rebuild and reinstall the mod |
| Game crashed | Unhandled exception in game code | Check `Cities_Data\output_log.txt`; restart from the last save |

**Timeouts do not mean "did not happen".** A command may have applied just before the
timeout. Always re-read state before retrying, or you may double-apply.

---

## 6. Idempotency

MCP clients may retry a call after a timeout. The tools are written to be as idempotent as
possible:

- `city_build_road` reuses a nearby existing node rather than creating a duplicate.
- `city_zone` with `preserveOccupied=true` (the default) will not repaint developed cells.
- `city_bulldoze` on an already-removed object fails cleanly rather than corrupting state.

But **`city_place_building` is not idempotent** — calling it twice places two buildings.
Always re-read state before retrying a placement.

---

## 7. Development safety

Since the agent creates its own city, corruption costs you the run, not a saved game.
Even so:

- Work on a **copy** of a save when experimenting with anything destructive.
- The upstream project's warning stands: *"Test on throwaway saves first."*
- The bridge never deletes saves. It only writes new ones.

---

## 8. Evaluating the agent

A mayor that cannot be scored is a demo, not an agent. Run the city at 3× for N simulated
months, then measure:

| Metric | Source |
|---|---|
| Population | `/state/summary` |
| Cash curve | `/state/economy` |
| Service coverage % | `/state/facilities` |
| Road connectivity | `/state/road-anomalies` (should be 0) |
| Problem icons by type | `/state/problems` |
| **Save integrity** | Reload the save and diff the snapshot |

The last one matters most: a city that scores well but produces a corrupt save has failed.
