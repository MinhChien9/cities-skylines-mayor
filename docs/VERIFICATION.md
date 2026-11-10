# Verification & Safety

An AI mayor mutating a live city needs two things: a way to check its work, and a way to
recover when it goes wrong.

---

## 1. The thread gate (do this first)

Before anything else, prove the bridge executes commands on the simulation thread.

```powershell
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

Expected:

```json
{
  "ok": true,
  "probeThreadId": 17,
  "simulationThreadId": 17,
  "mainThreadId": 5,
  "httpWorkerThreadId": 33,
  "runsOnSimulationThread": true,
  "runsOnMainThread": false
}
```

**Pass criteria:**

- `probeThreadId == simulationThreadId`
- `probeThreadId != mainThreadId`
- `runsOnSimulationThread == true`
- `runsOnMainThread == false`

**If `probeThreadId == mainThreadId`, stop.** Every mutation is racing the simulation
thread and will silently corrupt the city. See `docs/ARCHITECTURE.md`.

Via MCP: `city_health`.

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
