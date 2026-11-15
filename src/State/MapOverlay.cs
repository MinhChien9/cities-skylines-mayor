using System;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// The optional data layer of GET /state/map (<c>overlay=</c>): one byte per map cell for
    /// land value, ground pollution, noise or road traffic, for the story maps of the videos
    /// and for the mayor's own decisions (where to put a park, which road is jammed).
    ///
    /// Values are the game's own numbers, clamped to 0-255: land value and noise from
    /// <c>ImmaterialResourceManager.CheckLocalResource</c>, ground pollution from
    /// <c>NaturalResourceManager.CheckPollution</c> (0-255), traffic from each road
    /// segment's <c>m_trafficDensity</c> (0-100 %, scaled to 0-255) painted along the road.
    /// Service coverage (police, fire, health, education, ...) is the local rate of the
    /// matching <c>ImmaterialResourceManager</c> resource, the number <c>ResidentAI</c> feeds to
    /// <c>CalculateResourceEffect</c> for wellbeing (full effect at 500), halved to fit a byte.
    /// Runs inside the map read, on the simulation thread.
    /// </summary>
    internal static class MapOverlay
    {
        public static readonly string[] Names =
        {
            "landValue", "pollution", "noise", "traffic",
            "police", "fire", "health", "education", "highSchool", "university",
            "entertainment", "transport", "deathCare",
            "ore", "oil", "forest", "fertility",
        };

        /// <summary>The coverage overlays and the resource each reads.</summary>
        private static bool TryCoverage(string name, out ImmaterialResourceManager.Resource resource)
        {
            switch (name)
            {
                case "police": resource = ImmaterialResourceManager.Resource.PoliceDepartment; return true;
                case "fire": resource = ImmaterialResourceManager.Resource.FireDepartment; return true;
                case "health": resource = ImmaterialResourceManager.Resource.HealthCare; return true;
                case "education": resource = ImmaterialResourceManager.Resource.EducationElementary; return true;
                case "highSchool": resource = ImmaterialResourceManager.Resource.EducationHighSchool; return true;
                case "university": resource = ImmaterialResourceManager.Resource.EducationUniversity; return true;
                case "entertainment": resource = ImmaterialResourceManager.Resource.Entertainment; return true;
                case "transport": resource = ImmaterialResourceManager.Resource.PublicTransport; return true;
                case "deathCare": resource = ImmaterialResourceManager.Resource.DeathCare; return true;
                default: resource = ImmaterialResourceManager.Resource.None; return false;
            }
        }

        public static bool IsKnown(string name)
        {
            return Array.IndexOf(Names, name) >= 0;
        }

        /// <summary>
        /// Fills one byte per cell (same layout as the map's classes) and returns the JSON
        /// description of the layer.
        /// </summary>
        public static string Build(string name, int n, float west, float north, float cell, out byte[] values)
        {
            values = new byte[n * n];
            int max = 0;
            string unit;
            ImmaterialResourceManager.Resource coverage;
            if (name == "traffic")
            {
                unit = "traffic density, 255 = 100 %";
                max = PaintTraffic(values, n, west, north, cell);
            }
            else if (name == "ore" || name == "oil" || name == "forest" || name == "fertility")
            {
                unit = name + " 0-255 (the natural resource grid, 33.75 m cells; 128+ = rich)";
                for (int r = 0; r < n; r++)
                {
                    float z = north - (r + 0.5f) * cell;
                    for (int c = 0; c < n; c++)
                    {
                        int v = ResourceCollector.Sample(name, west + (c + 0.5f) * cell, z);
                        if (v > max) max = v;
                        values[r * n + c] = (byte)Mathf.Clamp(v, 0, 255);
                    }
                }
            }
            else if (TryCoverage(name, out coverage))
            {
                unit = "coverage rate / 2 (ResidentAI wellbeing: full effect at rate 500 = 250 here; the middle effect at the age group's requirement, roughly 25-50 here)";
                ImmaterialResourceManager immaterial = Singleton<ImmaterialResourceManager>.instance;
                for (int r = 0; r < n; r++)
                {
                    float z = north - (r + 0.5f) * cell;
                    for (int c = 0; c < n; c++)
                    {
                        Vector3 p = new Vector3(west + (c + 0.5f) * cell, 0f, z);
                        int v;
                        immaterial.CheckLocalResource(coverage, p, out v);
                        if (v > max) max = v;
                        values[r * n + c] = (byte)Mathf.Clamp((v + 1) / 2, 0, 255);
                    }
                }
            }
            else
            {
                ImmaterialResourceManager immaterial = Singleton<ImmaterialResourceManager>.instance;
                NaturalResourceManager natural = Singleton<NaturalResourceManager>.instance;
                unit = name == "pollution" ? "ground pollution, 0-255" : name == "noise" ? "noise level" : "land value";
                for (int r = 0; r < n; r++)
                {
                    float z = north - (r + 0.5f) * cell;
                    for (int c = 0; c < n; c++)
                    {
                        Vector3 p = new Vector3(west + (c + 0.5f) * cell, 0f, z);
                        int v;
                        if (name == "pollution")
                        {
                            byte ground;
                            natural.CheckPollution(p, out ground);
                            v = ground;
                        }
                        else
                        {
                            immaterial.CheckLocalResource(
                                name == "noise" ? ImmaterialResourceManager.Resource.NoisePollution
                                                : ImmaterialResourceManager.Resource.LandValue,
                                p, out v);
                        }
                        if (v > max) max = v;
                        values[r * n + c] = (byte)Mathf.Clamp(v, 0, 255);
                    }
                }
            }
            return "{\"name\":\"" + name + "\",\"unit\":\"" + JsonUtil.Escape(unit) +
                "\",\"rawMax\":" + max + ",\"encoding\":\"overlayValues: base64, one byte per cell, same layout as classes\"}";
        }

        private static int PaintTraffic(byte[] values, int n, float west, float north, float cell)
        {
            NetManager net = Singleton<NetManager>.instance;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode[] nodes = net.m_nodes.m_buffer;
            float east = west + n * cell;
            float south = north - n * cell;
            int max = 0;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }
                NetInfo info = segments[i].Info;
                if (info == null || info.m_class == null || info.m_class.m_service != ItemClass.Service.Road)
                {
                    continue;
                }
                Vector3 a = nodes[segments[i].m_startNode].m_position;
                Vector3 b = nodes[segments[i].m_endNode].m_position;
                if (Mathf.Max(a.x, b.x) < west || Mathf.Min(a.x, b.x) > east ||
                    Mathf.Max(a.z, b.z) < south || Mathf.Min(a.z, b.z) > north)
                {
                    continue;
                }
                int density = segments[i].m_trafficDensity;
                if (density > max) max = density;
                byte v = (byte)Mathf.Clamp(Mathf.RoundToInt(density * 2.55f), 1, 255); // 1 = road, no traffic
                Vector3 m1;
                Vector3 m2;
                NetSegment.CalculateMiddlePoints(a, segments[i].m_startDirection, b, segments[i].m_endDirection, true, true, out m1, out m2);
                Bezier3 curve = new Bezier3(a, m1, m2, b);
                float length = Mathf.Max(VectorUtils.LengthXZ(b - a) * 1.2f, 1f);
                int steps = Mathf.Max(2, Mathf.CeilToInt(length / (cell * 0.5f)));
                float half = Mathf.Max(info.m_halfWidth, cell * 0.5f);
                for (int s = 0; s <= steps; s++)
                {
                    Vector3 q = curve.Position((float)s / steps);
                    PaintMax(values, n, west, north, cell, q.x, q.z, half, v);
                }
            }
            return max;
        }

        private static void PaintMax(byte[] values, int n, float west, float north, float cell, float x, float z, float radius, byte v)
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
                    if ((cx - x) * (cx - x) + (cz - z) * (cz - z) <= r2 && values[r * n + c] < v)
                    {
                        values[r * n + c] = v;
                    }
                }
            }
        }
    }
}
