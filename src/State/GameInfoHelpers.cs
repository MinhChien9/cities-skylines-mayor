using ColossalFramework;
using UnityEngine;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// Predicates and lookups over CS1's game objects that more than one module needs.
    ///
    /// These used to be copy-pasted into the state builders, the anomaly collectors and
    /// the zone repair commands. Keeping one copy here means a fix to, say, "what counts
    /// as a created road segment" cannot drift between the detector that reports it and
    /// the repair that fixes it.
    /// </summary>
    internal static class GameInfoHelpers
    {
        /// <summary>
        /// True for the two internal pipe-junction buildings the game spawns itself.
        /// They are not player-placed, so they must never be zoned over or counted as
        /// city facilities.
        /// </summary>
        public static bool IsInternalNetworkHelperBuilding(BuildingInfo info)
        {
            return info != null &&
                (info.name == "Water Pipe Junction" || info.name == "Heating Pipe Junction");
        }

        /// <summary>True when the prefab belongs to the road service.</summary>
        public static bool IsRoadInfo(NetInfo info)
        {
            return info != null && info.m_class != null && info.m_class.m_service == ItemClass.Service.Road;
        }

        /// <summary>
        /// True for a segment the player actually built (not a service helper, not a
        /// highway ramp stub, not an unloaded prefab placeholder).
        /// </summary>
        public static bool IsCreatedRoadSegment(NetSegment segment)
        {
            return (segment.m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None &&
                IsRoadInfo(segment.Info);
        }

        /// <summary>
        /// Reads the segment id stored at one of a node's eight segment slots.
        /// The game packs them as individual fields rather than an array.
        /// </summary>
        public static ushort GetSegmentId(NetNode node, int index)
        {
            if (index == 0) return node.m_segment0;
            if (index == 1) return node.m_segment1;
            if (index == 2) return node.m_segment2;
            if (index == 3) return node.m_segment3;
            if (index == 4) return node.m_segment4;
            if (index == 5) return node.m_segment5;
            if (index == 6) return node.m_segment6;
            return node.m_segment7;
        }

        /// <summary>
        /// The grid cell key used to group blocks into spatial clusters.
        /// Shared by the anomaly detector and the cluster repair so both agree on what
        /// "the same cluster" means.
        /// </summary>
        public static string ClusterKey(Vector3 position, float gridSize)
        {
            int x = Mathf.FloorToInt((position.x + gridSize * 0.5f) / gridSize);
            int z = Mathf.FloorToInt((position.z + gridSize * 0.5f) / gridSize);
            return x.ToString() + ":" + z.ToString();
        }

        /// <summary>True for the services that count as city-wide facilities.</summary>
        public static bool IsCityFacilityService(string service)
        {
            return service == "Water" ||
                service == "Electricity" ||
                service == "Garbage" ||
                service == "HealthCare" ||
                service == "PoliceDepartment" ||
                service == "FireDepartment" ||
                service == "Education" ||
                service == "Disaster";
        }

        /// <summary>True for the four zoned services (residential, commercial, industrial, office).</summary>
        public static bool IsGrowableService(string service)
        {
            return service == "Residential" ||
                service == "Commercial" ||
                service == "Industrial" ||
                service == "Office";
        }
    }
}
