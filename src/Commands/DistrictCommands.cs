using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ColossalFramework;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// GET /state/districts and POST /commands/district.
    ///
    /// Districts and the special areas (park, amusement park, zoo, nature reserve, industry,
    /// farming, forestry, ore, oil, campus, airport, pedestrian zone) are two id spaces over
    /// the same mechanism: a 512 x 512 grid of <c>DistrictManager.Cell</c> values, 19.2 m per
    /// side, where each cell holds up to four (id, alpha) pairs so the game can blend district
    /// edges. Districts live in <c>m_districts</c> / <c>m_districtGrid</c>; special areas live
    /// in <c>m_parks</c> / <c>m_parkGrid</c> and are told apart by <c>DistrictPark.ParkType</c>.
    ///
    /// Every call used here was read out of the live game's Assembly-CSharp.dll by
    /// decompilation (2026-11-11):
    ///   DistrictManager.CreateDistrict(out Byte) / CreatePark(out Byte, ParkType, ParkLevel)
    ///   DistrictManager.SetDistrictName / SetParkName      // one-step IEnumerator coroutines
    ///   DistrictManager.GetDistrictName / GetParkName / GetDistrict(Vector3) / GetPark(Vector3)
    ///   DistrictManager.ReleaseDistrict / ReleasePark
    ///   DistrictManager.ModifyCell(x, z, Cell)              // the only public district write
    ///   DistrictManager.AreaModified / ParksAreaModified
    ///   DistrictTool.ApplyBrush(layer, id, radius, start, end)  // the game's own brush
    ///   DistrictManager.SetDistrictPolicy / UnsetDistrictPolicy / IsDistrictPolicySet
    ///
    /// Painting deliberately calls <c>DistrictTool.ApplyBrush</c>, the same public static helper
    /// the district tool calls while the player drags the brush. That reuses the game's own
    /// rasterisation, edge blending, neighbour smoothing and texture invalidation instead of
    /// re-implementing the 4-slot alpha model here.
    /// </summary>
    public static class DistrictCommands
    {
        /// <summary>The district grid is 512 x 512 cells of 19.2 m (DISTRICTGRID_*).</summary>
        private const int GridResolution = 512;

        /// <summary>DISTRICTGRID_CELL_SIZE — 19.2 m per cell, 368.64 m2.</summary>
        private const float GridCellSize = 19.2f;

        /// <summary>World coordinate of grid cell 0. The grid is centred on the origin.</summary>
        private const float GridOrigin = -GridResolution * 0.5f * GridCellSize;

        /// <summary>The brush fades its last 2 cells (2 x 19.2 m) on each side.</summary>
        private const float BrushFadeMeters = GridCellSize * 2f;

        private const float MaxBrushRadius = 1024f;

        /// <summary>DistrictPolicies.Policies values 0..12 are the specializations.</summary>
        private const int SpecializationMaxValue = 12;

        /// <summary>Both m_districts and m_parks are Array8 buffers of 128 entries.</summary>
        private const int MaxEntities = 128;

        // ------------------------------------------------------------------ reads

        public static CommandResult BuildDistrictsJson()
        {
            DistrictManager manager = Singleton<DistrictManager>.instance;

            GridScan districtScan = new GridScan();
            GridScan parkScan = new GridScan();
            ScanGrid(manager.m_districtGrid, districtScan);
            ScanGrid(manager.m_parkGrid, parkScan);

            District[] districts = manager.m_districts.m_buffer;
            StringBuilder districtItems = new StringBuilder();
            int districtCount = 0;
            for (int id = 1; id < districts.Length && id < MaxEntities; id++)
            {
                if (!IsCreatedDistrict(manager, id))
                {
                    continue;
                }
                if (districtCount > 0)
                {
                    districtItems.Append(",");
                }
                AppendDistrict(districtItems, manager, id, districts[id], districtScan);
                districtCount++;
            }

            DistrictPark[] parks = manager.m_parks.m_buffer;
            StringBuilder parkItems = new StringBuilder();
            int parkCount = 0;
            for (int id = 1; id < parks.Length && id < MaxEntities; id++)
            {
                if (!IsCreatedPark(manager, id))
                {
                    continue;
                }
                if (parkCount > 0)
                {
                    parkItems.Append(",");
                }
                AppendPark(parkItems, manager, id, parks[id], parkScan);
                parkCount++;
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            AppendGridInfo(json);
            json.Append(",\"districtCount\":").Append(districtCount);
            json.Append(",\"paintedDistrictCells\":").Append(districtScan.PaintedCells);
            json.Append(",\"districts\":[").Append(districtItems.ToString()).Append("]");
            json.Append(",\"parkCount\":").Append(parkCount);
            json.Append(",\"paintedParkCells\":").Append(parkScan.PaintedCells);
            json.Append(",\"parks\":[").Append(parkItems.ToString()).Append("]");
            json.Append(",\"notes\":\"One cell is 19.2 m square (368.64 m2). Districts and special areas are separate id spaces. areaCells is the game's own m_totalAlpha / 255, where one fully painted cell counts as 1.\"");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        // --------------------------------------------------------------- commands

        public static CommandResult DistrictCommand(string body)
        {
            string action = JsonUtil.GetString(body, "action", "state").Trim().ToLowerInvariant();
            string layerText = JsonUtil.GetString(body, "layer", "").Trim();
            string layerName = NormalizeLayer(layerText);
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);

            if (layerName.Length == 0 && layerText.Length > 0)
            {
                return CommandResult.Fail("Unknown layer: " + layerText + ". Use \"district\" or \"park\".");
            }

            // Infer the layer when it is not given: a parkType or a park id means an area.
            if (layerName.Length == 0)
            {
                bool looksLikePark = JsonUtil.GetString(body, "parkType", "").Trim().Length > 0 ||
                    (HasKey(body, "park") && !HasKey(body, "district"));
                layerName = looksLikePark ? "park" : "district";
            }

            int id = ReadId(body, layerName);

            if (action == "state")
            {
                if (id == 0 && !HasKey(body, "id") && !HasKey(body, "district") && !HasKey(body, "park"))
                {
                    return BuildDistrictsJson();
                }
                return State(layerName, id);
            }
            if (action == "create")
            {
                return Create(layerName, body, dryRun);
            }
            if (action == "rename")
            {
                return Rename(layerName, id, body, dryRun);
            }
            if (action == "paint" || action == "erase")
            {
                int paintId = action == "erase" ? 0 : id;
                return Paint(layerName, paintId, body, dryRun);
            }
            if (action == "specialize")
            {
                return Specialize(layerName, id, body, dryRun);
            }
            if (action == "release")
            {
                return Release(layerName, id, dryRun);
            }

            return CommandResult.Fail("Unknown action: " + action +
                ". Supported: create, rename, paint, erase, specialize, release, state.");
        }

        private static CommandResult Create(string layerName, string body, bool dryRun)
        {
            string name = JsonUtil.GetString(body, "name", "").Trim();
            DistrictManager manager = Singleton<DistrictManager>.instance;

            if (layerName == "park")
            {
                string typeText = JsonUtil.GetString(body, "parkType", "").Trim();
                DistrictPark.ParkType type;
                if (!TryParseParkType(typeText, out type) || type == DistrictPark.ParkType.None)
                {
                    return CommandResult.Fail("parkType is required for a special area. Use one of: " +
                        DescribeParkTypes() + ".");
                }

                DistrictPark.ParkLevel level = ParseParkLevel(JsonUtil.GetString(body, "parkLevel", "Level1"));

                if (dryRun)
                {
                    StringBuilder dry = new StringBuilder();
                    dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"create\",\"layer\":\"park\"");
                    dry.Append(",\"parkType\":\"").Append(type.ToString()).Append("\"");
                    dry.Append(",\"parkLevel\":\"").Append(level.ToString()).Append("\"");
                    dry.Append(",\"name\":").Append(name.Length == 0 ? "null" : "\"" + JsonUtil.Escape(name) + "\"");
                    dry.Append(",\"message\":\"Create-area validation passed; no area was created. Areas are not functional until they have a main gate building.\"");
                    dry.Append("}");
                    return CommandResult.FromJson(dry.ToString());
                }

                byte park;
                if (!manager.CreatePark(out park, type, level))
                {
                    return CommandResult.Fail("CreatePark was refused by the game (the usual cause is the 127-area limit).");
                }

                bool named = false;
                if (name.Length > 0)
                {
                    named = ApplyParkName(manager, park, name);
                }

                Debug.Log("[SkylinesAgentBridge] Created special area " + park + " (" + type + ")");

                StringBuilder json = new StringBuilder();
                json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"create\",\"layer\":\"park\"");
                json.Append(",\"park\":").Append(park);
                json.Append(",\"parkType\":\"").Append(type.ToString()).Append("\"");
                json.Append(",\"parkLevel\":\"").Append(level.ToString()).Append("\"");
                json.Append(",\"named\":").Append(JsonUtil.Bool(named));
                json.Append(",\"state\":");
                AppendPark(json, manager, park, manager.m_parks.m_buffer[park], null);
                json.Append("}");
                return CommandResult.FromJson(json.ToString());
            }

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"create\",\"layer\":\"district\"");
                dry.Append(",\"name\":").Append(name.Length == 0 ? "null" : "\"" + JsonUtil.Escape(name) + "\"");
                dry.Append(",\"message\":\"Create-district validation passed; no district was created.\"");
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            byte district;
            if (!manager.CreateDistrict(out district))
            {
                return CommandResult.Fail("CreateDistrict was refused by the game (the 128-district limit).");
            }

            bool districtNamed = false;
            if (name.Length > 0)
            {
                districtNamed = ApplyDistrictName(manager, district, name);
            }

            Debug.Log("[SkylinesAgentBridge] Created district " + district);

            StringBuilder result = new StringBuilder();
            result.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"create\",\"layer\":\"district\"");
            result.Append(",\"district\":").Append(district);
            result.Append(",\"named\":").Append(JsonUtil.Bool(districtNamed));
            result.Append(",\"state\":");
            AppendDistrict(result, manager, district, manager.m_districts.m_buffer[district], null);
            result.Append("}");
            return CommandResult.FromJson(result.ToString());
        }

        private static CommandResult Rename(string layerName, int id, string body, bool dryRun)
        {
            string name = JsonUtil.GetString(body, "name", "").Trim();
            DistrictManager manager = Singleton<DistrictManager>.instance;

            if (id <= 0 || id >= MaxEntities || !Exists(manager, layerName, id))
            {
                return CommandResult.Fail(DescribeMissing(layerName, id) +
                    " Create one first (action \"create\") or read GET /state/districts.");
            }

            if (layerName == "park")
            {
                string before = SafeParkName(manager, id);
                if (dryRun)
                {
                    return NameResult("rename", "park", id, before, name, dryRun, false);
                }
                bool applied = ApplyParkName(manager, (byte)id, name);
                return NameResult("rename", "park", id, before, name, dryRun, applied);
            }

            string districtBefore = SafeDistrictName(manager, id);
            if (dryRun)
            {
                return NameResult("rename", "district", id, districtBefore, name, dryRun, false);
            }
            bool districtApplied = ApplyDistrictName(manager, (byte)id, name);
            return NameResult("rename", "district", id, districtBefore, name, dryRun, districtApplied);
        }

        private static CommandResult Paint(string layerName, int id, string body, bool dryRun)
        {
            Vector3 center = CommandInput.ReadPoint(body, "center");
            float radius = JsonUtil.GetNumber(body, "radius", 64f);
            DistrictManager manager = Singleton<DistrictManager>.instance;
            bool park = layerName == "park";

            if (radius <= BrushFadeMeters || radius > MaxBrushRadius)
            {
                return CommandResult.Fail("radius must be greater than " + JsonUtil.Number(BrushFadeMeters) +
                    " and at most " + JsonUtil.Number(MaxBrushRadius) +
                    ". The brush paints at full strength inside radius - " + JsonUtil.Number(BrushFadeMeters) +
                    " m and fades to nothing at radius (the game's own brush).");
            }
            if (id < 0 || id >= MaxEntities)
            {
                return CommandResult.Fail("id must be 0..127 (0 = no district / erase).");
            }
            if (id != 0 && !Exists(manager, layerName, id))
            {
                return CommandResult.Fail(DescribeMissing(layerName, id) +
                    " Painting needs an existing " + (park ? "area" : "district") + ".");
            }

            float areaBefore = id == 0 ? 0f : AreaCells(manager, layerName, id);
            int cellsInBrushBefore = CountCellsInBrush(manager, park, id, center, radius);

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"paint\"");
                dry.Append(",\"layer\":\"").Append(park ? "park" : "district").Append("\"");
                dry.Append(",\"id\":").Append(id);
                AppendPoint(dry, "center", center);
                dry.Append(",\"radius\":").Append(JsonUtil.Number(radius));
                dry.Append(",\"areaCellsBefore\":").Append(JsonUtil.Number(areaBefore));
                dry.Append(",\"cellsInBrushBefore\":").Append(cellsInBrushBefore);
                dry.Append(",\"message\":\"Brush validation passed; nothing was painted.\"");
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            DistrictTool.ApplyBrush(
                park ? DistrictTool.Layer.Parks : DistrictTool.Layer.Districts,
                (byte)id,
                radius,
                center,
                center);

            float areaAfter = id == 0 ? 0f : AreaCells(manager, layerName, id);
            int cellsInBrushAfter = CountCellsInBrush(manager, park, id, center, radius);

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"paint\"");
            json.Append(",\"layer\":\"").Append(park ? "park" : "district").Append("\"");
            json.Append(",\"id\":").Append(id);
            AppendPoint(json, "center", center);
            json.Append(",\"radius\":").Append(JsonUtil.Number(radius));
            json.Append(",\"areaCells\":{\"before\":").Append(JsonUtil.Number(areaBefore));
            json.Append(",\"after\":").Append(JsonUtil.Number(areaAfter)).Append("}");
            json.Append(",\"cellsInBrush\":{\"before\":").Append(cellsInBrushBefore);
            json.Append(",\"after\":").Append(cellsInBrushAfter).Append("}");
            json.Append(",\"atCenter\":{\"district\":").Append(manager.GetDistrict(center));
            json.Append(",\"park\":").Append(manager.GetPark(center));
            json.Append(",\"sampleMask\":").Append(park ? manager.SamplePark(center) : manager.SampleDistrict(center));
            json.Append("}}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult Specialize(string layerName, int id, string body, bool dryRun)
        {
            if (layerName != "district")
            {
                return CommandResult.Fail("specialize applies to districts, not special areas. " +
                    "A special area carries its specialization in parkType (e.g. Industry, Farming, Airport).");
            }

            DistrictManager manager = Singleton<DistrictManager>.instance;
            if (id <= 0 || !Exists(manager, layerName, id))
            {
                return CommandResult.Fail(DescribeMissing(layerName, id) + " Specializations need an existing district.");
            }

            string text = JsonUtil.GetString(body, "specialization", "").Trim();
            DistrictPolicies.Policies policy;
            if (!TryParsePolicy(text, out policy))
            {
                return CommandResult.Fail("Unknown specialization: " + text + ". Use one of: " +
                    DescribeSpecializations() + ".");
            }
            if ((int)policy < 0 || (int)policy > SpecializationMaxValue)
            {
                return CommandResult.Fail("\"" + text + "\" is a policy, not a specialization. Use one of: " +
                    DescribeSpecializations() + " (set policies with POST /commands/set-policy).");
            }

            bool set = JsonUtil.GetBool(body, "set", true);
            string before = SpecializationList(manager.m_districts.m_buffer[id]);

            if (!dryRun)
            {
                if (set)
                {
                    // District.SetPolicy clears the competing specializations of the same
                    // family itself (resource / commercial / residential / office groups), so
                    // one call is enough — this is the same path the district panel uses.
                    manager.SetDistrictPolicy(policy, (byte)id);
                }
                else
                {
                    manager.UnsetDistrictPolicy(policy, (byte)id);
                }
            }

            string after = SpecializationList(manager.m_districts.m_buffer[id]);

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":").Append(JsonUtil.Bool(dryRun));
            json.Append(",\"action\":\"specialize\",\"layer\":\"district\"");
            json.Append(",\"district\":").Append(id);
            json.Append(",\"specialization\":\"").Append(policy.ToString()).Append("\"");
            json.Append(",\"set\":").Append(JsonUtil.Bool(set));
            json.Append(",\"before\":[").Append(before).Append("]");
            json.Append(",\"after\":[").Append(after).Append("]");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult Release(string layerName, int id, bool dryRun)
        {
            DistrictManager manager = Singleton<DistrictManager>.instance;

            if (id <= 0 || id >= MaxEntities || !Exists(manager, layerName, id))
            {
                return CommandResult.Fail(DescribeMissing(layerName, id) + " Nothing to release.");
            }

            bool park = layerName == "park";
            int paintedBefore = CountPaintedCells(park ? manager.m_parkGrid : manager.m_districtGrid);

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"release\"");
                dry.Append(",\"layer\":\"").Append(park ? "park" : "district").Append("\"");
                dry.Append(",\"id\":").Append(id);
                dry.Append(",\"paintedCellsBefore\":").Append(paintedBefore);
                dry.Append(",\"message\":\"Release validation passed; nothing was released.\"");
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            int clearedCells;
            string name;
            if (park)
            {
                name = SafeParkName(manager, id);
                clearedCells = ClearParkCells(manager, (byte)id);
                manager.ReleasePark((byte)id);
            }
            else
            {
                name = SafeDistrictName(manager, id);
                clearedCells = ClearDistrictCells(manager, (byte)id);
                manager.ReleaseDistrict((byte)id);
            }

            int paintedAfter = CountPaintedCells(park ? manager.m_parkGrid : manager.m_districtGrid);

            Debug.Log("[SkylinesAgentBridge] Released " + layerName + " " + id + " (" + clearedCells + " grid cells cleared)");

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"release\"");
            json.Append(",\"layer\":\"").Append(park ? "park" : "district").Append("\"");
            json.Append(",\"id\":").Append(id);
            json.Append(",\"name\":").Append(name == null ? "null" : "\"" + JsonUtil.Escape(name) + "\"");
            json.Append(",\"clearedCells\":").Append(clearedCells);
            json.Append(",\"paintedCells\":{\"before\":").Append(paintedBefore);
            json.Append(",\"after\":").Append(paintedAfter).Append("}");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult State(string layerName, int id)
        {
            DistrictManager manager = Singleton<DistrictManager>.instance;
            if (id <= 0 || id >= MaxEntities || !Exists(manager, layerName, id))
            {
                return CommandResult.Fail(DescribeMissing(layerName, id) +
                    " Read GET /state/districts for the full list.");
            }

            GridScan scan = new GridScan();
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"action\":\"state\"");
            json.Append(",\"layer\":\"").Append(layerName).Append("\"");
            json.Append(",\"id\":").Append(id);
            if (layerName == "park")
            {
                ScanGrid(manager.m_parkGrid, scan);
                json.Append(",\"state\":");
                AppendPark(json, manager, id, manager.m_parks.m_buffer[id], scan);
            }
            else
            {
                ScanGrid(manager.m_districtGrid, scan);
                json.Append(",\"state\":");
                AppendDistrict(json, manager, id, manager.m_districts.m_buffer[id], scan);
            }
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// Scans one 512 x 512 grid once: how many cells carry a district/area, and the cell
        /// bounds of every id. One pass keeps the read endpoint cheap even with 128 entities.
        /// </summary>
        private static void ScanGrid(DistrictManager.Cell[] grid, GridScan scan)
        {
            for (int z = 0; z < GridResolution; z++)
            {
                int row = z * GridResolution;
                for (int x = 0; x < GridResolution; x++)
                {
                    DistrictManager.Cell cell = grid[row + x];
                    bool painted = false;
                    AddEntry(scan, cell.m_district1, cell.m_alpha1, x, z, ref painted);
                    AddEntry(scan, cell.m_district2, cell.m_alpha2, x, z, ref painted);
                    AddEntry(scan, cell.m_district3, cell.m_alpha3, x, z, ref painted);
                    AddEntry(scan, cell.m_district4, cell.m_alpha4, x, z, ref painted);
                    if (painted)
                    {
                        scan.PaintedCells++;
                    }
                }
            }
        }

        private static void AddEntry(GridScan scan, byte id, byte alpha, int x, int z, ref bool painted)
        {
            if (alpha == 0)
            {
                return;
            }
            if (id != 0)
            {
                painted = true;
            }
            if (id >= MaxEntities)
            {
                return;
            }
            if (x < scan.MinX[id]) { scan.MinX[id] = x; }
            if (x > scan.MaxX[id]) { scan.MaxX[id] = x; }
            if (z < scan.MinZ[id]) { scan.MinZ[id] = z; }
            if (z > scan.MaxZ[id]) { scan.MaxZ[id] = z; }
        }

        private static void AppendDistrict(StringBuilder json, DistrictManager manager, int id,
            District district, GridScan scan)
        {
            json.Append("{\"id\":").Append(id);
            json.Append(",\"name\":").Append(NullableString(SafeDistrictName(manager, id)));
            json.Append(",\"customName\":").Append(JsonUtil.Bool((district.m_flags & District.Flags.CustomName) != 0));
            json.Append(",\"areaCells\":").Append(JsonUtil.Number(district.m_totalAlpha / 255f));
            json.Append(",\"population\":").Append(district.m_populationData.m_finalCount);
            json.Append(",\"happiness\":").Append(district.m_finalHappiness);
            json.Append(",\"specializations\":[").Append(SpecializationList(district)).Append("]");
            AppendExtent(json, scan, id);
            json.Append("}");
        }

        private static void AppendPark(StringBuilder json, DistrictManager manager, int id,
            DistrictPark park, GridScan scan)
        {
            json.Append("{\"id\":").Append(id);
            json.Append(",\"name\":").Append(NullableString(SafeParkName(manager, id)));
            json.Append(",\"customName\":").Append(JsonUtil.Bool((park.m_flags & DistrictPark.Flags.CustomName) != 0));
            json.Append(",\"parkType\":\"").Append(park.m_parkType.ToString()).Append("\"");
            json.Append(",\"parkLevel\":\"").Append(park.m_parkLevel.ToString()).Append("\"");
            json.Append(",\"parkGroup\":\"").Append(park.Group.ToString()).Append("\"");
            json.Append(",\"isPark\":").Append(JsonUtil.Bool(park.IsPark));
            json.Append(",\"isIndustry\":").Append(JsonUtil.Bool(park.IsIndustry));
            json.Append(",\"isCampus\":").Append(JsonUtil.Bool(park.IsCampus));
            json.Append(",\"isAirport\":").Append(JsonUtil.Bool(park.IsAirport));
            json.Append(",\"isPedestrianZone\":").Append(JsonUtil.Bool(park.IsPedestrianZone));
            json.Append(",\"mainGate\":").Append(park.m_mainGate);
            json.Append(",\"functional\":").Append(JsonUtil.Bool(park.m_mainGate != 0));
            json.Append(",\"areaCells\":").Append(JsonUtil.Number(park.m_totalAlpha / 255f));
            json.Append(",\"sizeInCells\":").Append(JsonUtil.Number(park.sizeInCells));
            json.Append(",\"areaFactor\":").Append(park.m_areaFactor);
            AppendExtent(json, scan, id);
            json.Append("}");
        }

        private static void AppendGridInfo(StringBuilder json)
        {
            json.Append(",\"grid\":{\"resolution\":").Append(GridResolution);
            json.Append(",\"cellSizeMeters\":").Append(JsonUtil.Number(GridCellSize));
            json.Append(",\"cellAreaSquareMeters\":").Append(JsonUtil.Number(GridCellSize * GridCellSize));
            json.Append(",\"originMeters\":").Append(JsonUtil.Number(GridOrigin));
            json.Append(",\"cellCount\":").Append(GridResolution * GridResolution);
            json.Append(",\"brushFadeMeters\":").Append(JsonUtil.Number(BrushFadeMeters)).Append("}");
        }

        private static void AppendExtent(StringBuilder json, GridScan scan, int id)
        {
            if (scan == null || scan.MaxX[id] < 0)
            {
                json.Append(",\"extentCells\":null,\"extent\":null");
                return;
            }

            json.Append(",\"extentCells\":{\"minX\":").Append(scan.MinX[id]);
            json.Append(",\"minZ\":").Append(scan.MinZ[id]);
            json.Append(",\"maxX\":").Append(scan.MaxX[id]);
            json.Append(",\"maxZ\":").Append(scan.MaxZ[id]).Append("}");

            // Cell x spans [x, x+1) in cell space; the grid is centred on the world origin.
            json.Append(",\"extent\":{\"minX\":").Append(JsonUtil.Number((scan.MinX[id] - GridResolution * 0.5f) * GridCellSize));
            json.Append(",\"minZ\":").Append(JsonUtil.Number((scan.MinZ[id] - GridResolution * 0.5f) * GridCellSize));
            json.Append(",\"maxX\":").Append(JsonUtil.Number((scan.MaxX[id] + 1 - GridResolution * 0.5f) * GridCellSize));
            json.Append(",\"maxZ\":").Append(JsonUtil.Number((scan.MaxZ[id] + 1 - GridResolution * 0.5f) * GridCellSize)).Append("}");
            json.Append(",\"center\":{\"x\":").Append(JsonUtil.Number(((scan.MinX[id] + scan.MaxX[id] + 1) * 0.5f - GridResolution * 0.5f) * GridCellSize));
            json.Append(",\"z\":").Append(JsonUtil.Number(((scan.MinZ[id] + scan.MaxZ[id] + 1) * 0.5f - GridResolution * 0.5f) * GridCellSize)).Append("}");
        }

        private static void AppendPoint(StringBuilder json, string name, Vector3 point)
        {
            json.Append(",\"").Append(name).Append("\":{\"x\":").Append(JsonUtil.Number(point.x));
            json.Append(",\"y\":").Append(JsonUtil.Number(point.y));
            json.Append(",\"z\":").Append(JsonUtil.Number(point.z)).Append("}");
        }

        private static string SpecializationList(District district)
        {
            StringBuilder list = new StringBuilder();
            foreach (DistrictPolicies.Policies policy in Enum.GetValues(typeof(DistrictPolicies.Policies)))
            {
                int value = (int)policy;
                if (value < 0 || value > SpecializationMaxValue || policy == DistrictPolicies.Policies.None)
                {
                    continue;
                }
                if (((int)district.m_specializationPolicies & (1 << value)) == 0)
                {
                    continue;
                }
                if (list.Length > 0)
                {
                    list.Append(",");
                }
                list.Append("\"").Append(policy.ToString()).Append("\"");
            }
            return list.ToString();
        }

        /// <summary>Counts the cells in the square that the brush's bounding box covers.</summary>
        private static int CountPaintedCells(DistrictManager.Cell[] grid)
        {
            int count = 0;
            for (int i = 0; i < grid.Length; i++)
            {
                DistrictManager.Cell cell = grid[i];
                if ((cell.m_alpha1 != 0 && cell.m_district1 != 0) ||
                    (cell.m_alpha2 != 0 && cell.m_district2 != 0) ||
                    (cell.m_alpha3 != 0 && cell.m_district3 != 0) ||
                    (cell.m_alpha4 != 0 && cell.m_district4 != 0))
                {
                    count++;
                }
            }
            return count;
        }

        /// <summary>
        /// How many cells in the brush's bounding box currently belong to <paramref name="id"/>
        /// (its dominant entry). 0 on the erase path counts cells that carry any district.
        /// </summary>
        private static int CountCellsInBrush(DistrictManager manager, bool park, int id, Vector3 center, float radius)
        {
            int minX;
            int minZ;
            int maxX;
            int maxZ;
            GetBrushBounds(center, radius, out minX, out minZ, out maxX, out maxZ);

            DistrictManager.Cell[] grid = park ? manager.m_parkGrid : manager.m_districtGrid;
            int count = 0;
            for (int z = minZ; z <= maxZ; z++)
            {
                int row = z * GridResolution;
                for (int x = minX; x <= maxX; x++)
                {
                    DistrictManager.Cell cell = grid[row + x];
                    if (id != 0)
                    {
                        if (cell.m_alpha1 != 0 && cell.m_district1 == id)
                        {
                            count++;
                        }
                    }
                    else if ((cell.m_alpha1 != 0 && cell.m_district1 != 0) ||
                        (cell.m_alpha2 != 0 && cell.m_district2 != 0) ||
                        (cell.m_alpha3 != 0 && cell.m_district3 != 0) ||
                        (cell.m_alpha4 != 0 && cell.m_district4 != 0))
                    {
                        count++;
                    }
                }
            }
            return count;
        }

        private static void GetBrushBounds(Vector3 center, float radius, out int minX, out int minZ, out int maxX, out int maxZ)
        {
            minX = Mathf.Clamp((int)((center.x - radius) / GridCellSize + GridResolution * 0.5f), 0, GridResolution - 1);
            minZ = Mathf.Clamp((int)((center.z - radius) / GridCellSize + GridResolution * 0.5f), 0, GridResolution - 1);
            maxX = Mathf.Clamp((int)((center.x + radius) / GridCellSize + GridResolution * 0.5f), 0, GridResolution - 1);
            maxZ = Mathf.Clamp((int)((center.z + radius) / GridCellSize + GridResolution * 0.5f), 0, GridResolution - 1);
        }

        /// <summary>
        /// Removes one district from every cell that references it, compacting the remaining
        /// 1-4 (id, alpha) entries so the game's blending stays consistent, then invalidates the
        /// changed rectangle. ReleaseDistrict itself does not touch the grid.
        /// </summary>
        private static int ClearDistrictCells(DistrictManager manager, byte id)
        {
            DistrictManager.Cell[] grid = manager.m_districtGrid;
            int cleared = 0;
            int minX = GridResolution;
            int minZ = GridResolution;
            int maxX = -1;
            int maxZ = -1;

            for (int z = 0; z < GridResolution; z++)
            {
                int row = z * GridResolution;
                for (int x = 0; x < GridResolution; x++)
                {
                    int index = row + x;
                    DistrictManager.Cell cell = grid[index];
                    if (!References(cell, id))
                    {
                        continue;
                    }

                    DistrictManager.Cell compacted = CompactCell(cell, id);
                    // ModifyCell keeps District.m_totalAlpha consistent with the grid.
                    manager.ModifyCell(x, z, compacted);
                    cleared++;
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (z < minZ) { minZ = z; }
                    if (z > maxZ) { maxZ = z; }
                }
            }

            if (maxX >= 0)
            {
                manager.AreaModified(minX, minZ, maxX, maxZ, false);
            }
            return cleared;
        }

        private static int ClearParkCells(DistrictManager manager, byte id)
        {
            DistrictManager.Cell[] grid = manager.m_parkGrid;
            int cleared = 0;
            int minX = GridResolution;
            int minZ = GridResolution;
            int maxX = -1;
            int maxZ = -1;

            for (int z = 0; z < GridResolution; z++)
            {
                int row = z * GridResolution;
                for (int x = 0; x < GridResolution; x++)
                {
                    int index = row + x;
                    DistrictManager.Cell cell = grid[index];
                    if (!References(cell, id))
                    {
                        continue;
                    }

                    // The park grid's gate-weighted blending lives in ModifyParkCell; for a
                    // release the park is going away, so the compacted cell is written directly.
                    grid[index] = CompactCell(cell, id);
                    cleared++;
                    if (x < minX) { minX = x; }
                    if (x > maxX) { maxX = x; }
                    if (z < minZ) { minZ = z; }
                    if (z > maxZ) { maxZ = z; }
                }
            }

            if (maxX >= 0)
            {
                manager.ParksAreaModified(minX, minZ, maxX, maxZ, false);
            }
            return cleared;
        }

        private static bool References(DistrictManager.Cell cell, byte id)
        {
            return (cell.m_alpha1 != 0 && cell.m_district1 == id) ||
                (cell.m_alpha2 != 0 && cell.m_district2 == id) ||
                (cell.m_alpha3 != 0 && cell.m_district3 == id) ||
                (cell.m_alpha4 != 0 && cell.m_district4 == id);
        }

        /// <summary>
        /// Drops every (id, alpha) pair that names <paramref name="id"/> and keeps the rest,
        /// sorted so the largest alpha sits in slot 1 (the slot GetDistrict reads).
        /// </summary>
        private static DistrictManager.Cell CompactCell(DistrictManager.Cell cell, byte id)
        {
            byte[] ids = new byte[4];
            int[] alphas = new int[4];
            int count = 0;

            AppendEntry(ids, alphas, ref count, cell.m_district1, cell.m_alpha1, id);
            AppendEntry(ids, alphas, ref count, cell.m_district2, cell.m_alpha2, id);
            AppendEntry(ids, alphas, ref count, cell.m_district3, cell.m_alpha3, id);
            AppendEntry(ids, alphas, ref count, cell.m_district4, cell.m_alpha4, id);

            for (int i = 1; i < count; i++)
            {
                byte entryId = ids[i];
                int entryAlpha = alphas[i];
                int j = i - 1;
                while (j >= 0 && alphas[j] < entryAlpha)
                {
                    ids[j + 1] = ids[j];
                    alphas[j + 1] = alphas[j];
                    j--;
                }
                ids[j + 1] = entryId;
                alphas[j + 1] = entryAlpha;
            }

            DistrictManager.Cell result = new DistrictManager.Cell();
            result.m_district1 = count > 0 ? ids[0] : (byte)0;
            result.m_alpha1 = count > 0 ? (byte)alphas[0] : (byte)255;
            result.m_district2 = count > 1 ? ids[1] : (byte)0;
            result.m_alpha2 = count > 1 ? (byte)alphas[1] : (byte)0;
            result.m_district3 = count > 2 ? ids[2] : (byte)0;
            result.m_alpha3 = count > 2 ? (byte)alphas[2] : (byte)0;
            result.m_district4 = count > 3 ? ids[3] : (byte)0;
            result.m_alpha4 = count > 3 ? (byte)alphas[3] : (byte)0;
            return result;
        }

        private static void AppendEntry(byte[] ids, int[] alphas, ref int count, byte id, byte alpha, byte removedId)
        {
            if (alpha == 0 || id == removedId)
            {
                return;
            }
            ids[count] = id;
            alphas[count] = alpha;
            count++;
        }

        private static bool IsCreatedDistrict(DistrictManager manager, int id)
        {
            if (id < 0 || id >= manager.m_districts.m_buffer.Length)
            {
                return false;
            }
            return (manager.m_districts.m_buffer[id].m_flags & District.Flags.Created) != 0;
        }

        private static bool IsCreatedPark(DistrictManager manager, int id)
        {
            if (id < 0 || id >= manager.m_parks.m_buffer.Length)
            {
                return false;
            }
            return (manager.m_parks.m_buffer[id].m_flags & DistrictPark.Flags.Created) != 0;
        }

        private static bool Exists(DistrictManager manager, string layerName, int id)
        {
            return layerName == "park" ? IsCreatedPark(manager, id) : IsCreatedDistrict(manager, id);
        }

        private static float AreaCells(DistrictManager manager, string layerName, int id)
        {
            if (layerName == "park")
            {
                return manager.m_parks.m_buffer[id].m_totalAlpha / 255f;
            }
            return manager.m_districts.m_buffer[id].m_totalAlpha / 255f;
        }

        private static string SafeDistrictName(DistrictManager manager, int id)
        {
            try { return manager.GetDistrictName(id); }
            catch (Exception) { return null; }
        }

        private static string SafeParkName(DistrictManager manager, int id)
        {
            try { return manager.GetParkName(id); }
            catch (Exception) { return null; }
        }

        /// <summary>SetDistrictName is a one-step coroutine; enumerate it to apply the name now.</summary>
        private static bool ApplyDistrictName(DistrictManager manager, byte district, string name)
        {
            using (IEnumerator<bool> steps = manager.SetDistrictName(district, name))
            {
                while (steps.MoveNext())
                {
                }
            }
            string after = SafeDistrictName(manager, district);

            // An empty name clears the custom name (the game falls back to its generated name);
            // a non-empty name must read back exactly.
            if (name.Length == 0)
            {
                return after != null;
            }
            return string.Compare(name, after, StringComparison.Ordinal) == 0;
        }

        private static bool ApplyParkName(DistrictManager manager, byte park, string name)
        {
            using (IEnumerator<bool> steps = manager.SetParkName(park, name))
            {
                while (steps.MoveNext())
                {
                }
            }
            string after = SafeParkName(manager, park);
            if (name.Length == 0)
            {
                return true;
            }
            return string.Compare(name, after, StringComparison.Ordinal) == 0;
        }

        private static CommandResult NameResult(string action, string layerName, int id,
            string before, string requested, bool dryRun, bool applied)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":").Append(JsonUtil.Bool(dryRun));
            json.Append(",\"action\":\"").Append(action).Append("\"");
            json.Append(",\"layer\":\"").Append(layerName).Append("\"");
            json.Append(",\"id\":").Append(id);
            json.Append(",\"name\":{\"before\":").Append(NullableString(before));
            json.Append(",\"after\":").Append(NullableString(requested)).Append("}");
            json.Append(",\"applied\":").Append(JsonUtil.Bool(applied));
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static string NullableString(string value)
        {
            return value == null ? "null" : "\"" + JsonUtil.Escape(value) + "\"";
        }

        private static string NormalizeLayer(string value)
        {
            string layer = value == null ? "" : value.Trim().ToLowerInvariant();
            if (layer == "district" || layer == "districts")
            {
                return "district";
            }
            if (layer == "park" || layer == "parks" || layer == "area" || layer == "areas")
            {
                return "park";
            }
            return "";
        }

        private static int ReadId(string body, string layerName)
        {
            if (HasKey(body, "id"))
            {
                return (int)JsonUtil.GetNumber(body, "id", 0f);
            }
            if (layerName == "park" && HasKey(body, "park"))
            {
                return (int)JsonUtil.GetNumber(body, "park", 0f);
            }
            if (layerName == "district" && HasKey(body, "district"))
            {
                return (int)JsonUtil.GetNumber(body, "district", 0f);
            }
            return 0;
        }

        /// <summary>
        /// Whether a top-level key is present. The body comes from our own client as a flat
        /// object, so a plain quote search is enough and keeps 0 (a real id) distinct from
        /// "not supplied" (the default of the numeric reader).
        /// </summary>
        private static bool HasKey(string body, string name)
        {
            if (body == null)
            {
                return false;
            }
            return body.IndexOf("\"" + name + "\"", StringComparison.Ordinal) >= 0;
        }

        private static string DescribeMissing(string layerName, int id)
        {
            return layerName == "park"
                ? "Area " + id + " does not exist."
                : "District " + id + " does not exist.";
        }

        private static string DescribeParkTypes()
        {
            StringBuilder list = new StringBuilder();
            foreach (DistrictPark.ParkType type in Enum.GetValues(typeof(DistrictPark.ParkType)))
            {
                if (type == DistrictPark.ParkType.None)
                {
                    continue;
                }
                if (list.Length > 0)
                {
                    list.Append(", ");
                }
                list.Append(type.ToString());
            }
            return list.ToString();
        }

        private static string DescribeSpecializations()
        {
            StringBuilder list = new StringBuilder();
            foreach (DistrictPolicies.Policies policy in Enum.GetValues(typeof(DistrictPolicies.Policies)))
            {
                int value = (int)policy;
                if (value < 0 || value > SpecializationMaxValue || policy == DistrictPolicies.Policies.None)
                {
                    continue;
                }
                if (list.Length > 0)
                {
                    list.Append(", ");
                }
                list.Append(policy.ToString());
            }
            return list.ToString();
        }

        private static bool TryParseParkType(string value, out DistrictPark.ParkType type)
        {
            try
            {
                type = (DistrictPark.ParkType)Enum.Parse(typeof(DistrictPark.ParkType), value, true);
                return true;
            }
            catch
            {
                type = DistrictPark.ParkType.None;
                return false;
            }
        }

        private static DistrictPark.ParkLevel ParseParkLevel(string value)
        {
            if (value == null || value.Trim().Length == 0)
            {
                return DistrictPark.ParkLevel.Level1;
            }
            try
            {
                DistrictPark.ParkLevel level = (DistrictPark.ParkLevel)Enum.Parse(typeof(DistrictPark.ParkLevel), value.Trim(), true);
                return level == DistrictPark.ParkLevel.None ? DistrictPark.ParkLevel.Level1 : level;
            }
            catch
            {
                return DistrictPark.ParkLevel.Level1;
            }
        }

        private static bool TryParsePolicy(string value, out DistrictPolicies.Policies policy)
        {
            try
            {
                policy = (DistrictPolicies.Policies)Enum.Parse(typeof(DistrictPolicies.Policies), value, true);
                return true;
            }
            catch
            {
                policy = DistrictPolicies.Policies.None;
                return false;
            }
        }

        /// <summary>Per-id cell bounds over one grid, gathered in a single pass.</summary>
        private sealed class GridScan
        {
            public int PaintedCells;
            public readonly int[] MinX = new int[MaxEntities];
            public readonly int[] MinZ = new int[MaxEntities];
            public readonly int[] MaxX = new int[MaxEntities];
            public readonly int[] MaxZ = new int[MaxEntities];

            public GridScan()
            {
                for (int i = 0; i < MaxEntities; i++)
                {
                    MinX[i] = GridResolution;
                    MinZ[i] = GridResolution;
                    MaxX[i] = -1;
                    MaxZ[i] = -1;
                }
            }
        }
    }
}
