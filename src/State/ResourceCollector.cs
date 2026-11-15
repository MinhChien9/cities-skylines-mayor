using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// GET /state/resources: where the natural resources are, so specialised industry
    /// (Forest, Farming, Oil, Ore districts) is zoned on top of them.
    ///
    /// Reads <c>NaturalResourceManager.m_naturalResources</c>, the game's 512 x 512 grid of
    /// 33.75 m cells (0-255 per resource), the same data the resource info view draws. The
    /// summary is per map tile (<c>/state/areas</c> layout): the mean over the tile, how much
    /// of it is rich (a cell at 128 or more), and the centre of the rich cells - a point to
    /// aim a district at. The full grid is a <c>/state/map</c> overlay (<c>ore</c>, <c>oil</c>,
    /// <c>forest</c>, <c>fertility</c>). Runs on the simulation thread.
    /// </summary>
    internal static class ResourceCollector
    {
        public const float CellSize = 33.75f;
        public const int Resolution = 512;
        public const int RichThreshold = 128;

        private static readonly string[] Names = { "ore", "oil", "forest", "fertility" };

        /// <summary>The resource byte of the grid cell under a world point; -1 for an unknown name.</summary>
        public static int Sample(string name, float x, float z)
        {
            NaturalResourceManager.ResourceCell[] cells = Singleton<NaturalResourceManager>.instance.m_naturalResources;
            int cx = Mathf.Clamp((int)(x / CellSize + Resolution * 0.5f), 0, Resolution - 1);
            int cz = Mathf.Clamp((int)(z / CellSize + Resolution * 0.5f), 0, Resolution - 1);
            return Value(cells[cz * Resolution + cx], name);
        }

        private static int Value(NaturalResourceManager.ResourceCell cell, string name)
        {
            switch (name)
            {
                case "ore": return cell.m_ore;
                case "oil": return cell.m_oil;
                case "forest": return cell.m_forest;
                case "fertility": return cell.m_fertility;
                default: return -1;
            }
        }

        public static CommandResult Build()
        {
            GameAreaManager areas = Singleton<GameAreaManager>.instance;
            NaturalResourceManager.ResourceCell[] cells = Singleton<NaturalResourceManager>.instance.m_naturalResources;
            int tiles = areas.m_areaGrid == null ? 5 : Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(areas.m_areaGrid.Length)));
            float cellArea = CellSize * CellSize / 1000000f; // km2

            int r = Names.Length;
            double[] ownedRich = new double[r];
            StringBuilder json = new StringBuilder("{\"ok\":true");
            json.Append(",\"grid\":{\"resolution\":").Append(Resolution)
                .Append(",\"cellSize\":").Append(JsonUtil.Number(CellSize))
                .Append(",\"richThreshold\":").Append(RichThreshold).Append('}');
            json.Append(",\"tiles\":[");
            for (int tz = 0; tz < tiles; tz++)
            {
                for (int tx = 0; tx < tiles; tx++)
                {
                    float minX;
                    float minZ;
                    float maxX;
                    float maxZ;
                    areas.GetAreaBounds(tx, tz, out minX, out minZ, out maxX, out maxZ);
                    int c0 = Mathf.Clamp(Mathf.CeilToInt(minX / CellSize + Resolution * 0.5f - 0.5f), 0, Resolution - 1);
                    int c1 = Mathf.Clamp(Mathf.FloorToInt(maxX / CellSize + Resolution * 0.5f - 0.5f), 0, Resolution - 1);
                    int r0 = Mathf.Clamp(Mathf.CeilToInt(minZ / CellSize + Resolution * 0.5f - 0.5f), 0, Resolution - 1);
                    int r1 = Mathf.Clamp(Mathf.FloorToInt(maxZ / CellSize + Resolution * 0.5f - 0.5f), 0, Resolution - 1);

                    long[] sum = new long[r];
                    int[] rich = new int[r];
                    double[] richX = new double[r];
                    double[] richZ = new double[r];
                    int count = 0;
                    for (int cz = r0; cz <= r1; cz++)
                    {
                        for (int cx = c0; cx <= c1; cx++)
                        {
                            NaturalResourceManager.ResourceCell cell = cells[cz * Resolution + cx];
                            count++;
                            for (int k = 0; k < r; k++)
                            {
                                int v = Value(cell, Names[k]);
                                sum[k] += v;
                                if (v >= RichThreshold)
                                {
                                    rich[k]++;
                                    richX[k] += (cx + 0.5f - Resolution * 0.5f) * CellSize;
                                    richZ[k] += (cz + 0.5f - Resolution * 0.5f) * CellSize;
                                }
                            }
                        }
                    }

                    bool unlocked = areas.IsUnlocked(tx, tz);
                    if (tz > 0 || tx > 0) json.Append(',');
                    json.Append("{\"x\":").Append(tx).Append(",\"z\":").Append(tz)
                        .Append(",\"unlocked\":").Append(JsonUtil.Bool(unlocked))
                        .Append(",\"canUnlock\":").Append(JsonUtil.Bool(!unlocked && areas.CanUnlock(tx, tz)))
                        .Append(",\"bounds\":{\"minX\":").Append(JsonUtil.Number(minX))
                        .Append(",\"minZ\":").Append(JsonUtil.Number(minZ))
                        .Append(",\"maxX\":").Append(JsonUtil.Number(maxX))
                        .Append(",\"maxZ\":").Append(JsonUtil.Number(maxZ)).Append('}');
                    for (int k = 0; k < r; k++)
                    {
                        json.Append(",\"").Append(Names[k]).Append("\":{\"mean\":")
                            .Append(count == 0 ? 0 : Mathf.RoundToInt((float)sum[k] / count))
                            .Append(",\"richKm2\":").Append(JsonUtil.Number(rich[k] * cellArea));
                        if (rich[k] > 0)
                        {
                            json.Append(",\"richCentre\":{\"x\":").Append(Mathf.RoundToInt((float)(richX[k] / rich[k])))
                                .Append(",\"z\":").Append(Mathf.RoundToInt((float)(richZ[k] / rich[k]))).Append('}');
                        }
                        json.Append('}');
                        if (unlocked)
                        {
                            ownedRich[k] += rich[k] * cellArea;
                        }
                    }
                    json.Append('}');
                }
            }
            json.Append("],\"ownedRichKm2\":{");
            for (int k = 0; k < r; k++)
            {
                if (k > 0) json.Append(',');
                json.Append('"').Append(Names[k]).Append("\":").Append(JsonUtil.Number((float)ownedRich[k]));
            }
            json.Append("},\"notes\":\"Values are the game's 0-255 per 33.75 m cell; rich = 128 or more. Ore and oil deplete as industry extracts them, forest and fertility do not. A Forest/Farming/Oil/Ore district only produces where its buildings stand on the resource: zone industry over the richCentre, paint the district (city_district) and specialize it. city_map overlay=ore|oil|forest|fertility shows the grid.\"}");
            return CommandResult.FromJson(json.ToString());
        }
    }
}
