# Verification

How to prove a change, or an agent's session, did not break the city. The bridge has no
in-game test harness, so these checks run against the live game.

## 1. The thread check, first

```powershell
Invoke-RestMethod http://127.0.0.1:32123/debug/threads
```

Pass: `runsOnSimulationThread: true`, `runsOnMainThread: false`,
`probeThreadId == simulationThreadId`, and `pendingJobs` back at 0 after each command. If
it fails, stop: nothing else is meaningful ([architecture](architecture.md#the-threading-rule)).

## 2. Dry run, act, read back

1. **Dry run** anything expensive or unusual (`dryRun: true`). Read `errors`, `hint`,
   `wouldRemove` and `removesPowerLines`.
2. **Act**, then read the response: `segmentIds`, `position`, `snappedTo`, `removed`.
3. **Read back** the state you changed:

| After | Re-read |
|---|---|
| `city_build_road` | `city_road_anomalies` (a crossing must share a node), `city_map` |
| `city_zone_segment`, `city_zone_area` | `city_zones` |
| `city_place_building`, `city_move_building` | `city_facilities`, `city_problems` (no power, no road?) |
| A water or power building | `city_utilities`, and the building's `active` flag |
| Taxes, budgets, policies | `city_economy`, `city_budgets`, `city_policies` |
| `city_bulldoze` | The anomaly reads: is it gone, did it leave an orphan node? |

The most common mistake: a road that only looks like it crosses another is not a junction
unless the segments share a node. `city_build_road` splits straight crossings for you;
check curved work with `city_road_anomalies`.

## 3. Saves

- Save after every meaningful batch, with a **new name** each time (`city_save`), then
  confirm the file with `city_saves`. Overwriting is how a city gets lost.
- Saves go to `%LOCALAPPDATA%\Colossal Order\Cities_Skylines\Saves\`. The bridge never
  deletes them.
- Try destructive experiments on a copy.

## 4. When something fails

| Symptom | Meaning | Do |
|---|---|---|
| HTTP 409 | No city loaded | Load one |
| "simulation thread is not available" | The game is loading or unloading | Wait |
| A timeout | **Not** "it did not happen" | Re-read the state before retrying. A placement retried blindly places twice |
| `ok: false` with errors and a hint | The game refused | Change the plan |
| The game crashed | Unhandled game exception | Check `Cities_Data\output_log.txt`, reload the last save, open an issue |

## 5. Verifying a bridge change

1. `scripts/restart-game.ps1` (save, rebuild, relaunch, resume).
2. The thread check.
3. Reproduce the issue's steps: the bug no longer happens, or the feature works. Paste
   the tool calls and responses into the PR.
4. Run the reads the change touches and look for new anomalies.
5. Save under a new name, reload it, and compare `city_summary`. A change that scores well
   but corrupts the save has failed.

## 6. Transport lines on a populated city

The first transport checks ran on a city with no residents, so no bus ever had a rider. They
were repeated on the 53k free-play save (`mayor-50k-34`, two bus lines) on 2026-11-15. It was
loaded with `city_load_game` and run at speed 3, and Peach Trees was reloaded afterwards
without saving.

| Read | Game date | Crosstown (14 stops) | North-South (16 stops) |
|---|---|---|---|
| Loaded | 2065-03-06 | 11 buses; passengers temp 270, final 182, average 277 | 10 buses; 212 / 269 / 296 |
| +90 s running | 2065-04-07 | 11 buses; 99 / 342 / 306 | 10 buses; 101 / 331 / 295 |
| Crosstown `activate active=false`, +60 s | 2065-04-30 | **0 buses**, day/night false; 0 / 0 / 207 | 10 buses; 191 / 347 / 319 |
| `activate active=true`, +90 s | 2065-06-04 | **11 buses** again; 167 / 243 / 159 | 10 buses; 203 / 263 / 274 |

What this shows:

- **Vehicles spawn and despawn with the line's switches.** Switching a line off empties it
  within three game weeks. Switching it on brings back the full fleet (the count the budget
  allows), with no other action needed.
- **Ridership counters move with simulated time.** `temp` counts the current period and
  falls back when a new one starts (270 → 99). `final` is the last complete period, and
  `average` is smoothed. A line that is switched off reads 0 / 0 while `average` decays.
  Judge a line by `final` or `average`, and compare two reads taken some game weeks apart.
- `city_transport` reads these while the city runs; no pause is needed.
