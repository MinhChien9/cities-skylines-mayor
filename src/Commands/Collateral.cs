using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// What a build removed besides what it was asked to change.
    ///
    /// The game's own tools release "non-important" objects in the way when they build:
    /// a road drawn through a power line deletes that power line (and its pylons), with
    /// only a red highlight to warn the player. Through the bridge there is no highlight,
    /// so a road could silently cut the power to a building across town (found live: the
    /// sewage outlet lost its line and every service in the city stopped). This class
    /// snapshots the networks and buildings near a build and reports what is gone after it.
    /// </summary>
    internal sealed class Collateral
    {
        private readonly Dictionary<ushort, NetInfo> segments = new Dictionary<ushort, NetInfo>();
        private readonly Dictionary<ushort, BuildingInfo> buildings = new Dictionary<ushort, BuildingInfo>();
        private readonly Dictionary<ushort, Vector3> buildingPositions = new Dictionary<ushort, Vector3>();

        /// <summary>Snapshot every created segment and building whose position lies in the box.</summary>
        public static Collateral Take(float minX, float minZ, float maxX, float maxZ, float margin)
        {
            minX -= margin; minZ -= margin; maxX += margin; maxZ += margin;
            Collateral c = new Collateral();
            NetManager net = Singleton<NetManager>.instance;
            NetNode[] nodes = net.m_nodes.m_buffer;
            NetSegment[] segs = net.m_segments.m_buffer;
            for (int i = 1; i < segs.Length; i++)
            {
                if ((segs[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None) continue;
                Vector3 a = nodes[segs[i].m_startNode].m_position;
                Vector3 b = nodes[segs[i].m_endNode].m_position;
                if (Mathf.Max(a.x, b.x) < minX || Mathf.Min(a.x, b.x) > maxX || Mathf.Max(a.z, b.z) < minZ || Mathf.Min(a.z, b.z) > maxZ) continue;
                c.segments[(ushort)i] = segs[i].Info;
            }
            Building[] blds = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            for (int i = 1; i < blds.Length; i++)
            {
                if ((blds[i].m_flags & Building.Flags.Created) == Building.Flags.None) continue;
                Vector3 p = blds[i].m_position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                c.buildings[(ushort)i] = blds[i].Info;
                c.buildingPositions[(ushort)i] = p;
            }
            return c;
        }

        /// <summary>
        /// JSON fields <c>"removed":{...}</c> (and a <c>"warning"</c> when anything went),
        /// leading comma included. Segments for which <paramref name="expected"/> returns
        /// true (a road split by design) are not reported.
        /// </summary>
        public string RemovedJson(System.Predicate<NetInfo> expected)
        {
            NetSegment[] segs = Singleton<NetManager>.instance.m_segments.m_buffer;
            Building[] blds = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            StringBuilder s = new StringBuilder();
            StringBuilder b = new StringBuilder();
            int segCount = 0;
            int bldCount = 0;
            bool powerCut = false;
            foreach (KeyValuePair<ushort, NetInfo> e in segments)
            {
                bool gone = (segs[e.Key].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || segs[e.Key].Info != e.Value;
                if (!gone || e.Value == null || (expected != null && expected(e.Value))) continue;
                if (segCount++ > 0) s.Append(',');
                s.Append("{\"id\":").Append(e.Key).Append(",\"prefab\":\"").Append(JsonUtil.Escape(e.Value.name)).Append("\"}");
                if (e.Value.m_class != null && e.Value.m_class.m_service == ItemClass.Service.Electricity) powerCut = true;
            }
            foreach (KeyValuePair<ushort, BuildingInfo> e in buildings)
            {
                bool gone = (blds[e.Key].m_flags & Building.Flags.Created) == Building.Flags.None || blds[e.Key].Info != e.Value;
                if (!gone || e.Value == null) continue;
                Vector3 p = buildingPositions[e.Key];
                if (bldCount++ > 0) b.Append(',');
                b.Append("{\"id\":").Append(e.Key).Append(",\"prefab\":\"").Append(JsonUtil.Escape(e.Value.name))
                    .Append("\",\"x\":").Append(JsonUtil.Number(p.x)).Append(",\"z\":").Append(JsonUtil.Number(p.z)).Append('}');
            }
            if (segCount == 0 && bldCount == 0) return "";
            string json = ",\"removed\":{\"segments\":[" + s + "],\"buildings\":[" + b + "]}";
            string warning = powerCut
                ? "The game removed a power line in the way (as the player's tool does). Anything it fed may now be unpowered: check city_problems and rebuild the line around the new road (pylons must not stand on a road)."
                : "The game removed objects in the way (as the player's tool does). Check that nothing important went.";
            return json + ",\"warning\":\"" + JsonUtil.Escape(warning) + "\"";
        }
    }
}
