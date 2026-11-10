# Command Surface — every action in the game, reachable by the agent

> **Status: master table + implementation plan.** The bridge today covers roads, zoning,
> buildings, bulldozing, saves, taxes, simulation speed and a rich set of state reads —
> exposed to the agent as **raw 1:1 MCP tools** (`mcp-server/CityTools.cs`, one tool per
> endpoint, plus the `city_request` passthrough). Everything else below is the target:
> every action the game itself can perform becomes one bridge endpoint + one raw tool.
> API names marked **[V]** were verified by reflection-only inspection of the live game's
> `Cities_Data\Managed\Assembly-CSharp.dll` and `ICities.dll` (Windows PowerShell
> `ReflectionOnlyLoadFrom`), so the signatures quoted are the real ones, not remembered
> ones. **[?]** marks anything that still needs confirming at implementation time.

---

## 1. The promise

> Everything the player can do, the agent can do — as raw commands.

The server adds **no decision logic on top**: one bridge endpoint, one tool, the response
passed through untouched. It is deliberately stronger than "the agent can build a city" —
it means the transport-line editor, the policy panel, the terrain tool, the milestone
tree, the district brush and the airport-area painter are all reachable. The agent
decides *what to do with them* from the stats the read endpoints return.

**How parity is judged:** for each row in the matrix below, the agent can perform the
action and the game state changes in the way the player's action would have changed it —
verified by reading the state back, not by "the call returned ok".

---

## 2. The four tiers

Not every player action has a convenient API. Each capability is reachable by exactly
one of four routes, and the route is part of the design, not an accident.

| Tier | Route | How | Cost | Risk |
|---|---|---|---|---|
| **0** | **`ICities` — the game's supported modding API** | The bridge implements an extension base and receives the interface | Fast, documented, patch-stable | **Lowest** — this is the API Colossal ships for exactly this |
| **1** | Public game API | The bridge calls `Assembly-CSharp`'s own public methods on the simulation thread | Fast, deterministic | Low — these are the APIs the game itself uses |
| **2** | Reflection into private state | The bridge reaches a private field or method by name, as it already does for `EconomyManager.m_taxRates` | Fast, but tied to one game version | Medium — a patch can rename a field |
| **3** | UI automation | The MCP server drives the game window with OS-level mouse and keyboard | ~1 s per action, needs the window focused | Brittle — but it is *literally what the player does* |

### Tier 0 is the important discovery

Cities: Skylines ships a first-class modding interface set in `ICities.dll` **[V]**. It
is not documented in this repo, and the project did not know about it until this design
pass inspected the live installation. It covers a large part of what the parity promise
needs — **terrain, resources, saving and loading, map tiles, milestones, the camera and
building placement** — through a supported, public interface.

**The bridge can already get it.** `ICities.ThreadingExtensionBase.OnCreated(IThreading
threading)` hands the mod an `IManagers` **[V]**, and the bridge *already* implements
`ThreadingExtensionBase` (`src/Bridge/AgentBridgeThreading.cs`). One field to capture
the interface, and the whole surface opens up:

```
IManagers ─┬─ get_areas()            IAreas             map tiles
            ├─ get_terrain()         ITerrain           heights
            ├─ get_resource()        IResource          ore / oil / forest / fertility
            ├─ get_serializableData() ISerializableData  save & load games
            ├─ get_milestones()      IMilestones        milestone read & unlock
            ├─ get_camera()          ICamera            framing, fades, dolly
            ├─ get_building()        IBuilding          place / relocate / release
            ├─ get_threading()       IThreading         sim speed, pause, tick counters
            ├─ get_disaster()        IDisaster          disasters on demand
            ├─ get_economy()         IEconomy           money
            ├─ get_application()     IApplication       version, expansion support
            ├─ get_chirper()         IChirper           citizen chatter
            └─ get_effects()         IEffects           lights, particles, sounds
```

The extension bases also give **hooks**, not just handles:
`TerrainExtensionBase.OnAfterHeightsModified`, `ResourceExtensionBase.OnAfterResourcesModified`,
`MilestonesExtensionBase.OnRefreshMilestones` / `OnGetPopulationTarget`,
`SerializableDataExtensionBase.OnLoadData` / `OnSaveData`,
`LoadingExtensionBase.OnLevelLoaded` **[V]**.

> **This changes the plan.** Terrain editing, save/load and camera framing were rated
> Tier 2 / Tier 3 in the first draft of this document. They are **Tier 0**. That is
> cheaper, safer and faster to build than the alternative.

### The tier-3 escape hatch

`scripts/game-ui.ps1` already solves the hard parts of driving CS1's window:

- Unity renders its whole UI into a texture, so the Windows accessibility tree is
  useless — input must be injected at the OS level.
- Windows refuses `SetForegroundWindow` from a background process; the script holds
  **ALT** while calling it.
- Screenshot pixels are scaled relative to the real window, and the script maps them
  back.

The MCP server runs on the player's machine, so it can call that machinery directly.
The MCP client calls a tool; the tool clicks the same button the player would click.
**This is what turns parity from aspirational into enforceable**: if no API exists, the
feature is still reachable.

Tier 3 is a fallback, never the first choice. It is slow, it needs the window focused
(it steals focus from whatever the user is doing), and it cannot be reasoned about
numerically — which is why the bridge's numeric state endpoints exist at all.

### Choosing a tier

```
Is it in ICities?                     → Tier 0
Does a public game API do it?         → Tier 1
Is the private state well understood? → Tier 2
Is it a UI-driven feature?            → Tier 3
```

---

## 3. The matrix

| # | Capability | Tier | Bridge endpoint | MCP tool | Difficulty |
|---|---|---|---|---|---|
| 1 | Roads, zoning, buildings, bulldoze | **1** (today) | `/commands/*` | `city_build_road`, `city_zone`, `city_place_building`, `city_bulldoze` | Done |
| 2 | **Service budgets** | **1** | `GET /state/budgets`, `POST /commands/set-budget` | `city_budgets`, `city_set_budget` | Easy |
| 3 | **Policies** (127) | **1** | `GET /state/policies`, `POST /commands/set-policy` | `city_policies`, `city_set_policy` | Easy |
| 4 | **Transport lines** | **1** + **3** | `GET /state/transport`, `POST /commands/transport-line` | `city_transport` | Hard |
| 5 | **Districts** (create / paint / specialise) | **2** + **3** | `GET /state/parks`, `POST /commands/district` | `city_district` | Hard |
| 6 | **Special areas** (airport / campus / pedestrian / industry) | **2** + **3** | `POST /commands/district` | `city_district` | Hard |
| 7 | **Milestones & map tiles** | **0** | `GET /state/progress`, `POST /commands/unlock-milestone`, `POST /commands/unlock-area` | `city_progress`, `city_unlock` | Easy to build; **a cheat** (§4.6) |
| 8 | **Props & landscaping** (trees, benches, lamps) | **1** | `GET /prefabs/props`, `POST /commands/place-prop` | `city_props`, `city_place_prop` | Easy |
| 9 | **Parks & plazas** | **1** (they are buildings) | `POST /commands/place-building` | `city_place_building` | Done |
| 10 | **Industry chains & cargo** | **1** + **2** | `GET /state/industry`, cargo transport lines | `city_industry` | Medium |
| 11 | **Terrain edits & natural resources** | **0** | `POST /commands/edit-terrain`, `GET /state/resources` | `city_terrain` | Easy |
| 12 | **Screenshot & camera** | **0** | `POST /commands/render-view` | `city_view` | Easy |
| 13 | **Save & load, rename** | **0** + **1** | `/state/saves`, `POST /commands/save`, `POST /commands/load` | `city_save`, `city_load` | Easy (load pending §7) |
| 14 | **Loans & tariff panels** | **1** + **2** | `POST /commands/set-loan` | `city_set_loan` | Medium |
| 15 | **Game speed / pause / simulation clock** | **0** + **1** | `POST /commands/set-simulation-speed` | `city_set_simulation_speed` | Done |
| 16 | **Disasters on demand** | **0** | `POST /commands/spawn-disaster` | `city_disaster` | Easy |
| 17 | **Citizen chatter / notifications** | **0** | `GET /state/chirps` | `city_chirps` | Done |

---

## 4. Domain notes

### 4.0 Getting `ICities` — the first piece of work

The bridge already implements `ICities.ThreadingExtensionBase` for the thread probe.
The change is small **[V]**:

```csharp
// src/Bridge/AgentBridgeThreading.cs — capture the interface on creation
public void OnCreated(IThreading threading)
{
    m_managers = threading.get_managers();   // ← the whole ICities surface
    ...
}
```

Then `m_managers.get_terrain()`, `get_camera()`, `get_serializableData()` and friends
are available to every command. **Do this first** — it is a prerequisite for rows 7,
11, 12, 13, 15 and 16, and it costs one bridge build. *(Done — R0 in the roadmap.
Live-game caveat: for a mod dropped into the Mods folder the game never instantiates
the extension bases, so `AgentBridge.EnsureICitiesManagers()` falls back to
constructing the game's public `ManagersWrapper` on the simulation thread.)*

Note that `ICities.ITerrain`, `ICities.IAreas` and friends are **interfaces implemented
by the game** (`AreasWrapper` implements `IAreas` internally). The bridge must treat
them as opaque handles and never reflect into their implementations.

### 4.1 Service budgets — Tier 1

The economics of the 101% trick are already documented in `05-game-mechanics.md` §3,
and the game exposes the setter publicly **[V]**:

```
EconomyManager.SetBudget(Service service, SubService subService, int budget, Boolean night)
EconomyManager.GetBudget(Service service, SubService subService, Boolean night)
EconomyManager.GetBudget(ItemClass itemClass) / (ItemClass, Boolean night)
fields: int[] m_serviceBudgetDay, int[] m_serviceBudgetNight
```

`budget` is a **percentage** (100 = 100%). Day is 05:00–20:00, so the night variant is
a separate value. The MCP tool should take `{service, subService?, budget, night?}` and
return the before/after for every target it touched — the same shape as `set-tax-rate`.

### 4.2 Policies — Tier 1

`DistrictPolicies.Policies` is an enum with **127 values** **[V]**, grouped into
`Services`, `Taxation`, `CityPlanning`, `Park` and `Event` sub-enums. Some are
**levels** (e.g. `TaxRaiseResLow` is a ±2 point tax shift), some are **booleans**
(`Recycling`, `ElectricCars`, `FreeTransport`).

The game exposes the state machine publicly **[V]**:

```
DistrictManager.IsCityPolicySet(Policies policy)
DistrictManager.SetCityPolicy(Policies policy)      // city-wide
DistrictManager.UnsetCityPolicy(Policies policy)
DistrictManager.SetDistrictPolicy(Policies policy, Byte district)   // per district
DistrictManager.UnsetDistrictPolicy(Policies policy, Byte district)
DistrictManager.IsPolicyLoaded(Policies policy)      // is it available yet?
```

Two subtleties:

1. A policy must be **loaded** before it can be set (`IsPolicyLoaded`). Setting an
   unloaded policy is a no-op — the tool must check and report that clearly.
2. City-wide vs per-district is a real distinction, and the agent will want both.

The MCP tool needs a **policy catalogue** endpoint (`/state/policies`) that returns the
full enum with names, groups, current state (city-wide + per district), and whether it
is loaded. 127 policies with no catalogue is unusable; the catalogue is the point.

### 4.3 Transport lines — Tier 1 for the model, Tier 3 for the authoring

The line model is fully public **[V]**:

```
TransportManager.CreateLine(out UInt16 lineID, out Randomizer randomizer,
                            TransportInfo info, Boolean newNumber)
TransportManager.UpdateLine(UInt16 lineID)
TransportManager.SetLineName(UInt16 lineID, String name)      // IEnumerator
TransportManager.GetLineName(UInt16 lineID)
TransportLine.AddStop(UInt16 lineID, Int32 index, Vector3 position, Boolean fixedPlatform)
TransportLine.CanAddStop(UInt16 lineID, Int32 addIndex, Vector3 position)
TransportLine.RemoveStop(UInt16 lineID, Int32 index)
TransportLine.CountStops(UInt16 lineID) / GetStop(Int32) / ClearStops(UInt16)
TransportLine.SetActive(Boolean day, Boolean night)
TransportLine.ReplaceVehicles(UInt16 lineID, VehicleInfo info)
TransportLine.CountVehicles(UInt16 lineID)
```

**The hard part is not the API — it is the geometry.** A CS1 transport line is a *path
of net nodes and segments* through the road network, plus an ordered list of stops. The
game's `TransportTool` draws that path with the mouse; an agent has to *derive* it.

Two viable strategies:

| Strategy | How | Quality |
|---|---|---|
| **A — walk existing roads** | Take a list of road nodes the caller supplies (or derive them from a path query), create the line, add a stop at each | Deterministic, scriptable, reuses the road network the agent already built |
| **B — drive the UI** | Open the transport tool, click the stops in screen space, press enter | Exactly what the player does; slow; needs screen coordinates per stop |

Strategy A is the primary. It needs one more piece: the list of `TransportInfo` assets
(bus / metro / tram / ferry / cargo) and their default vehicles. `PrefabCollection<TransportInfo>`
should enumerate them the same way `PrefabCollection<NetInfo>` already does for roads
**[?]** — verify. The existing `/prefabs/networks` endpoint lists **NetInfo** assets
(roads), *not* transport lines; a separate catalogue is needed.

**Practical order:** bus lines first (cheapest, biggest effect, unlocked at Boom Town),
then tram, then metro. Cargo lines matter for industry level-ups but are a separate
vehicle class with different economics.

### 4.4 Districts — Tier 2 + Tier 3

Creating a district is public **[V]**:

```
DistrictManager.CreateDistrict(out Byte district)
DistrictManager.SetDistrictName(Int32 district, String name)   // IEnumerator
DistrictManager.GetDistrict(Vector3 worldPos) / SampleDistrict(Vector3)
DistrictManager.GetDistrictName(Int32 district)
DistrictManager.ReleaseDistrict(Byte district)
```

**Painting** is the hard part. The district map is a private `Cell[] m_districtGrid`
**[V]** — one byte per cell encoding district + alpha, exactly like the zone grid. The
game's `DistrictTool` writes into it and then calls
`DistrictManager.AreaModified(minX, minZ, maxX, maxZ, fullUpdate)` to refresh the
texture.

So painting is **Tier 2**: write the cells (respecting the game's cell-index bit layout
`index = (z << 3) | ((x & 1) << 2)`), then call `AreaModified`. Tier 3 (the district
brush in the UI) is the fallback and the reference implementation to compare against.

The MCP tool should mirror the zone tool's shape: `city_district(action, center, radius, …)`
so the agent's mental model stays "paint a disc".

**Specialisation** (Forest / Farming / Oil / Ore / Leisure / Tourist / Hightech /
Organic / Selfsufficient / WallToWall / Financial) is a district property — the same
`Policies` enum covers those values, so it is a `SetDistrictPolicy`-style call on the
district's data **[?]**.

### 4.5 Special areas — airport, campus, pedestrian, industry — Tier 2 + Tier 3

This is the finding that changes the shape of the work: **special areas are districts
of a different kind.** `DistrictPark.ParkType` **[V]** contains `GenericCampus`,
`Airport`, `PedestrianZone`, and `DistrictPark.ParkGroup` contains `Campus`, `Airport`,
`PedestrianZone`. The manager exposes **[V]**:

```
DistrictManager.CreatePark(out Byte park, ParkType type, ParkLevel level)
DistrictManager.AddParkBuilding(Byte park, BuildingInfo info, ParkType type)
DistrictManager.GetParkArea(Byte park, out minX, minZ, maxX, maxZ)
```

So the airport-area painter is the *same mechanism* as the district painter, with a
different `ParkType`. One implementation covers districts, parks, airport areas, campus
areas and pedestrian zones.

Whether the industry area uses a `ParkType` at all **[?]** — the enum grep did not show
an `Industry` member, so the industry area may be a distinct mechanism. Confirm before
implementing.

### 4.6 Milestones and map tiles — Tier 0, but a cheat

Reading is open and cheap. Through `ICities` **[V]**:

```
IMilestones.EnumerateMilestones()      → the milestone names
IMilestones.UnlockMilestone(name)      → the unlock (see the policy note below)
```

and through the game API **[V]**:

```
UnlockManager.GetCurrentMilestone()
UnlockManager.Unlocked(MilestoneInfo milestone)
UnlockManager.Unlocked(Feature feature)       // AirportAreas, CampusAreas, Metro, …
UnlockManager.GetMilestone(Policies policy)
UnlockManager.Unlocked(Service) / (SubService) / (Zone) / (Int32 areaIndex)
MilestoneInfo.GetProgress() / IsPassed() / GetLocalizedName() / GetComparisonValue()
```

`Feature` is the important one: it is the gate list for everything milestone-gated
(AirportAreas, CampusAreas, IndustryAreas, ParkAreas, Ferry, Trolleybus, IntercityBus,
Helicopter, Airline, PedestrianZones, WallToWallSpecializations, Investments, …) **[V]**.
`Unlocked(Feature)` is how the agent asks "can I use metro yet?" without guessing.

Map tiles are Tier 0 as well **[V]**:

```
IAreas.IsAreaUnlocked(Int32 x, Int32 z)
IAreas.CanUnlockArea(Int32 x, Int32 z)
IAreas.GetAreaPrice(Int32 x, Int32 z)
IAreas.UnlockArea(Int32 x, Int32 z, Boolean requireMoney)
IAreas.unlockedAreaCount / get_startTileX / get_startTileZ
```

> `AGENTS.md` §7 is right and stays: the tile-unlock API **is** `IAreas.UnlockArea(x, z)`.
> It lives in `ICities.dll`, not `Assembly-CSharp.dll`, which is why a search of the
> latter does not find it. `GameAreaManager.UnlockArea(int tileIndex)` **[V]** is the
> game's internal equivalent.

**The policy: these are exposed as ordinary commands — the server does not refuse
anything.** Milestone and map-tile unlocks are cheats that invalidate the city's
economics and forfeit achievements; `AGENTS.md` §4 rule 16 tells the agent to avoid them
unless the user explicitly asks. The commands exist, because the server's job is reach-
ability, not judgement.

### 4.7 Props and landscaping — Tier 1

Trees, benches, lamps and railings are `PropInfo` assets, and placement is public **[V]**:

```
PropManager.CreateProp(out UInt16 prop, ref Randomizer randomizer, PropInfo info,
                       Vector3 position, Single angle, Boolean single)
PropManager.ReleaseProp(UInt16 prop)
PropManager.MoveProp(UInt16 prop, Vector3 position)
PropInfo  (asset: name, service, subService, scale, …)
```

Props need a catalogue endpoint (`/prefabs/props`) the same way buildings have one.
`PropManager.CreateProp` will reject a placement that overlaps something, so the tool
must report the failure with the position and the reason.

**Parks are not props** — a park is a placeable building (`/prefabs/buildings` already
lists them), so parks and plazas are covered by `city_place_building` today.

The game's own prop tool adds *automatic* props (trees scattered by the game's rules)
via `PropManager.RefreshAutomaticProps()` **[V]** — that is the cheapest way to get
greenery, and it is one call.

### 4.8 Industry chains and cargo — Tier 1 + Tier 2

Industrial buildings are ordinary placeable prefabs, so the *buildings* are already
covered. What is missing is:

- **Specialised industry areas** — see §4.5; the area type needs confirming **[?]**.
- **Cargo routes** — a cargo transport line, i.e. §4.3 again with a cargo vehicle class.
  Cargo transport is worth **+1.0** to the industrial level-up score, five times police
  (`05-game-mechanics.md` §5), so this is a high-value target.
- **Production monitoring** — reading each industrial building's output and input
  (`BuildingManager` production data **[?]**) so the agent can see that a chain is
  starved.

### 4.9 Terrain and natural resources — Tier 0

`ICities.ITerrain` does the whole job **[V]**:

```
ITerrain.get_heightMapResolution / get_cellSize
ITerrain.PositionToHeightMapCoord(x, z, out heightX, out heightZ)
ITerrain.HeightMapCoordToPosition(heightX, heightZ, out x, out z)
ITerrain.GetHeights(heightX, heightZ, width, length, UInt16[] rawHeights)
ITerrain.SetHeights(heightX, heightZ, width, length, UInt16[] rawHeights)
ITerrain.RawToHeight(UInt16 rawHeight) / HeightToRaw(Single height)
ITerrain.SampleTerrainHeight(x, z) / SampleWaterHeight(x, z)
```

The recipe for raising a hill is: map world → height-map coordinates, read the raw
heights of the affected rectangle, modify them, write them back with `SetHeights`. The
`RawToHeight` / `HeightToRaw` pair removes the "what is the encoding?" question that
the first draft of this document listed as open.

The `ITerrainExtensionBase.OnAfterHeightsModified(minX, minZ, maxX, maxZ)` hook
**[V]** tells the mod when the game itself changed the terrain — useful for keeping a
cache honest, and for knowing that a save/load cycle happened.

Natural resources are Tier 0 too **[V]**:

```
IResource.get_gridResolution / get_cellSize
IResource.WorldToGridPosition(Vector3 pos, out x, out z)
IResource.GridToWorldPosition(x, z) → Vector3
IResource.GetResource(x, z, NaturalResource type) → Byte
IResource.SetResource(x, z, NaturalResource type, Byte value, Boolean refresh)
IResource.GetDirt() / SetDirt(int)
```

`NaturalResource` covers ore, oil, forest and fertility **[?]**. This is how the agent
finds where to put a mining industry — and how it reads the map instead of guessing.

### 4.10 Screenshot and camera — Tier 0

`ICities.ICamera` is a complete camera control surface **[V]**:

```
ICamera.SetPosition(x, y, z, dodgeObjects) / MoveCamera(x, y, z, relativeAngle)
ICamera.SetRotation(horizontal, vertical, tilt) / RotateCamera(...)
ICamera.SetCameraTarget(x, y, z, distance, verticalAngle, horizontalAngle)
ICamera.GetCameraPosition(out x, out y, out z) / GetCameraAngle(out h, out v, out tilt)
ICamera.SetFieldOfView(fov) / SetFocusDistance(d) / SetAperture(a)
ICamera.Dolly(startX, startY, startZ, endX, endY, endZ, duration)
ICamera.FadeIn(seconds) / FadeOut(seconds)
ICamera.SetInteractive(bool)
ICamera.WaitForNextFrame() / Wait(seconds) / StartRoutine(IEnumerator)
```

So framing a shot is a supported API call, not a reflection into `CameraController`'s
private `m_targetPosition` / `m_targetAngle` / `m_targetSize` fields **[V]** (those
exist too, but there is no reason to touch them now).

The capture itself is standard Unity — `Camera.Render()` → `RenderTexture` →
`ReadPixels` → `Texture2D.EncodeToPNG()` — and it must run on the **Unity main
thread**. This is the one sanctioned exception to the "simulation thread only" rule, and
it may not mutate city state. The plan is a separate, explicitly-named main-thread queue
in `SimulationDispatcher` so the exception is visible in the code rather than implied.

`ICamera.WaitForNextFrame()` and `Wait(seconds)` also make it possible to let the
camera settle before capturing, instead of guessing a delay.

Cost: a 1080p `ReadPixels` is a ~8 MB GPU→CPU sync. At most one capture every few
seconds, and only when the agent actually needs to look.

### 4.11 Saves — Tier 0

```
ISerializableData.SaveGame(String saveName) → Boolean
ISerializableData.LoadGame(String saveName) → Boolean
ISerializableData.EnumerateData() / LoadData(id) / SaveData(id, bytes) / EraseData(id)
```

**[V]** — loading a save is a *supported* call, not a UI automation problem. (The
first draft of this document rated it Tier 3; it is not.) `LoadGame` still needs care:
the mod's `LevelLoaded` flag and the dispatcher's pending jobs have to be invalidated
around it. The `ISerializableDataExtensionBase.OnLoadData` hook **[V]** is where that
invalidation belongs.

Renaming and duplicating a `.crp` are plain file operations.

### 4.12 Building placement — Tier 0 (alternative to the existing command)

```
IBuilding.BuildingTypes() → IEnumerable<String>
IBuilding.CreateBuilding(Vector3 position, Single angle, String buildingType) → UInt16
IBuilding.RelocateBuilding(UInt16 id, Vector3 position, Single angle)
IBuilding.ReleaseBuilding(UInt16 id)
```

**[V]** The bridge already places buildings through `BuildingManager` directly, which
gives finer control (dry runs, blocked-asset policy). `IBuilding` is a useful
cross-check and a fallback for prefabs the direct path refuses.

### 4.13 Game speed, pause and the simulation clock — Tier 0

```
IThreading.get_simulationPaused / set_simulationPaused
IThreading.get_simulationSpeed / set_simulationSpeed
IThreading.get_simulationTime / get_simulationTick / get_simulationFrame
IThreading.get_renderTime / get_renderFrame / get_renderDayTimeHour
IThreading.QueueMainThread() / QueueSimulationThread()
```

**[V]** The simulation clock matters for pacing: the agent can measure how much simulated
time elapsed between two reads and avoid drawing conclusions after zero sim-days.
`simulationTime` is a first-class supported read, which is better than parsing the date
out of the snapshot.

---

## 5. The cost model that shapes the schedule

Every change to `src/` requires: build the mod → install the DLL → **restart the game**
→ reload the city. That is minutes of downtime per bridge change.

Two consequences:

1. **Batch bridge work.** A feature that needs three endpoints should land in one
   bridge build, not three. The matrix rows are grouped into *bridge releases* below.
2. **New endpoints are instantly agent-reachable.** The MCP server ships a raw
   passthrough (`city_request`), so a bridge endpoint added today is callable by the
   agent today; the dedicated 1:1 tool only adds discoverability. No server-side gate
   sits between a new endpoint and the agent.

### Proposed bridge releases

| Release | Contents | Why together |
|---|---|---|
| **R0 — ICities** | Capture `IManagers` in `ThreadingExtensionBase.OnCreated`. Expose `/state/capabilities` (what the running game supports) and the simulation clock | Prerequisite for R4/R5; one tiny change, huge unlock |
| **R1 — budgets & policies** | `/state/budgets`, `set-budget`, `/state/policies`, `set-policy` | Both are trivial Tier 1 and unlock the economy levers in `05-game-mechanics.md` |
| **R3 — transport** | `/state/transport`, `/prefabs/transport`, `transport-line` | The biggest single quality unlock; needs its own verification budget |
| **R4 — districts & areas** | `/state/parks`, `district` (create / paint / specialise), `game-area` | One implementation covers four feature families (§4.4 + §4.5) |
| **R5 — terrain, resources, view, load** | `edit-terrain`, `/state/resources`, `render-view`, `load` | All Tier 0 — one implementation covers all four |
| **R6 — props, progress, industry** | `/prefabs/props`, `place-prop`, `/state/progress`, `unlock-*`, `/state/industry` | Cheap Tier 1 work, batched |
| **R7 — UI automation fallback (tier 3)** | Move the `game-ui.ps1` primitives into the MCP server; world→screen projection | The escape hatch for whatever is left |

---

## 6. Verification requirements

A parity claim is only true if the game state changed the way the player's action would
have changed it. Per row:

| Capability | Verification |
|---|---|
| Budgets | Read the budget back; confirm the vehicle count moved (`/state/facilities`). |
| Policies | Read the policy state back; confirm the demand/happiness delta over a simulated week. |
| Transport line | Read the line back: stops, vehicles, active state, and that it is `SetActive`. Then confirm ridership over a simulated week. |
| District / area | Read `m_districtGrid` back and confirm the cells carry the right district byte; confirm `GetDistrict(worldPos)` returns it. |
| Milestones | Read `GetCurrentMilestone()` and `Unlocked(Feature)` before and after; the unlock itself is an ordinary command (a cheat — see §4.6). |
| Props | Confirm the prop id exists and `PropManager` reports it. |
| Terrain | Compare `ITerrain.GetHeights` before/after; confirm `SampleTerrainHeight` changed where it should and nowhere else. |
| Resources | Confirm `IResource.GetResource` reports the new value. |
| Screenshot | Confirm a non-blank PNG of the expected size; the agent must look at it. |
| Save / load | Confirm the city state after the load matches the target save's summary. |

Every one of these follows the existing pattern in
[`07-verification.md`](07-verification.md): act → re-read → compare → expect.

---

## 7. Open questions

| Question | Notes |
|---|---|
| Does `PrefabCollection<TransportInfo>` enumerate the bus/metro assets? | Almost certainly — it is the same generic the road catalogue already uses — but the names and the default vehicle per line type are unverified. |
| How is the industry area represented? | The `ParkType` enum shows campus / airport / pedestrian but not industry. |
| Can the transport line be built by the agent *without* the UI? | Strategy A (§4.3) says yes. If `NetManager` refuses to attach a transport segment to an existing road node, strategy B becomes mandatory. |
| Does `ICities` expose a transport-line interface? | No `ICities.ITransport` exists **[V]** — transport is the one big family with no supported API, which is why it stays Tier 1 + Tier 3. |
| Does `LoadGame` work while the mod's dispatcher holds a pending job? | Must be answered before exposing `/commands/load`. The safest rule: refuse to load unless the dispatcher is idle. |
| Should the tier-3 UI automation live in the MCP server or a helper process? | The MCP server blocks during the call, which is fine for a 1 s action. A helper would avoid blocking but adds a process to supervise. **In the server, for now.** |
