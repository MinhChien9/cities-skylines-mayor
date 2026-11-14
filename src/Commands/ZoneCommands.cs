using System.Collections.Generic;
using UnityEngine;
using SkylinesAgentBridge.State;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    public static class ZoneCommands
    {
        /// <summary>
        /// Paints zone cells inside a circle - the zone tool's brush (<c>ZoneTool.ApplyBrush</c>):
        /// a cell is painted when its centre lies inside the circle. Like the tool, an
        /// already-zoned cell is only changed when <c>overwrite</c> is true, and painting
        /// <c>Unzoned</c> clears cells. Only valid cells (along a zonable road, not blocked)
        /// can hold a zone.
        /// </summary>
        public static CommandResult SetZone(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            bool overwrite = JsonUtil.GetBool(body, "overwrite", false);
            string zoneName = JsonUtil.GetString(body, "zone", "ResidentialLow");
            float radius = JsonUtil.GetNumber(body, "radius", 32f);
            Vector3 center = CommandInput.ReadPoint(body, "center");

            if (radius <= 0f || radius > 256f)
            {
                return CommandResult.Fail("Radius must be between 0 and 256.");
            }

            ItemClass.Zone zone;
            if (!TryParseZone(zoneName, out zone))
            {
                return CommandResult.Fail("Unsupported zone: " + zoneName);
            }

            ZoneManager manager = ZoneManager.instance;
            ZoneTally tally = new ZoneTally();
            for (int i = 1; i < manager.m_blocks.m_buffer.Length; i++)
            {
                if ((manager.m_blocks.m_buffer[i].m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }
                Vector3 delta = manager.m_blocks.m_buffer[i].m_position - center;
                if (Mathf.Abs(delta.x) > 46f + radius || Mathf.Abs(delta.z) > 46f + radius)
                {
                    continue;
                }
                PaintBlock((ushort)i, zone, overwrite, dryRun, 4, center, radius, tally);
            }

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"zone\":\"" + JsonUtil.Escape(zone.ToString()) + "\"" +
                ",\"overwrite\":" + JsonUtil.Bool(overwrite) + tally.Json() + "}");
        }

        /// <summary>
        /// Zones the cells along one road segment: the zone blocks the road itself created
        /// (<c>m_blockStartLeft/Right</c>, <c>m_blockEndLeft/Right</c>), on one or both sides,
        /// up to <c>depth</c> cells (1-4) back from the road. This is the precise way to say
        /// "zone both sides of this street", and it never spills onto neighbouring roads.
        /// </summary>
        public static CommandResult ZoneSegment(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            bool overwrite = JsonUtil.GetBool(body, "overwrite", false);
            string zoneName = JsonUtil.GetString(body, "zone", "ResidentialLow");
            string side = JsonUtil.GetString(body, "side", "both");
            int depth = Mathf.Clamp((int)JsonUtil.GetNumber(body, "depth", 4f), 1, 4);
            ushort segmentId = (ushort)JsonUtil.GetNumber(body, "segmentId", 0f);

            ItemClass.Zone zone;
            if (!TryParseZone(zoneName, out zone))
            {
                return CommandResult.Fail("Unsupported zone: " + zoneName);
            }
            if (side != "left" && side != "right" && side != "both")
            {
                return CommandResult.Fail("side must be left, right or both.");
            }

            NetSegment[] segments = NetManager.instance.m_segments.m_buffer;
            if (segmentId == 0 || segmentId >= segments.Length ||
                (segments[segmentId].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
            {
                return CommandResult.Fail("Road segment was not found: " + segmentId);
            }

            NetSegment segment = segments[segmentId];
            List<ushort> blocks = new List<ushort>();
            if (side != "right")
            {
                AddBlock(blocks, segment.m_blockStartLeft);
                AddBlock(blocks, segment.m_blockEndLeft);
            }
            if (side != "left")
            {
                AddBlock(blocks, segment.m_blockStartRight);
                AddBlock(blocks, segment.m_blockEndRight);
            }
            if (blocks.Count == 0)
            {
                return CommandResult.Fail("Segment " + segmentId + " has no zone blocks on that side. Highways, ramps, pipes and very short or steep roads cannot be zoned.");
            }

            ZoneTally tally = new ZoneTally();
            for (int i = 0; i < blocks.Count; i++)
            {
                PaintBlock(blocks[i], zone, overwrite, dryRun, depth, Vector3.zero, -1f, tally);
            }

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"segmentId\":" + segmentId +
                ",\"zone\":\"" + JsonUtil.Escape(zone.ToString()) + "\"" +
                ",\"side\":\"" + side + "\",\"depth\":" + depth +
                ",\"overwrite\":" + JsonUtil.Bool(overwrite) +
                ",\"blocks\":" + blocks.Count + tally.Json() + "}");
        }

        private sealed class ZoneTally
        {
            public int Blocks;
            public int Changed;
            public int AlreadyZoned;
            public int Invalid;

            public string Json()
            {
                return ",\"touchedBlocks\":" + Blocks + ",\"changedCells\":" + Changed +
                    ",\"skippedAlreadyZoned\":" + AlreadyZoned + ",\"skippedInvalidCells\":" + Invalid;
            }
        }

        private static void AddBlock(List<ushort> blocks, ushort block)
        {
            if (block != 0 && (ZoneManager.instance.m_blocks.m_buffer[block].m_flags & ZoneBlock.FLAG_CREATED) != 0 && !blocks.Contains(block))
            {
                blocks.Add(block);
            }
        }

        /// <summary>
        /// Applies a zone to the cells of one block. Cell column 0 is the one touching the
        /// road; <paramref name="depth"/> limits how far back from the road cells are
        /// painted. With <paramref name="radius"/> &gt; 0 only cells whose centre is inside
        /// the circle are painted (the brush). Mirrors ZoneTool.ApplyBrush, plus the m_valid
        /// check so the counts are honest.
        /// </summary>
        private static void PaintBlock(ushort blockId, ItemClass.Zone zone, bool overwrite, bool dryRun, int depth,
            Vector3 center, float radius, ZoneTally tally)
        {
            ZoneManager manager = ZoneManager.instance;
            ZoneBlock block = manager.m_blocks.m_buffer[blockId];
            int rows = block.RowCount;
            Vector3 forward = new Vector3(Mathf.Cos(block.m_angle), 0f, Mathf.Sin(block.m_angle)) * 8f;
            Vector3 side = new Vector3(forward.z, 0f, -forward.x);
            bool touched = false;
            bool changed = false;

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < depth; col++)
                {
                    if (radius > 0f)
                    {
                        Vector3 cell = block.m_position + ((float)col - 3.5f) * forward + ((float)row - 3.5f) * side - center;
                        if (cell.x * cell.x + cell.z * cell.z > radius * radius)
                        {
                            continue;
                        }
                    }
                    touched = true;
                    if ((block.m_valid & (1UL << ((row << 3) | col))) == 0)
                    {
                        tally.Invalid++;
                        continue;
                    }
                    ItemClass.Zone current = block.GetZone(col, row);
                    if (current == zone)
                    {
                        continue;
                    }
                    if (!overwrite && zone != ItemClass.Zone.Unzoned && current != ItemClass.Zone.Unzoned)
                    {
                        tally.AlreadyZoned++;
                        continue;
                    }
                    if (dryRun)
                    {
                        tally.Changed++;
                        continue;
                    }
                    if (manager.m_blocks.m_buffer[blockId].SetZone(col, row, zone))
                    {
                        tally.Changed++;
                        changed = true;
                    }
                }
            }

            if (touched)
            {
                tally.Blocks++;
            }
            if (changed)
            {
                manager.m_blocks.m_buffer[blockId].RefreshZoning(blockId);
            }
        }

        public static CommandResult RepairZonesToGrowables(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            ZoneManager manager = ZoneManager.instance;
            int inspectedBlocks = 0;
            int repairableBlocks = 0;
            int skippedMixedUseBlocks = 0;
            int changedCells = 0;

            for (int i = 1; i < manager.m_blocks.m_buffer.Length; i++)
            {
                ZoneBlock block = manager.m_blocks.m_buffer[i];
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }

                inspectedBlocks++;
                ItemClass.Zone expectedZone;
                bool hasGrowable;
                bool mixedUse;
                GetExpectedZoneFromNearbyGrowables(block, out expectedZone, out hasGrowable, out mixedUse);
                if (!hasGrowable)
                {
                    continue;
                }
                if (mixedUse)
                {
                    skippedMixedUseBlocks++;
                    continue;
                }

                int rows = ZoneHelpers.GetRowCount(block);
                bool needsRepair = false;
                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ItemClass.Zone current = block.GetZone(x, z);
                        if (current != ItemClass.Zone.Unzoned && current != expectedZone)
                        {
                            needsRepair = true;
                        }
                    }
                }

                if (!needsRepair)
                {
                    continue;
                }

                repairableBlocks++;
                if (dryRun)
                {
                    continue;
                }

                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        try
                        {
                            ItemClass.Zone current = manager.m_blocks.m_buffer[i].GetZone(x, z);
                            if (current != ItemClass.Zone.Unzoned && manager.m_blocks.m_buffer[i].SetZone(x, z, expectedZone))
                            {
                                changedCells++;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            Debug.Log("[SkylinesAgentBridge] Skipped growable zone repair cell " + i + "/" + x + "/" + z + ": " + ex.Message);
                        }
                    }
                }

                try
                {
                    manager.m_blocks.m_buffer[i].RefreshZoning((ushort)i);
                    manager.UpdateBlock((ushort)i);
                }
                catch (System.Exception ex)
                {
                    Debug.Log("[SkylinesAgentBridge] Failed to refresh repaired zone block " + i + ": " + ex.Message);
                }
            }

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"inspectedBlocks\":" + inspectedBlocks +
                ",\"repairableBlocks\":" + repairableBlocks +
                ",\"skippedMixedUseBlocks\":" + skippedMixedUseBlocks +
                ",\"changedCells\":" + changedCells + "}");
        }

        public static CommandResult RepairZoneClusters(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            bool includePatchy = JsonUtil.GetBool(body, "includePatchy", true);
            bool fillUnzoned = JsonUtil.GetBool(body, "fillUnzoned", true);
            bool preferGrowableZone = JsonUtil.GetBool(body, "preferGrowableZone", true);
            float gridSize = JsonUtil.GetNumber(body, "gridSize", 80f);
            int minMinorityCells = (int)JsonUtil.GetNumber(body, "minMinorityCells", 3f);
            int minUnzonedCells = (int)JsonUtil.GetNumber(body, "minUnzonedCells", 6f);
            if (gridSize < 24f)
            {
                gridSize = 24f;
            }
            if (gridSize > 160f)
            {
                gridSize = 160f;
            }
            if (minMinorityCells < 1)
            {
                minMinorityCells = 1;
            }
            if (minUnzonedCells < 1)
            {
                minUnzonedCells = 1;
            }

            ZoneManager manager = ZoneManager.instance;
            Dictionary<string, ZoneClusterRepairStats> clusters = BuildZoneClusters(manager, gridSize);
            int repairableClusters = 0;
            int repairedBlocks = 0;
            int changedCells = 0;

            foreach (KeyValuePair<string, ZoneClusterRepairStats> pair in clusters)
            {
                ZoneClusterRepairStats stats = pair.Value;
                bool isMixed = stats.DistinctZoned > 1 && stats.MinorityCells >= minMinorityCells;
                bool isPatchy = includePatchy && stats.DistinctZoned == 1 && stats.ZonedCells >= minMinorityCells && stats.UnzonedCells >= minUnzonedCells;
                if (!isMixed && !isPatchy)
                {
                    continue;
                }

                ItemClass.Zone clusterTargetZone;
                if (!TryParseZone(stats.DominantZone, out clusterTargetZone) || clusterTargetZone == ItemClass.Zone.Unzoned)
                {
                    continue;
                }

                repairableClusters++;
                if (dryRun)
                {
                    continue;
                }

                for (int i = 0; i < stats.BlockIds.Count; i++)
                {
                    ushort blockId = stats.BlockIds[i];
                    ZoneBlock block = manager.m_blocks.m_buffer[blockId];
                    if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                    {
                        continue;
                    }

                    bool blockChanged = false;
                    ItemClass.Zone blockTargetZone = clusterTargetZone;
                    if (preferGrowableZone)
                    {
                        ItemClass.Zone growableZone;
                        bool hasGrowable;
                        bool mixedUse;
                        GetExpectedZoneFromNearbyGrowables(block, out growableZone, out hasGrowable, out mixedUse);
                        if (hasGrowable && !mixedUse && growableZone != ItemClass.Zone.Unzoned)
                        {
                            blockTargetZone = growableZone;
                        }
                    }

                    int rows = ZoneHelpers.GetRowCount(block);
                    for (int z = 0; z < rows; z++)
                    {
                        for (int x = 0; x < 4; x++)
                        {
                            try
                            {
                                ItemClass.Zone current = manager.m_blocks.m_buffer[blockId].GetZone(x, z);
                                if (current == blockTargetZone)
                                {
                                    continue;
                                }
                                if (!fillUnzoned && current == ItemClass.Zone.Unzoned)
                                {
                                    continue;
                                }

                                if (manager.m_blocks.m_buffer[blockId].SetZone(x, z, blockTargetZone))
                                {
                                    changedCells++;
                                    blockChanged = true;
                                }
                            }
                            catch (System.Exception ex)
                            {
                                Debug.Log("[SkylinesAgentBridge] Skipped zone cluster repair cell " + blockId + "/" + x + "/" + z + ": " + ex.Message);
                            }
                        }
                    }

                    if (blockChanged)
                    {
                        repairedBlocks++;
                        try
                        {
                            manager.m_blocks.m_buffer[blockId].RefreshZoning(blockId);
                            manager.UpdateBlock(blockId);
                        }
                        catch (System.Exception ex)
                        {
                            Debug.Log("[SkylinesAgentBridge] Failed to refresh cluster-repaired zone block " + blockId + ": " + ex.Message);
                        }
                    }
                }
            }

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"gridSize\":" + JsonUtil.Number(gridSize) +
                ",\"includePatchy\":" + JsonUtil.Bool(includePatchy) +
                ",\"fillUnzoned\":" + JsonUtil.Bool(fillUnzoned) +
                ",\"preferGrowableZone\":" + JsonUtil.Bool(preferGrowableZone) +
                ",\"clusterCount\":" + clusters.Count +
                ",\"repairableClusters\":" + repairableClusters +
                ",\"repairedBlocks\":" + repairedBlocks +
                ",\"changedCells\":" + changedCells + "}");
        }

        private static bool TryParseZone(string name, out ItemClass.Zone zone)
        {
            if (name == "Unzoned") { zone = ItemClass.Zone.Unzoned; return true; }
            if (name == "ResidentialLow") { zone = ItemClass.Zone.ResidentialLow; return true; }
            if (name == "ResidentialHigh") { zone = ItemClass.Zone.ResidentialHigh; return true; }
            if (name == "CommercialLow") { zone = ItemClass.Zone.CommercialLow; return true; }
            if (name == "CommercialHigh") { zone = ItemClass.Zone.CommercialHigh; return true; }
            if (name == "Industrial") { zone = ItemClass.Zone.Industrial; return true; }
            if (name == "Office") { zone = ItemClass.Zone.Office; return true; }
            zone = ItemClass.Zone.Unzoned;
            return false;
        }

        private static void GetExpectedZoneFromNearbyGrowables(ZoneBlock block, out ItemClass.Zone expectedZone, out bool hasGrowable, out bool mixedUse)
        {
            expectedZone = ItemClass.Zone.Unzoned;
            hasGrowable = false;
            mixedUse = false;
            BuildingManager manager = BuildingManager.instance;
            float closestSq = float.MaxValue;

            for (ushort i = 1; i < manager.m_buildings.m_buffer.Length; i++)
            {
                Building building = manager.m_buildings.m_buffer[i];
                if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }

                BuildingInfo info = building.Info;
                if (info == null || info.m_class == null)
                {
                    continue;
                }

                ItemClass.Zone buildingZone;
                if (!TryGetGrowableZone(info, out buildingZone))
                {
                    continue;
                }

                float protectionRadius = Mathf.Max(info.GetWidth(), info.GetLength()) * 4f + 38f;
                Vector3 delta = building.m_position - block.m_position;
                delta.y = 0f;
                if (delta.sqrMagnitude > protectionRadius * protectionRadius)
                {
                    continue;
                }

                if (!hasGrowable || delta.sqrMagnitude < closestSq)
                {
                    expectedZone = buildingZone;
                    hasGrowable = true;
                    closestSq = delta.sqrMagnitude;
                }
                else if (expectedZone != buildingZone && Mathf.Abs(delta.sqrMagnitude - closestSq) < 100f)
                {
                    mixedUse = true;
                    return;
                }
            }
        }

        private static bool TryGetGrowableZone(BuildingInfo info, out ItemClass.Zone zone)
        {
            string service = info.m_class.m_service.ToString();
            string subService = info.m_class.m_subService.ToString();
            if (service == "Residential")
            {
                zone = subService == "ResidentialHigh" ? ItemClass.Zone.ResidentialHigh : ItemClass.Zone.ResidentialLow;
                return true;
            }
            if (service == "Commercial")
            {
                zone = subService == "CommercialHigh" ? ItemClass.Zone.CommercialHigh : ItemClass.Zone.CommercialLow;
                return true;
            }
            if (service == "Industrial")
            {
                zone = ItemClass.Zone.Industrial;
                return true;
            }
            if (service == "Office")
            {
                zone = ItemClass.Zone.Office;
                return true;
            }

            zone = ItemClass.Zone.Unzoned;
            return false;
        }

        private static Dictionary<string, ZoneClusterRepairStats> BuildZoneClusters(ZoneManager manager, float gridSize)
        {
            Dictionary<string, ZoneClusterRepairStats> clusters = new Dictionary<string, ZoneClusterRepairStats>();
            for (ushort blockId = 1; blockId < manager.m_blocks.m_buffer.Length; blockId++)
            {
                ZoneBlock block = manager.m_blocks.m_buffer[blockId];
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }

                string key = GameInfoHelpers.ClusterKey(block.m_position, gridSize);
                ZoneClusterRepairStats stats;
                if (!clusters.TryGetValue(key, out stats))
                {
                    stats = new ZoneClusterRepairStats(key);
                    clusters[key] = stats;
                }
                stats.Add(blockId, block);
            }

            return clusters;
        }
        private sealed class ZoneClusterRepairStats
        {
            public readonly string Key;
            public readonly List<ushort> BlockIds = new List<ushort>();
            private readonly Dictionary<string, int> zoneCounts = new Dictionary<string, int>();
            public int CellCount;
            public int ZonedCells;
            public int UnzonedCells;
            public int DominantCells;
            public int DistinctZoned;
            public int MinorityCells;
            public string DominantZone = "Unzoned";

            public ZoneClusterRepairStats(string key)
            {
                Key = key;
            }

            public void Add(ushort blockId, ZoneBlock block)
            {
                BlockIds.Add(blockId);
                int rows = ZoneHelpers.GetRowCount(block);
                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ItemClass.Zone zone = block.GetZone(x, z);
                        string zoneName = zone.ToString();
                        if (zoneCounts.ContainsKey(zoneName))
                        {
                            zoneCounts[zoneName]++;
                        }
                        else
                        {
                            zoneCounts[zoneName] = 1;
                        }

                        CellCount++;
                        if (zone == ItemClass.Zone.Unzoned)
                        {
                            UnzonedCells++;
                        }
                        else
                        {
                            ZonedCells++;
                        }
                    }
                }

                Recalculate();
            }

            private void Recalculate()
            {
                DominantCells = 0;
                DominantZone = "Unzoned";
                DistinctZoned = 0;
                foreach (KeyValuePair<string, int> pair in zoneCounts)
                {
                    if (pair.Key == "Unzoned")
                    {
                        continue;
                    }

                    DistinctZoned++;
                    if (pair.Value > DominantCells)
                    {
                        DominantCells = pair.Value;
                        DominantZone = pair.Key;
                    }
                }

                MinorityCells = ZonedCells - DominantCells;
            }
        }
    }
}
