using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    internal sealed class BuildingAnomalyCollector
    {
        private readonly int limit;
        private readonly StringBuilder items = new StringBuilder();
        private int total;
        private int emitted;
        private bool firstItem = true;

        public BuildingAnomalyCollector(int limit)
        {
            this.limit = limit;
        }

        public void Collect()
        {
            BuildingManager buildings = BuildingManager.instance;
            NetManager net = NetManager.instance;

            for (ushort buildingId = 1; buildingId < buildings.m_buildings.m_buffer.Length; buildingId++)
            {
                Building building = buildings.m_buildings.m_buffer[buildingId];
                if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }

                BuildingInfo buildingInfo = building.Info;
                if (buildingInfo == null || buildingInfo.m_class == null)
                {
                    continue;
                }

                if (GameInfoHelpers.IsInternalNetworkHelperBuilding(buildingInfo))
                {
                    continue;
                }

                if (!GameInfoHelpers.IsCityFacilityService(buildingInfo.m_class.m_service.ToString()))
                {
                    continue;
                }

                for (ushort segmentId = 1; segmentId < net.m_segments.m_buffer.Length; segmentId++)
                {
                    NetSegment segment = net.m_segments.m_buffer[segmentId];
                    if (!GameInfoHelpers.IsCreatedRoadSegment(segment))
                    {
                        continue;
                    }

                    Vector3 start = net.m_nodes.m_buffer[segment.m_startNode].m_position;
                    Vector3 end = net.m_nodes.m_buffer[segment.m_endNode].m_position;
                    if (RoadCrossesBuildingFootprint(building, buildingInfo, start, end))
                    {
                        AddRoadOverlap(buildingId, building, buildingInfo, segmentId);
                        break;
                    }
                }
            }
        }

        public string ToJson()
        {
            return "{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"counts\":{\"buildingRoadOverlap\":" + total + "}" +
                ",\"anomalies\":[" + items.ToString() + "]}";
        }

        private void AddRoadOverlap(ushort buildingId, Building building, BuildingInfo info, ushort segmentId)
        {
            total++;
            if (emitted >= limit)
            {
                return;
            }

            if (!firstItem)
            {
                items.Append(",");
            }

            Vector3 position = building.m_position;
            items.Append("{\"type\":\"buildingRoadOverlap\"");
            items.Append(",\"buildingId\":").Append(buildingId);
            items.Append(",\"segmentId\":").Append(segmentId);
            items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
            items.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
            items.Append(",\"service\":\"").Append(JsonUtil.Escape(info.m_class.m_service.ToString())).Append("\"");
            items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(position.x));
            items.Append(",\"y\":").Append(JsonUtil.Number(position.y));
            items.Append(",\"z\":").Append(JsonUtil.Number(position.z)).Append("}}");
            emitted++;
            firstItem = false;
        }

        private static bool RoadCrossesBuildingFootprint(Building building, BuildingInfo info, Vector3 roadStart, Vector3 roadEnd)
        {
            float halfWidth = Mathf.Max(1f, info.GetWidth() * 4f - 1.5f);
            float halfLength = Mathf.Max(1f, info.GetLength() * 4f - 1.5f);
            Vector2 start = ToLocal(building, roadStart);
            Vector2 end = ToLocal(building, roadEnd);

            return SegmentIntersectsAabb(start, end, -halfWidth, halfWidth, -halfLength, halfLength);
        }

        private static Vector2 ToLocal(Building building, Vector3 point)
        {
            float dx = point.x - building.m_position.x;
            float dz = point.z - building.m_position.z;
            float cos = Mathf.Cos(-building.m_angle);
            float sin = Mathf.Sin(-building.m_angle);
            return new Vector2(dx * cos - dz * sin, dx * sin + dz * cos);
        }

        private static bool SegmentIntersectsAabb(Vector2 start, Vector2 end, float minX, float maxX, float minY, float maxY)
        {
            float t0 = 0f;
            float t1 = 1f;
            float dx = end.x - start.x;
            float dy = end.y - start.y;

            return Clip(-dx, start.x - minX, ref t0, ref t1) &&
                Clip(dx, maxX - start.x, ref t0, ref t1) &&
                Clip(-dy, start.y - minY, ref t0, ref t1) &&
                Clip(dy, maxY - start.y, ref t0, ref t1);
        }

        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 0.0001f)
            {
                return q >= 0f;
            }

            float r = q / p;
            if (p < 0f)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }
}
