# Game API notes

Which game APIs to use for parts of the game that have no tool yet. Each section links its
issue. **[V]** means verified against the game assemblies; **[?]** means not yet verified.

## Choosing a route

In order of preference:

1. **The game's own tool code.** If the player does it with a tool (roads, buildings,
   zoning, districts), call that tool's code: `NetTool.CreateNode`, the `BuildingTool` rules,
   `DistrictTool.ApplyBrush`. You get the player's validation for free.
2. **`ICities`, the supported modding API.** It is patch-stable. The bridge already holds
   `IManagers` (captured in `AgentBridgeThreading.OnCreated`).
3. **Public `Assembly-CSharp` methods**, the ones the game itself calls, on the simulation
   thread.
4. **Reflection into private state**, only when nothing else exists. It breaks when a patch
   renames a field, so say so in the code.

Decompile with `ilspycmd -t <Type> "<game>\Cities_Data\Managed\Assembly-CSharp.dll"` to read
how the game itself does something before copying it.

```
IManagers ─┬─ areas            IAreas             map tiles
           ├─ terrain          ITerrain           heights, water height
           ├─ resource         IResource          ore / oil / forest / fertility
           ├─ serializableData ISerializableData  save and load games
           ├─ milestones       IMilestones        milestone read (unlocking is a cheat)
           ├─ camera           ICamera            the cinematic camera, not the player's view
           ├─ building         IBuilding          place / relocate / release
           ├─ threading        IThreading         speed, pause, ticks, thread queues
           ├─ economy          IEconomy           money
           └─ application      IApplication       version, expansion support
```

## Loading saves and starting games: [#69](https://github.com/MinhChien9/cities-skylines-mayor/issues/69)

```
ISerializableData.LoadGame(string saveName) → bool          [V]
ISerializableData.SaveGame(string saveName) → bool          [V]
```

Loading is a supported call. Around it, the bridge must reset its `LevelLoaded` flag and drop
pending dispatcher jobs; `SerializableDataExtensionBase.OnLoadData` is the hook for that.
Starting a new game from a map or scenario goes through `LoadingManager` (see
`NewScenarioGamePanel` in the game code) and has to work from the main menu, before a level
exists.

## Terrain and natural resources: [#72](https://github.com/MinhChien9/cities-skylines-mayor/issues/72)

```
IResource.WorldToGridPosition(Vector3, out x, out z)        [V]
IResource.GetResource(x, z, NaturalResource) → byte         [V]   ore, oil, forest, fertility [?]
ITerrain.SampleTerrainHeight(x, z) / SampleWaterHeight(x, z) [V]
ITerrain.GetHeights / SetHeights + RawToHeight / HeightToRaw [V]
```

Return resources as a grid like `/state/map` does, plus a `city_map` overlay. Editing the
terrain is out of scope (it belongs to the player's landscaping tool).

## Service coverage: [#73](https://github.com/MinhChien9/cities-skylines-mayor/issues/73)

The info views read `ImmaterialResourceManager`: `CheckResource(resource, position, out local,
out total)` for police, fire, health care, education, entertainment and public transport.
These terms drive citizen wellbeing (`ResidentAI.UpdateWellbeing`; see
[game-mechanics.md](game-mechanics.md)). Sample them per map cell the way `MapOverlay` samples
land value.

## Industry chains and cargo: [#74](https://github.com/MinhChien9/cities-skylines-mayor/issues/74), [#75](https://github.com/MinhChien9/cities-skylines-mayor/issues/75)

- **Cargo routes** are transport lines with a cargo vehicle class. Cargo counts five times
  more than police in the industrial level-up score.
- **Production:** read each industrial building's production rate and its input and output
  buffers (`BuildingManager` data **[?]**), so a starved chain (`NoGoods`) shows before shops
  are abandoned.

## Special areas (airport, campus, pedestrian, industry)

Special areas are districts in a separate id space **[V]**:

```
DistrictManager.CreatePark(out byte park, ParkType type, ParkLevel level)
DistrictManager.AddParkBuilding(byte park, BuildingInfo info, ParkType type)
```

`/commands/district` with `layer: "park"` already creates and paints them. A new area is
`functional: false` until its main gate building is placed. Exposing create and paint in the
MCP tool is [#68](https://github.com/MinhChien9/cities-skylines-mayor/issues/68).

## Props and landscaping

```
PropManager.CreateProp(out ushort prop, ref Randomizer r, PropInfo info, Vector3 pos, float angle, bool single)  [V]
PropManager.ReleaseProp(ushort) / MoveProp(ushort, Vector3)                                                    [V]
```

This needs a `/prefabs/props` catalogue. Parks are buildings, not props: `city_place_building`
already covers them.

## Milestones and map tiles

Reading milestones is fine (`city_scenario.currentMilestone`, `city_capabilities`). **Unlocking**
milestones or tiles for free is a cheat. Buying a tile goes through the area tool's pricing
(`city_unlock_area`).
