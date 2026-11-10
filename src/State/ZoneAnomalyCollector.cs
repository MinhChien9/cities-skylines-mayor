using System.Text;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    internal sealed class ZoneAnomalyCollector
    {
        private readonly int limit;
        private readonly int minMinorityCells;
        private readonly int minUnzonedCells;
        private readonly bool includeUnzonedHoles;
        private readonly StringBuilder items = new StringBuilder();
        private readonly Dictionary<string, int> countByType = new Dictionary<string, int>();
        private int total;
        private int emitted;
        private bool firstItem = true;

        public ZoneAnomalyCollector(int limit, int minMinorityCells, int minUnzonedCells, bool includeUnzonedHoles)
        {
            this.limit = limit;
            this.minMinorityCells = minMinorityCells;
            this.minUnzonedCells = minUnzonedCells;
            this.includeUnzonedHoles = includeUnzonedHoles;
        }

        public void Collect()
        {
            ZoneManager manager = ZoneManager.instance;
            for (ushort blockId = 1; blockId < manager.m_blocks.m_buffer.Length; blockId++)
            {
                ZoneBlock block = manager.m_blocks.m_buffer[blockId];
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }

                AnalyzeBlock(blockId, block);
            }

            AnalyzeClusters(manager);
        }

        public string ToJson()
        {
            StringBuilder counts = new StringBuilder();
            bool first = true;
            foreach (KeyValuePair<string, int> pair in countByType)
            {
                if (!first)
                {
                    counts.Append(",");
                }
                counts.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }

            return "{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"minMinorityCells\":" + minMinorityCells +
                ",\"minUnzonedCells\":" + minUnzonedCells +
                ",\"includeUnzonedHoles\":" + JsonUtil.Bool(includeUnzonedHoles) +
                ",\"counts\":{" + counts.ToString() + "}" +
                ",\"anomalies\":[" + items.ToString() + "]}";
        }

        private void AnalyzeBlock(ushort blockId, ZoneBlock block)
        {
            int rows = ZoneHelpers.GetRowCount(block);

            int cellCount = rows * 4;
            int unzonedCells = 0;
            int zonedCells = 0;
            int dominantCount = 0;
            string dominantZone = "Unzoned";
            Dictionary<string, int> counts = new Dictionary<string, int>();

            for (int z = 0; z < rows; z++)
            {
                for (int x = 0; x < 4; x++)
                {
                    ItemClass.Zone zone = block.GetZone(x, z);
                    string zoneName = zone.ToString();
                    if (counts.ContainsKey(zoneName))
                    {
                        counts[zoneName]++;
                    }
                    else
                    {
                        counts[zoneName] = 1;
                    }

                    if (zone == ItemClass.Zone.Unzoned)
                    {
                        unzonedCells++;
                        continue;
                    }

                    zonedCells++;
                    if (counts[zoneName] > dominantCount)
                    {
                        dominantCount = counts[zoneName];
                        dominantZone = zoneName;
                    }
                }
            }

            int distinctZoned = 0;
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (pair.Key != "Unzoned" && pair.Value > 0)
                {
                    distinctZoned++;
                }
            }

            int minorityCells = zonedCells - dominantCount;
            if (distinctZoned > 1 && minorityCells >= minMinorityCells)
            {
                Add(blockId, block, "mixedZoneBlock", rows, cellCount, zonedCells, unzonedCells, dominantZone, minorityCells, counts);
            }
            else if (includeUnzonedHoles && distinctZoned == 1 && zonedCells >= minMinorityCells && unzonedCells >= minUnzonedCells)
            {
                Add(blockId, block, "patchyUnzonedHoles", rows, cellCount, zonedCells, unzonedCells, dominantZone, unzonedCells, counts);
            }
        }

        private void Add(ushort blockId, ZoneBlock block, string type, int rows, int cellCount, int zonedCells, int unzonedCells, string dominantZone, int suspiciousCells, Dictionary<string, int> zoneCounts)
        {
            total++;
            if (countByType.ContainsKey(type))
            {
                countByType[type]++;
            }
            else
            {
                countByType[type] = 1;
            }

            if (emitted >= limit)
            {
                return;
            }

            if (!firstItem)
            {
                items.Append(",");
            }

            items.Append("{\"type\":\"").Append(type).Append("\"");
            items.Append(",\"blockId\":").Append(blockId);
            items.Append(",\"rowCount\":").Append(rows);
            items.Append(",\"cellCount\":").Append(cellCount);
            items.Append(",\"zonedCells\":").Append(zonedCells);
            items.Append(",\"unzonedCells\":").Append(unzonedCells);
            items.Append(",\"dominantZone\":\"").Append(JsonUtil.Escape(dominantZone)).Append("\"");
            items.Append(",\"suspiciousCells\":").Append(suspiciousCells);
            items.Append(",\"zoneCounts\":{");
            bool first = true;
            foreach (KeyValuePair<string, int> pair in zoneCounts)
            {
                if (!first)
                {
                    items.Append(",");
                }
                items.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }
            items.Append("}");
            items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(block.m_position.x));
            items.Append(",\"y\":").Append(JsonUtil.Number(block.m_position.y));
            items.Append(",\"z\":").Append(JsonUtil.Number(block.m_position.z)).Append("}}");
            emitted++;
            firstItem = false;
        }

        private void AnalyzeClusters(ZoneManager manager)
        {
            Dictionary<string, ZoneClusterStats> clusters = new Dictionary<string, ZoneClusterStats>();
            for (ushort blockId = 1; blockId < manager.m_blocks.m_buffer.Length; blockId++)
            {
                ZoneBlock block = manager.m_blocks.m_buffer[blockId];
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }

                string key = GameInfoHelpers.ClusterKey(block.m_position, 80f);
                ZoneClusterStats stats;
                if (!clusters.TryGetValue(key, out stats))
                {
                    stats = new ZoneClusterStats(key);
                    clusters[key] = stats;
                }
                stats.Add(blockId, block);
            }

            foreach (KeyValuePair<string, ZoneClusterStats> pair in clusters)
            {
                ZoneClusterStats stats = pair.Value;
                if (stats.DistinctZoned > 1 && stats.MinorityCells >= minMinorityCells)
                {
                    AddCluster(stats, "mixedZoneCluster", stats.MinorityCells);
                }
                else if (includeUnzonedHoles && stats.DistinctZoned == 1 && stats.ZonedCells >= minMinorityCells && stats.UnzonedCells >= minUnzonedCells)
                {
                    AddCluster(stats, "patchyZoneCluster", stats.UnzonedCells);
                }
            }
        }

        private void AddCluster(ZoneClusterStats stats, string type, int suspiciousCells)
        {
            total++;
            if (countByType.ContainsKey(type))
            {
                countByType[type]++;
            }
            else
            {
                countByType[type] = 1;
            }

            if (emitted >= limit)
            {
                return;
            }

            if (!firstItem)
            {
                items.Append(",");
            }

            items.Append("{\"type\":\"").Append(type).Append("\"");
            items.Append(",\"clusterKey\":\"").Append(JsonUtil.Escape(stats.Key)).Append("\"");
            items.Append(",\"blockCount\":").Append(stats.BlockCount);
            items.Append(",\"cellCount\":").Append(stats.CellCount);
            items.Append(",\"zonedCells\":").Append(stats.ZonedCells);
            items.Append(",\"unzonedCells\":").Append(stats.UnzonedCells);
            items.Append(",\"dominantZone\":\"").Append(JsonUtil.Escape(stats.DominantZone)).Append("\"");
            items.Append(",\"dominantCells\":").Append(stats.DominantCells);
            items.Append(",\"minorityCells\":").Append(stats.MinorityCells);
            items.Append(",\"distinctZoned\":").Append(stats.DistinctZoned);
            items.Append(",\"suspiciousCells\":").Append(suspiciousCells);
            items.Append(",\"zoneCounts\":{");
            bool first = true;
            foreach (KeyValuePair<string, int> count in stats.ZoneCounts)
            {
                if (!first)
                {
                    items.Append(",");
                }
                items.Append("\"").Append(JsonUtil.Escape(count.Key)).Append("\":").Append(count.Value);
                first = false;
            }
            items.Append("}");
            items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(stats.Center.x));
            items.Append(",\"y\":").Append(JsonUtil.Number(stats.Center.y));
            items.Append(",\"z\":").Append(JsonUtil.Number(stats.Center.z)).Append("}");
            items.Append(",\"bounds\":{\"minX\":").Append(JsonUtil.Number(stats.MinX));
            items.Append(",\"maxX\":").Append(JsonUtil.Number(stats.MaxX));
            items.Append(",\"minZ\":").Append(JsonUtil.Number(stats.MinZ));
            items.Append(",\"maxZ\":").Append(JsonUtil.Number(stats.MaxZ)).Append("}}");
            emitted++;
            firstItem = false;
        }
        internal sealed class ZoneClusterStats
        {
            public readonly string Key;
            public readonly Dictionary<string, int> ZoneCounts = new Dictionary<string, int>();
            public int BlockCount;
            public int CellCount;
            public int ZonedCells;
            public int UnzonedCells;
            public int DominantCells;
            public int DistinctZoned;
            public int MinorityCells;
            public string DominantZone = "Unzoned";
            public float MinX = float.MaxValue;
            public float MaxX = float.MinValue;
            public float MinZ = float.MaxValue;
            public float MaxZ = float.MinValue;
            private Vector3 positionSum = Vector3.zero;

            public ZoneClusterStats(string key)
            {
                Key = key;
            }

            public Vector3 Center
            {
                get
                {
                    return BlockCount == 0 ? Vector3.zero : positionSum / BlockCount;
                }
            }

            public void Add(ushort blockId, ZoneBlock block)
            {
                BlockCount++;
                positionSum += block.m_position;
                MinX = Mathf.Min(MinX, block.m_position.x);
                MaxX = Mathf.Max(MaxX, block.m_position.x);
                MinZ = Mathf.Min(MinZ, block.m_position.z);
                MaxZ = Mathf.Max(MaxZ, block.m_position.z);

                int rows = ZoneHelpers.GetRowCount(block);

                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ItemClass.Zone zone = block.GetZone(x, z);
                        string zoneName = zone.ToString();
                        if (ZoneCounts.ContainsKey(zoneName))
                        {
                            ZoneCounts[zoneName]++;
                        }
                        else
                        {
                            ZoneCounts[zoneName] = 1;
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

                foreach (KeyValuePair<string, int> pair in ZoneCounts)
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
