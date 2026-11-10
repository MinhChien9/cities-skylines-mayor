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
thread and will silently corrupt the city. See `docs/ARCHITECTURE.md`.

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
| `city_build_road` | `city_infrastructure` — is the new segment present? Any new anomaly? |
| `city_zone` | `city_districts` — did occupancy change? |
| `city_place_building` | `city_problems` — did the building immediately acquire a problem (no power, no road)? |
| `city_economy` | `city_economy` — did the tax rate and cash actually change? |
| `city_bulldoze` | `city_infrastructure` — is the object gone? Did it leave orphaned nodes? |

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
