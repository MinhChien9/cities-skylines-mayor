# Game API notes

Which game APIs to use for parts of the game that have no tool yet. **[V]** means verified
against the game assemblies; **[?]** means not yet verified.

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
