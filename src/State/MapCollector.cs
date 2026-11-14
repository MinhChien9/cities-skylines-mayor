using System;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// GET /state/map — a semantic top-down raster of a square area, built from game data
    /// rather than pixels.
    ///
    /// WHY THIS EXISTS
    /// ---------------
    /// To decide where a road or a building can go, an agent needs to know where the land,
    /// the water, the existing roads and the locked map tiles are. A screenshot answers that
    /// badly: it depends on camera angle, zoom, time of day and UI overlays, and it carries
    /// no coordinates. This raster carries exact world coordinates per cell, so a plan made
    /// on it can be sent straight to the build commands.
    ///
    /// LAYOUT
    /// ------
    /// <c>cols × rows</c> cells of <c>cell</c> metres. Row 0 is the NORTH edge (max z), column
    /// 0 is the WEST edge (min x):
    ///     world x of column c = west  + (c + 0.5) * cell
    ///     world z of row r    = north - (r + 0.5) * cell
    /// <c>classes</c> is base64 of one byte per cell (row-major): the low 7 bits are a class
    /// from <see cref="Legend"/>, bit 0x80 means "outside the unlocked map area".
    /// <c>heights</c> is base64 of one little-endian uint16 per cell: terrain height × 4
    /// (quarter-metre resolution).
    /// </summary>
    internal static class MapCollector
    {
        public const byte Land = 0;
        public const byte Water = 1;
        public const byte Road = 2;
        public const byte Highway = 3;
        public const byte OtherNetwork = 4;
        public const byte ServiceBuilding = 5;
        public const byte GrowableBuilding = 6;
        public const byte ZoneResidential = 7;
        public const byte ZoneCommercial = 8;
        public const byte ZoneIndustrial = 9;
        public const byte ZoneOffice = 10;
        public const byte LockedFlag = 0x80;

        public static readonly string Legend =
            "{\"0\":\"land\",\"1\":\"water\",\"2\":\"road\",\"3\":\"highway\",\"4\":\"otherNetwork\"," +
            "\"5\":\"serviceBuilding\",\"6\":\"growableBuilding\",\"7\":\"zoneResidential\"," +
            "\"8\":\"zoneCommercial\",\"9\":\"zoneIndustrial\",\"10\":\"zoneOffice\",\"128\":\"lockedFlag\"}";

        public static string BuildMapJson(float centerX, float centerZ, float size, float cell)
        {
            cell = Mathf.Clamp(cell, 2f, 64f);
            size = Mathf.Clamp(size, cell * 8f, 4096f);
            int n = Mathf.Clamp(Mathf.RoundToInt(size / cell), 8, 512);
            size = n * cell;
            float west = centerX - size * 0.5f;
            float north = centerZ + size * 0.5f;
            float east = west + size;
            float south = north - size;

            byte[] classes = new byte[n * n];
            byte[] heights = new byte[n * n * 2];
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            GameAreaManager areas = Singleton<GameAreaManager>.instance;

            float minH = float.MaxValue;
            float maxH = float.MinValue;
            int waterCells = 0;
            int lockedCells = 0;

            // 1. terrain, water, locked tiles
            for (int r = 0; r < n; r++)
            {
                float z = north - (r + 0.5f) * cell;
                for (int c = 0; c < n; c++)
                {
                    float x = west + (c + 0.5f) * cell;
                    // The (float, float) overloads take heightmap-grid coordinates and return
                    // raw units; the Vector3 overloads take world metres and return metres.
                    Vector3 world = new Vector3(x, 0f, z);
                    float ground = terrain.SampleRawHeightSmooth(world);
                    float surface = terrain.SampleRawHeightSmoothWithWater(world, false, 0f);
                    int idx = r * n + c;
                    byte cls = Land;
                    if (surface - ground > 0.5f)
                    {
                        cls = Water;
                        waterCells++;
                    }
                    if (areas.PointOutOfArea(new Vector3(x, ground, z)))
                    {
                        cls |= LockedFlag;
                        lockedCells++;
                    }
                    classes[idx] = cls;
                    int h = Mathf.Clamp(Mathf.RoundToInt(ground * 4f), 0, 65535);
                    heights[idx * 2] = (byte)(h & 0xFF);
                    heights[idx * 2 + 1] = (byte)(h >> 8);
                    if (ground < minH) minH = ground;
                    if (ground > maxH) maxH = ground;
                }
            }

            Raster raster = new Raster(classes, n, west, north, cell);

            // 2. zones (painted under roads and buildings)
            ZoneManager zones = Singleton<ZoneManager>.instance;
            ZoneBlock[] blocks = zones.m_blocks.m_buffer;
            for (int i = 1; i < blocks.Length; i++)
            {
                if ((blocks[i].m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }
                Vector3 p = blocks[i].m_position;
                if (p.x < west - 64f || p.x > east + 64f || p.z < south - 64f || p.z > north + 64f)
                {
                    continue;
                }
                int rows = blocks[i].RowCount;
                Vector3 forward = new Vector3(Mathf.Cos(blocks[i].m_angle), 0f, Mathf.Sin(blocks[i].m_angle)) * 8f;
                Vector3 side = new Vector3(forward.z, 0f, -forward.x);
                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < 4; col++)
                    {
                        if ((blocks[i].m_valid & (1UL << ((row << 3) | col))) == 0)
                        {
                            continue;
                        }
                        byte zc = ZoneClass(blocks[i].GetZone(col, row));
                        if (zc == Land)
                        {
                            continue;
                        }
                        Vector3 cellPos = p + ((float)col - 3.5f) * forward + ((float)row - 3.5f) * side;
                        raster.Fill(cellPos.x, cellPos.z, 5.7f, zc); // covers the whole 8 m cell
                    }
                }
            }

            // 3. networks
            NetManager net = Singleton<NetManager>.instance;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode[] nodes = net.m_nodes.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }
                NetInfo info = segments[i].Info;
                if (info == null || info.m_class == null)
                {
                    continue;
                }
                byte cls = NetworkClass(info);
                if (cls == Land)
                {
                    continue; // underground (pipes, metro, tunnels)
                }
                Vector3 a = nodes[segments[i].m_startNode].m_position;
                Vector3 b = nodes[segments[i].m_endNode].m_position;
                if (Mathf.Max(a.x, b.x) < west - 64f || Mathf.Min(a.x, b.x) > east + 64f ||
                    Mathf.Max(a.z, b.z) < south - 64f || Mathf.Min(a.z, b.z) > north + 64f)
                {
                    continue;
                }
                Vector3 m1;
                Vector3 m2;
                NetSegment.CalculateMiddlePoints(a, segments[i].m_startDirection, b, segments[i].m_endDirection, true, true, out m1, out m2);
                Bezier3 curve = new Bezier3(a, m1, m2, b);
                // Not m_averageLength: that is the mean lane length, 0 for pipes and power lines.
                float length = Mathf.Max(VectorUtils.LengthXZ(b - a) * 1.2f, 1f);
                int steps = Mathf.Max(2, Mathf.CeilToInt(length / (cell * 0.5f)));
                float half = Mathf.Max(info.m_halfWidth, cell * 0.5f);
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 q = curve.Position((float)s / steps);
                    raster.Fill(q.x, q.z, half, cls);
                }
            }

            // 4. buildings
            BuildingManager bm = Singleton<BuildingManager>.instance;
            Building[] buildings = bm.m_buildings.m_buffer;
            int serviceCount = 0;
            int growableCount = 0;
            for (int i = 1; i < buildings.Length; i++)
            {
                if ((buildings[i].m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }
                if (buildings[i].m_parentBuilding != 0)
                {
                    continue;
                }
                BuildingInfo info = buildings[i].Info;
                if (info == null || info.m_class == null || GameInfoHelpers.IsInternalNetworkHelperBuilding(info))
                {
                    continue;
                }
                Vector3 p = buildings[i].m_position;
                if (p.x < west - 128f || p.x > east + 128f || p.z < south - 128f || p.z > north + 128f)
                {
                    continue;
                }
                bool growable = info.m_buildingAI is PrivateBuildingAI;
                byte cls = growable ? GrowableBuilding : ServiceBuilding;
                if (growable) growableCount++; else serviceCount++;
                raster.FillRect(p, buildings[i].m_angle, buildings[i].Width * 8f, buildings[i].Length * 8f, cls);
            }

            // 5. road dead ends: the natural places to extend the network from
            StringBuilder ends = new StringBuilder();
            int endCount = 0;
            for (int i = 1; i < nodes.Length && endCount < 60; i++)
            {
                if ((nodes[i].m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
                {
                    continue;
                }
                NetInfo info = nodes[i].Info;
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Road)
                {
                    continue;
                }
                if (nodes[i].CountSegments() != 1 || (nodes[i].m_flags & NetNode.Flags.Outside) != NetNode.Flags.None)
                {
                    continue;
                }
                Vector3 p = nodes[i].m_position;
                if (p.x < west || p.x > east || p.z < south || p.z > north)
                {
                    continue;
                }
                if (endCount > 0) ends.Append(',');
                ends.Append("{\"nodeId\":").Append(i)
                    .Append(",\"x\":").Append(JsonUtil.Number(p.x))
                    .Append(",\"y\":").Append(JsonUtil.Number(p.y))
                    .Append(",\"z\":").Append(JsonUtil.Number(p.z))
                    .Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"}");
                endCount++;
            }

            StringBuilder json = new StringBuilder(n * n * 3 + 2048);
            json.Append("{\"ok\":true");
            json.Append(",\"center\":{\"x\":").Append(JsonUtil.Number(centerX)).Append(",\"z\":").Append(JsonUtil.Number(centerZ)).Append('}');
            json.Append(",\"bounds\":{\"west\":").Append(JsonUtil.Number(west))
                .Append(",\"east\":").Append(JsonUtil.Number(east))
                .Append(",\"south\":").Append(JsonUtil.Number(south))
                .Append(",\"north\":").Append(JsonUtil.Number(north)).Append('}');
            json.Append(",\"cell\":").Append(JsonUtil.Number(cell));
            json.Append(",\"cols\":").Append(n).Append(",\"rows\":").Append(n);
            json.Append(",\"orientation\":\"row 0 = north (max z), col 0 = west (min x); x = west + (col+0.5)*cell, z = north - (row+0.5)*cell\"");
            json.Append(",\"legend\":").Append(Legend);
            json.Append(",\"heightRange\":{\"min\":").Append(JsonUtil.Number(minH)).Append(",\"max\":").Append(JsonUtil.Number(maxH)).Append('}');
            json.Append(",\"counts\":{\"waterCells\":").Append(waterCells)
                .Append(",\"lockedCells\":").Append(lockedCells)
                .Append(",\"serviceBuildings\":").Append(serviceCount)
                .Append(",\"growableBuildings\":").Append(growableCount).Append('}');
            json.Append(",\"roadDeadEnds\":[").Append(ends).Append(']');
            json.Append(",\"classes\":\"").Append(Convert.ToBase64String(classes)).Append('"');
            json.Append(",\"heights\":\"").Append(Convert.ToBase64String(heights)).Append('"');
            json.Append('}');
            return json.ToString();
        }

        private static byte ZoneClass(ItemClass.Zone zone)
        {
            switch (zone)
            {
                case ItemClass.Zone.ResidentialLow:
                case ItemClass.Zone.ResidentialHigh:
                    return ZoneResidential;
                case ItemClass.Zone.CommercialLow:
                case ItemClass.Zone.CommercialHigh:
                    return ZoneCommercial;
                case ItemClass.Zone.Industrial:
                    return ZoneIndustrial;
                case ItemClass.Zone.Office:
                    return ZoneOffice;
                default:
                    return Land;
            }
        }

        private static byte NetworkClass(NetInfo info)
        {
            ItemClass.Layer layer = info.m_class.m_layer;
            if ((layer & (ItemClass.Layer.Default | ItemClass.Layer.PublicTransport)) == 0 && layer != ItemClass.Layer.None)
            {
                return Land; // water pipes, metro tunnels and other underground layers
            }
            if (info.m_netAI is RoadTunnelAI || info.m_netAI is TrainTrackTunnelAI || info.m_netAI is PedestrianTunnelAI)
            {
                return Land;
            }
            if (info.m_class.m_service == ItemClass.Service.Road)
            {
                RoadAI road = info.m_netAI as RoadAI;
                if (road != null && !road.m_enableZoning && info.m_halfWidth >= 8f)
                {
                    return Highway;
                }
                string name = info.name ?? "";
                if (name.IndexOf("Highway", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Ramp", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return Highway;
                }
                return Road;
            }
            return OtherNetwork;
        }

        /// <summary>A class raster with world-space fill helpers. Later fills win.</summary>
        private sealed class Raster
        {
            private readonly byte[] classes;
            private readonly int n;
            private readonly float west;
            private readonly float north;
            private readonly float cell;

            public Raster(byte[] classes, int n, float west, float north, float cell)
            {
                this.classes = classes;
                this.n = n;
                this.west = west;
                this.north = north;
                this.cell = cell;
            }

            /// <summary>Fills every cell whose centre is within <paramref name="radius"/> of (x,z).</summary>
            public void Fill(float x, float z, float radius, byte cls)
            {
                int c0 = Mathf.FloorToInt((x - radius - west) / cell);
                int c1 = Mathf.FloorToInt((x + radius - west) / cell);
                int r0 = Mathf.FloorToInt((north - (z + radius)) / cell);
                int r1 = Mathf.FloorToInt((north - (z - radius)) / cell);
                float r2 = radius * radius;
                for (int r = Math.Max(r0, 0); r <= Math.Min(r1, n - 1); r++)
                {
                    float cz = north - (r + 0.5f) * cell;
                    for (int c = Math.Max(c0, 0); c <= Math.Min(c1, n - 1); c++)
                    {
                        float cx = west + (c + 0.5f) * cell;
                        float dx = cx - x;
                        float dz = cz - z;
                        if (dx * dx + dz * dz <= r2 || (c0 == c1 && r0 == r1))
                        {
                            Set(r * n + c, cls);
                        }
                    }
                }
            }

            /// <summary>Fills a rotated rectangle (building footprint).</summary>
            public void FillRect(Vector3 center, float angle, float width, float length, byte cls)
            {
                Vector3 ax = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                Vector3 az = new Vector3(-ax.z, 0f, ax.x);
                float hw = width * 0.5f;
                float hl = length * 0.5f;
                float reach = Mathf.Sqrt(hw * hw + hl * hl);
                int c0 = Mathf.FloorToInt((center.x - reach - west) / cell);
                int c1 = Mathf.FloorToInt((center.x + reach - west) / cell);
                int r0 = Mathf.FloorToInt((north - (center.z + reach)) / cell);
                int r1 = Mathf.FloorToInt((north - (center.z - reach)) / cell);
                bool any = false;
                for (int r = Math.Max(r0, 0); r <= Math.Min(r1, n - 1); r++)
                {
                    float cz = north - (r + 0.5f) * cell;
                    for (int c = Math.Max(c0, 0); c <= Math.Min(c1, n - 1); c++)
                    {
                        float cx = west + (c + 0.5f) * cell;
                        float dx = cx - center.x;
                        float dz = cz - center.z;
                        float u = dx * ax.x + dz * ax.z;
                        float v = dx * az.x + dz * az.z;
                        if (Mathf.Abs(u) <= hw && Mathf.Abs(v) <= hl)
                        {
                            Set(r * n + c, cls);
                            any = true;
                        }
                    }
                }
                if (!any)
                {
                    Fill(center.x, center.z, 0f, cls); // smaller than a cell: mark its cell
                }
            }

            private void Set(int idx, byte cls)
            {
                classes[idx] = (byte)((classes[idx] & LockedFlag) | cls);
            }
        }
    }
}
