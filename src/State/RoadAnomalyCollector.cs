using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    internal sealed class RoadAnomalyCollector
    {
        private const float RoadOverlapDistance = 3f;
        private const float RoadCrossingHeightTolerance = 5f;
        private const float RoadOverlapMinLength = 18f;
        private const float RoadTerrainSideOffset = 22f;
        private const float RoadTerrainHeightTolerance = 18f;
        private const float RoadTerrainSideDeltaTolerance = 28f;
        private const float RoadTerrainAdjacentDeltaTolerance = 14f;
        private const float RoadTerrainBoundsMargin = 700f;
        private const float RoadBelowLocalGradeTolerance = 24f;
        private const float RoadOverlapGridSize = 96f;
        private readonly int limit;
        private readonly float nearMissDistance;
        private readonly float shortSegmentLength;
        private readonly bool includeDeadEnds;
        private readonly StringBuilder items = new StringBuilder();
        private readonly StringBuilder counts = new StringBuilder();
        private readonly Dictionary<string, int> countByType = new Dictionary<string, int>();
        private int total;
        private int emitted;
        private bool firstItem = true;

        public RoadAnomalyCollector(int limit, float nearMissDistance, float shortSegmentLength, bool includeDeadEnds)
        {
            this.limit = limit;
            this.nearMissDistance = nearMissDistance;
            this.shortSegmentLength = shortSegmentLength;
            this.includeDeadEnds = includeDeadEnds;
        }

        public void Collect()
        {
            NetManager manager = NetManager.instance;

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment))
                {
                    continue;
                }

                Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                float length = FlatDistance(start, end);
                int startConnections = CountRoadSegments(manager, segment.m_startNode);
                int endConnections = CountRoadSegments(manager, segment.m_endNode);

                if (length <= shortSegmentLength && (startConnections <= 1 || endConnections <= 1))
                {
                    AddShortSegment(i, segment, start, end, length, startConnections, endConnections);
                }
            }

            CollectRoadOverlapAnomalies(manager);
            CollectRoadTerrainAnomalies(manager);

            for (ushort i = 1; i < manager.m_nodes.m_buffer.Length; i++)
            {
                NetNode node = manager.m_nodes.m_buffer[i];
                if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None || !IsRoadInfo(node.Info))
                {
                    continue;
                }

                int connections = CountRoadSegments(manager, i);
                if (connections != 1)
                {
                    continue;
                }

                ushort ownSegment = FirstRoadSegment(manager, i);
                ushort nearestSegment;
                float distance;
                FindNearestRoadSegment(manager, i, node.m_position, ownSegment, out nearestSegment, out distance);
                if (nearestSegment != 0 && distance <= nearMissDistance)
                {
                    AddNearMissNode(i, ownSegment, nearestSegment, node.m_position, distance);
                }
                else if (includeDeadEnds)
                {
                    AddDeadEndNode(i, ownSegment, node.m_position);
                }
            }
        }

        public string ToJson()
        {
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
                ",\"nearMissDistance\":" + JsonUtil.Number(nearMissDistance) +
                ",\"shortSegmentLength\":" + JsonUtil.Number(shortSegmentLength) +
                ",\"includeDeadEnds\":" + JsonUtil.Bool(includeDeadEnds) +
                ",\"counts\":{" + counts.ToString() + "}" +
                ",\"anomalies\":[" + items.ToString() + "]}";
        }

        private void AddShortSegment(ushort segmentId, NetSegment segment, Vector3 start, Vector3 end, float length, int startConnections, int endConnections)
        {
            if (Begin("shortRoadStub"))
            {
                items.Append(",\"segmentId\":").Append(segmentId);
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(segment.Info.name)).Append("\"");
                items.Append(",\"length\":").Append(JsonUtil.Number(length));
                items.Append(",\"startConnections\":").Append(startConnections);
                items.Append(",\"endConnections\":").Append(endConnections);
                AppendPoint("start", start);
                AppendPoint("end", end);
                items.Append("}");
            }
        }

        private void AddNearMissNode(ushort nodeId, ushort ownSegment, ushort nearestSegment, Vector3 position, float distance)
        {
            if (Begin("deadEndNearRoad"))
            {
                items.Append(",\"nodeId\":").Append(nodeId);
                items.Append(",\"ownSegmentId\":").Append(ownSegment);
                items.Append(",\"nearestSegmentId\":").Append(nearestSegment);
                items.Append(",\"distance\":").Append(JsonUtil.Number(distance));
                AppendPoint("position", position);
                items.Append("}");
            }
        }

        private void AddDeadEndNode(ushort nodeId, ushort ownSegment, Vector3 position)
        {
            if (Begin("deadEndRoad"))
            {
                items.Append(",\"nodeId\":").Append(nodeId);
                items.Append(",\"ownSegmentId\":").Append(ownSegment);
                AppendPoint("position", position);
                items.Append("}");
            }
        }

        private void AddOverlappingRoadSegments(ushort segmentAId, ushort segmentBId, NetSegment segmentA, NetSegment segmentB, float distance, float overlapLength)
        {
            if (Begin("overlappingRoadSegments"))
            {
                items.Append(",\"segmentAId\":").Append(segmentAId);
                items.Append(",\"segmentBId\":").Append(segmentBId);
                items.Append(",\"prefabA\":\"").Append(JsonUtil.Escape(segmentA.Info.name)).Append("\"");
                items.Append(",\"prefabB\":\"").Append(JsonUtil.Escape(segmentB.Info.name)).Append("\"");
                items.Append(",\"distance\":").Append(JsonUtil.Number(distance));
                items.Append(",\"overlapLength\":").Append(JsonUtil.Number(overlapLength));
                AppendSegment("segmentA", segmentA);
                AppendSegment("segmentB", segmentB);
                items.Append("}");
            }
        }

        private void AddCrossingRoadWithoutNode(ushort segmentAId, ushort segmentBId, NetSegment segmentA, NetSegment segmentB, Vector3 position, float heightDifference)
        {
            if (Begin("roadCrossingWithoutNode"))
            {
                items.Append(",\"segmentAId\":").Append(segmentAId);
                items.Append(",\"segmentBId\":").Append(segmentBId);
                items.Append(",\"prefabA\":\"").Append(JsonUtil.Escape(segmentA.Info.name)).Append("\"");
                items.Append(",\"prefabB\":\"").Append(JsonUtil.Escape(segmentB.Info.name)).Append("\"");
                items.Append(",\"heightDifference\":").Append(JsonUtil.Number(heightDifference));
                AppendPoint("position", position);
                AppendSegment("segmentA", segmentA);
                AppendSegment("segmentB", segmentB);
                items.Append("}");
            }
        }

        private void AddDuplicateRoadSegments(ushort segmentAId, ushort segmentBId, NetSegment segmentA, NetSegment segmentB)
        {
            if (Begin("duplicateRoadSegments"))
            {
                items.Append(",\"segmentAId\":").Append(segmentAId);
                items.Append(",\"segmentBId\":").Append(segmentBId);
                items.Append(",\"prefabA\":\"").Append(JsonUtil.Escape(segmentA.Info.name)).Append("\"");
                items.Append(",\"prefabB\":\"").Append(JsonUtil.Escape(segmentB.Info.name)).Append("\"");
                AppendSegment("segmentA", segmentA);
                AppendSegment("segmentB", segmentB);
                items.Append("}");
            }
        }

        private void AddRoadTerrainCliff(ushort segmentId, NetSegment segment, float maxRoadToTerrainDelta, float maxSideToSideDelta, float maxAdjacentTerrainDelta, Vector3 samplePosition)
        {
            if (Begin("roadTerrainCliff"))
            {
                items.Append(",\"segmentId\":").Append(segmentId);
                items.Append(",\"name\":\"").Append(JsonUtil.Escape(NetManager.instance.GetSegmentName(segmentId))).Append("\"");
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(segment.Info.name)).Append("\"");
                items.Append(",\"maxRoadToTerrainDelta\":").Append(JsonUtil.Number(maxRoadToTerrainDelta));
                items.Append(",\"maxSideToSideDelta\":").Append(JsonUtil.Number(maxSideToSideDelta));
                items.Append(",\"maxAdjacentTerrainDelta\":").Append(JsonUtil.Number(maxAdjacentTerrainDelta));
                AppendPoint("samplePosition", samplePosition);
                AppendSegment("segment", segment);
                items.Append("}");
            }
        }

        private void AddRoadBelowLocalGrade(ushort segmentId, NetSegment segment, float localGradeY, float roadY)
        {
            if (Begin("roadBelowLocalGrade"))
            {
                items.Append(",\"segmentId\":").Append(segmentId);
                items.Append(",\"name\":\"").Append(JsonUtil.Escape(NetManager.instance.GetSegmentName(segmentId))).Append("\"");
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(segment.Info.name)).Append("\"");
                items.Append(",\"localGradeY\":").Append(JsonUtil.Number(localGradeY));
                items.Append(",\"roadY\":").Append(JsonUtil.Number(roadY));
                items.Append(",\"belowBy\":").Append(JsonUtil.Number(localGradeY - roadY));
                AppendSegment("segment", segment);
                items.Append("}");
            }
        }

        private bool Begin(string type)
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
                return false;
            }

            if (!firstItem)
            {
                items.Append(",");
            }
            items.Append("{\"type\":\"").Append(type).Append("\"");
            emitted++;
            firstItem = false;
            return true;
        }

        private void AppendPoint(string name, Vector3 point)
        {
            items.Append(",\"").Append(name).Append("\":{\"x\":").Append(JsonUtil.Number(point.x));
            items.Append(",\"y\":").Append(JsonUtil.Number(point.y));
            items.Append(",\"z\":").Append(JsonUtil.Number(point.z)).Append("}");
        }

        private void AppendSegment(string name, NetSegment segment)
        {
            NetManager manager = NetManager.instance;
            Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
            Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
            items.Append(",\"").Append(name).Append("\":{");
            items.Append("\"startNodeId\":").Append(segment.m_startNode);
            items.Append(",\"endNodeId\":").Append(segment.m_endNode);
            AppendPoint("start", start);
            AppendPoint("end", end);
            items.Append("}");
        }

        private void CollectRoadOverlapAnomalies(NetManager manager)
        {
            List<RoadSegmentCandidate> segments = new List<RoadSegmentCandidate>();
            Dictionary<long, List<int>> grid = new Dictionary<long, List<int>>();

            for (ushort id = 1; id < manager.m_segments.m_buffer.Length; id++)
            {
                NetSegment segment = manager.m_segments.m_buffer[id];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment))
                {
                    continue;
                }

                RoadSegmentCandidate candidate = new RoadSegmentCandidate(id, segment, manager);
                int index = segments.Count;
                segments.Add(candidate);
                AddToOverlapGrid(grid, candidate, index);
            }

            HashSet<long> checkedPairs = new HashSet<long>();
            for (int a = 0; a < segments.Count; a++)
            {
                RoadSegmentCandidate segmentA = segments[a];
                int minX = GridIndex(segmentA.MinX);
                int maxX = GridIndex(segmentA.MaxX);
                int minZ = GridIndex(segmentA.MinZ);
                int maxZ = GridIndex(segmentA.MaxZ);

                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        List<int> bucket;
                        if (!grid.TryGetValue(CellKey(x, z), out bucket))
                        {
                            continue;
                        }

                        for (int i = 0; i < bucket.Count; i++)
                        {
                            int b = bucket[i];
                            if (b <= a)
                            {
                                continue;
                            }

                            RoadSegmentCandidate segmentB = segments[b];
                            if (!BoundsOverlap(segmentA, segmentB))
                            {
                                continue;
                            }

                            long pairKey = PairKey(segmentA.Id, segmentB.Id);
                            if (checkedPairs.Contains(pairKey))
                            {
                                continue;
                            }
                            checkedPairs.Add(pairKey);

                            AddRoadPairAnomaly(segmentA, segmentB);
                        }
                    }
                }
            }
        }

        private void AddRoadPairAnomaly(RoadSegmentCandidate segmentA, RoadSegmentCandidate segmentB)
        {
            if (SameEndpoints(segmentA.Segment, segmentB.Segment))
            {
                AddDuplicateRoadSegments(segmentA.Id, segmentB.Id, segmentA.Segment, segmentB.Segment);
                return;
            }

            if (SharesNode(segmentA.Segment, segmentB.Segment))
            {
                return;
            }

            Vector3 crossing;
            float heightDifference;
            if (SegmentsCrossWithoutNode(segmentA.Start, segmentA.End, segmentB.Start, segmentB.End, out crossing, out heightDifference))
            {
                AddCrossingRoadWithoutNode(segmentA.Id, segmentB.Id, segmentA.Segment, segmentB.Segment, crossing, heightDifference);
                return;
            }

            float distance;
            float overlapLength;
            if (SegmentsOverlap(segmentA.Start, segmentA.End, segmentB.Start, segmentB.End, out distance, out overlapLength))
            {
                AddOverlappingRoadSegments(segmentA.Id, segmentB.Id, segmentA.Segment, segmentB.Segment, distance, overlapLength);
            }
        }

        private static void AddToOverlapGrid(Dictionary<long, List<int>> grid, RoadSegmentCandidate segment, int index)
        {
            int minX = GridIndex(segment.MinX);
            int maxX = GridIndex(segment.MaxX);
            int minZ = GridIndex(segment.MinZ);
            int maxZ = GridIndex(segment.MaxZ);

            for (int x = minX; x <= maxX; x++)
            {
                for (int z = minZ; z <= maxZ; z++)
                {
                    long key = CellKey(x, z);
                    List<int> bucket;
                    if (!grid.TryGetValue(key, out bucket))
                    {
                        bucket = new List<int>();
                        grid[key] = bucket;
                    }
                    bucket.Add(index);
                }
            }
        }

        private static bool BoundsOverlap(RoadSegmentCandidate a, RoadSegmentCandidate b)
        {
            return a.MaxX >= b.MinX && a.MinX <= b.MaxX &&
                a.MaxZ >= b.MinZ && a.MinZ <= b.MaxZ;
        }

        private static int GridIndex(float value)
        {
            return Mathf.FloorToInt(value / RoadOverlapGridSize);
        }

        private static long CellKey(int x, int z)
        {
            return ((long)x << 32) ^ (uint)z;
        }

        private static long PairKey(ushort a, ushort b)
        {
            ushort min = a < b ? a : b;
            ushort max = a < b ? b : a;
            return ((long)min << 32) | (uint)max;
        }

        private void CollectRoadTerrainAnomalies(NetManager manager)
        {
            TerrainManager terrain = TerrainManager.instance;
            if (terrain == null)
            {
                return;
            }

            float minX;
            float maxX;
            float minZ;
            float maxZ;
            if (!TryGetTerrainCheckBounds(manager, out minX, out maxX, out minZ, out maxZ))
            {
                return;
            }

            float localGradeY = EstimateAgentRoadGrade(manager);

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment) || SkipTerrainAnomalyCheck(segment.Info))
                {
                    continue;
                }

                Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                float averageRoadY = (start.y + end.y) * 0.5f;
                if (localGradeY > 0f && IsAgentNamedRoad(i) && localGradeY - averageRoadY >= RoadBelowLocalGradeTolerance)
                {
                    AddRoadBelowLocalGrade(i, segment, localGradeY, averageRoadY);
                }

                if (!IsAgentNamedRoad(i) && !SegmentTouchesBounds(start, end, minX, maxX, minZ, maxZ))
                {
                    continue;
                }

                Vector3 direction = end - start;
                direction.y = 0f;
                float length = Mathf.Sqrt(direction.x * direction.x + direction.z * direction.z);
                if (length < 24f)
                {
                    continue;
                }

                direction.x /= length;
                direction.z /= length;
                Vector3 side = new Vector3(-direction.z, 0f, direction.x);

                float maxRoadToTerrainDelta = 0f;
                float maxSideToSideDelta = 0f;
                float maxAdjacentTerrainDelta = 0f;
                Vector3 worstPoint = segment.m_middlePosition;

                for (int sample = 1; sample <= 3; sample++)
                {
                    float t = sample * 0.25f;
                    Vector3 center = Vector3.Lerp(start, end, t);
                    float roadY = Mathf.Lerp(start.y, end.y, t);
                    float leftY = SampleRoadSideTerrain(terrain, center, side, 1f, roadY, ref maxRoadToTerrainDelta, ref maxAdjacentTerrainDelta);
                    float rightY = SampleRoadSideTerrain(terrain, center, side, -1f, roadY, ref maxRoadToTerrainDelta, ref maxAdjacentTerrainDelta);
                    float sideDelta = Mathf.Abs(leftY - rightY);

                    if (sideDelta > maxSideToSideDelta)
                    {
                        maxSideToSideDelta = sideDelta;
                        worstPoint = center;
                    }
                }

                if (IsLongGroundOutsideConnector(i, segment, length))
                {
                    maxRoadToTerrainDelta = Mathf.Max(maxRoadToTerrainDelta, RoadTerrainHeightTolerance);
                }

                if (maxRoadToTerrainDelta >= RoadTerrainHeightTolerance ||
                    maxSideToSideDelta >= RoadTerrainSideDeltaTolerance ||
                    maxAdjacentTerrainDelta >= RoadTerrainAdjacentDeltaTolerance)
                {
                    AddRoadTerrainCliff(i, segment, maxRoadToTerrainDelta, maxSideToSideDelta, maxAdjacentTerrainDelta, worstPoint);
                }
            }
        }

        private static float EstimateAgentRoadGrade(NetManager manager)
        {
            float total = 0f;
            int count = 0;
            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment) || !IsAgentNamedRoad(i) || SkipTerrainAnomalyCheck(segment.Info))
                {
                    continue;
                }

                string name = NetManager.instance.GetSegmentName(i);
                if (StringUtil.ContainsIgnoreCase(name, "Outside") || StringUtil.ContainsIgnoreCase(name, "Highway Connector"))
                {
                    continue;
                }

                Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                total += (start.y + end.y) * 0.5f;
                count++;
            }

            return count == 0 ? 0f : total / count;
        }

        private static float SampleRoadSideTerrain(TerrainManager terrain, Vector3 center, Vector3 side, float sideSign, float roadY, ref float maxRoadToTerrainDelta, ref float maxAdjacentTerrainDelta)
        {
            float previousY = roadY;
            float lastY = roadY;
            float[] offsets = new float[] { 8f, RoadTerrainSideOffset, 44f, 66f, 96f };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 sample = center + side * (offsets[i] * sideSign);
                float terrainY = terrain.SampleRawHeightSmoothWithWater(sample, false, 0f);
                maxRoadToTerrainDelta = Mathf.Max(maxRoadToTerrainDelta, Mathf.Abs(roadY - terrainY));
                maxAdjacentTerrainDelta = Mathf.Max(maxAdjacentTerrainDelta, Mathf.Abs(previousY - terrainY));
                previousY = terrainY;
                lastY = terrainY;
            }
            return lastY;
        }

        private static bool TryGetTerrainCheckBounds(NetManager manager, out float minX, out float maxX, out float minZ, out float maxZ)
        {
            minX = float.MaxValue;
            maxX = float.MinValue;
            minZ = float.MaxValue;
            maxZ = float.MinValue;
            bool found = false;

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment) || !ContributesToTerrainCheckBounds(i, segment))
                {
                    continue;
                }

                Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                ExpandBounds(start, ref minX, ref maxX, ref minZ, ref maxZ);
                ExpandBounds(end, ref minX, ref maxX, ref minZ, ref maxZ);
                found = true;
            }

            if (!found)
            {
                return false;
            }

            minX -= RoadTerrainBoundsMargin;
            maxX += RoadTerrainBoundsMargin;
            minZ -= RoadTerrainBoundsMargin;
            maxZ += RoadTerrainBoundsMargin;
            return true;
        }

        private static void ExpandBounds(Vector3 point, ref float minX, ref float maxX, ref float minZ, ref float maxZ)
        {
            minX = Mathf.Min(minX, point.x);
            maxX = Mathf.Max(maxX, point.x);
            minZ = Mathf.Min(minZ, point.z);
            maxZ = Mathf.Max(maxZ, point.z);
        }

        private static bool SegmentTouchesBounds(Vector3 start, Vector3 end, float minX, float maxX, float minZ, float maxZ)
        {
            float segmentMinX = Mathf.Min(start.x, end.x);
            float segmentMaxX = Mathf.Max(start.x, end.x);
            float segmentMinZ = Mathf.Min(start.z, end.z);
            float segmentMaxZ = Mathf.Max(start.z, end.z);
            return segmentMaxX >= minX && segmentMinX <= maxX && segmentMaxZ >= minZ && segmentMinZ <= maxZ;
        }

        private static bool ContributesToTerrainCheckBounds(ushort segmentId, NetSegment segment)
        {
            if (IsAgentNamedRoad(segmentId))
            {
                return true;
            }

            string prefab = segment.Info == null || segment.Info.name == null ? "" : segment.Info.name;
            return !StringUtil.ContainsIgnoreCase(prefab, "Highway");
        }

        private static bool IsAgentNamedRoad(ushort segmentId)
        {
            string name = NetManager.instance.GetSegmentName(segmentId);
            return StringUtil.ContainsIgnoreCase(name, "Agent");
        }

        private static bool IsLongGroundOutsideConnector(ushort segmentId, NetSegment segment, float length)
        {
            if (length < 140f || segment.Info == null)
            {
                return false;
            }

            string prefab = segment.Info.name == null ? "" : segment.Info.name;
            if (StringUtil.ContainsIgnoreCase(prefab, "Elevated") || StringUtil.ContainsIgnoreCase(prefab, "Bridge") || StringUtil.ContainsIgnoreCase(prefab, "Tunnel") || StringUtil.ContainsIgnoreCase(prefab, "Slope"))
            {
                return false;
            }

            string name = NetManager.instance.GetSegmentName(segmentId);
            return StringUtil.ContainsIgnoreCase(name, "Agent") && StringUtil.ContainsIgnoreCase(name, "Outside");
        }

        private static void FindNearestRoadSegment(NetManager manager, ushort nodeId, Vector3 point, ushort ownSegment, out ushort nearestSegment, out float distance)
        {
            nearestSegment = 0;
            distance = float.MaxValue;

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                if (i == ownSegment)
                {
                    continue;
                }

                NetSegment segment = manager.m_segments.m_buffer[i];
                if (!GameInfoHelpers.IsCreatedRoadSegment(segment) || segment.m_startNode == nodeId || segment.m_endNode == nodeId)
                {
                    continue;
                }

                Vector3 start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                Vector3 end = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                float candidate = DistancePointToSegment(point, start, end);
                if (candidate < distance)
                {
                    distance = candidate;
                    nearestSegment = i;
                }
            }
        }

        private static float DistancePointToSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            float dx = end.x - start.x;
            float dz = end.z - start.z;
            float lengthSq = dx * dx + dz * dz;
            if (lengthSq < 0.001f)
            {
                return FlatDistance(point, start);
            }

            float t = ((point.x - start.x) * dx + (point.z - start.z) * dz) / lengthSq;
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;

            Vector3 nearest = new Vector3(start.x + t * dx, 0f, start.z + t * dz);
            return FlatDistance(point, nearest);
        }

        private static bool SegmentsCrossWithoutNode(Vector3 aStart, Vector3 aEnd, Vector3 bStart, Vector3 bEnd, out Vector3 crossing, out float heightDifference)
        {
            crossing = Vector3.zero;
            heightDifference = 0f;

            float ax = aEnd.x - aStart.x;
            float az = aEnd.z - aStart.z;
            float bx = bEnd.x - bStart.x;
            float bz = bEnd.z - bStart.z;
            float denom = ax * bz - az * bx;
            if (Mathf.Abs(denom) < 0.0001f)
            {
                return false;
            }

            float cx = bStart.x - aStart.x;
            float cz = bStart.z - aStart.z;
            float t = (cx * bz - cz * bx) / denom;
            float u = (cx * az - cz * ax) / denom;
            if (t <= 0.04f || t >= 0.96f || u <= 0.04f || u >= 0.96f)
            {
                return false;
            }

            float aY = Mathf.Lerp(aStart.y, aEnd.y, t);
            float bY = Mathf.Lerp(bStart.y, bEnd.y, u);
            heightDifference = Mathf.Abs(aY - bY);
            if (heightDifference > RoadCrossingHeightTolerance)
            {
                return false;
            }

            crossing = new Vector3(aStart.x + ax * t, (aY + bY) * 0.5f, aStart.z + az * t);
            return true;
        }

        private static bool SegmentsOverlap(Vector3 aStart, Vector3 aEnd, Vector3 bStart, Vector3 bEnd, out float distance, out float overlapLength)
        {
            distance = Mathf.Min(
                Mathf.Min(DistancePointToSegment(aStart, bStart, bEnd), DistancePointToSegment(aEnd, bStart, bEnd)),
                Mathf.Min(DistancePointToSegment(bStart, aStart, aEnd), DistancePointToSegment(bEnd, aStart, aEnd)));
            overlapLength = 0f;

            if (distance > RoadOverlapDistance)
            {
                return false;
            }

            float aDx = aEnd.x - aStart.x;
            float aDz = aEnd.z - aStart.z;
            float bDx = bEnd.x - bStart.x;
            float bDz = bEnd.z - bStart.z;
            float aLength = Mathf.Sqrt(aDx * aDx + aDz * aDz);
            float bLength = Mathf.Sqrt(bDx * bDx + bDz * bDz);
            if (aLength < 0.001f || bLength < 0.001f)
            {
                return false;
            }

            float dot = Mathf.Abs((aDx * bDx + aDz * bDz) / (aLength * bLength));
            if (dot < 0.985f)
            {
                return false;
            }

            float b0 = ProjectAlong(aStart, aEnd, bStart);
            float b1 = ProjectAlong(aStart, aEnd, bEnd);
            if (b0 > b1)
            {
                float swap = b0;
                b0 = b1;
                b1 = swap;
            }

            float overlapStart = Mathf.Max(0f, b0);
            float overlapEnd = Mathf.Min(aLength, b1);
            overlapLength = overlapEnd - overlapStart;
            if (overlapLength < RoadOverlapMinLength)
            {
                return false;
            }

            float maxHeightDifference = Mathf.Max(
                Mathf.Abs(aStart.y - bStart.y),
                Mathf.Abs(aEnd.y - bEnd.y));
            return maxHeightDifference <= RoadCrossingHeightTolerance;
        }

        private static float ProjectAlong(Vector3 start, Vector3 end, Vector3 point)
        {
            float dx = end.x - start.x;
            float dz = end.z - start.z;
            float length = Mathf.Sqrt(dx * dx + dz * dz);
            if (length < 0.001f)
            {
                return 0f;
            }
            return ((point.x - start.x) * dx + (point.z - start.z) * dz) / length;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static int CountRoadSegments(NetManager manager, ushort nodeId)
        {
            int count = 0;
            for (int i = 0; i < 8; i++)
            {
                ushort segmentId = GameInfoHelpers.GetSegmentId(manager.m_nodes.m_buffer[nodeId], i);
                if (segmentId == 0)
                {
                    continue;
                }
                if (GameInfoHelpers.IsCreatedRoadSegment(manager.m_segments.m_buffer[segmentId]))
                {
                    count++;
                }
            }
            return count;
        }

        private static ushort FirstRoadSegment(NetManager manager, ushort nodeId)
        {
            for (int i = 0; i < 8; i++)
            {
                ushort segmentId = GameInfoHelpers.GetSegmentId(manager.m_nodes.m_buffer[nodeId], i);
                if (segmentId != 0 && GameInfoHelpers.IsCreatedRoadSegment(manager.m_segments.m_buffer[segmentId]))
                {
                    return segmentId;
                }
            }
            return 0;
        }

        private static bool SharesNode(NetSegment a, NetSegment b)
        {
            return a.m_startNode == b.m_startNode ||
                a.m_startNode == b.m_endNode ||
                a.m_endNode == b.m_startNode ||
                a.m_endNode == b.m_endNode;
        }

        private static bool SameEndpoints(NetSegment a, NetSegment b)
        {
            return (a.m_startNode == b.m_startNode && a.m_endNode == b.m_endNode) ||
                (a.m_startNode == b.m_endNode && a.m_endNode == b.m_startNode);
        }

        private static bool SkipTerrainAnomalyCheck(NetInfo info)
        {
            if (info == null)
            {
                return true;
            }

            string name = info.name == null ? "" : info.name;
            string aiName = info.m_netAI == null ? "" : info.m_netAI.GetType().Name;
            return StringUtil.ContainsIgnoreCase(name, "Elevated") ||
                StringUtil.ContainsIgnoreCase(name, "Bridge") ||
                StringUtil.ContainsIgnoreCase(name, "Tunnel") ||
                StringUtil.ContainsIgnoreCase(name, "Slope") ||
                StringUtil.ContainsIgnoreCase(aiName, "Elevated") ||
                StringUtil.ContainsIgnoreCase(aiName, "Bridge") ||
                StringUtil.ContainsIgnoreCase(aiName, "Tunnel");
        }
        private static bool IsRoadInfo(NetInfo info)
        {
            return info != null && info.m_class != null && info.m_class.m_service == ItemClass.Service.Road;
        }

        internal struct RoadSegmentCandidate
        {
            public readonly ushort Id;
            public readonly NetSegment Segment;
            public readonly Vector3 Start;
            public readonly Vector3 End;
            public readonly float MinX;
            public readonly float MaxX;
            public readonly float MinZ;
            public readonly float MaxZ;

            public RoadSegmentCandidate(ushort id, NetSegment segment, NetManager manager)
            {
                Id = id;
                Segment = segment;
                Start = manager.m_nodes.m_buffer[segment.m_startNode].m_position;
                End = manager.m_nodes.m_buffer[segment.m_endNode].m_position;
                MinX = Mathf.Min(Start.x, End.x) - RoadOverlapDistance;
                MaxX = Mathf.Max(Start.x, End.x) + RoadOverlapDistance;
                MinZ = Mathf.Min(Start.z, End.z) - RoadOverlapDistance;
                MaxZ = Mathf.Max(Start.z, End.z) + RoadOverlapDistance;
            }
        }
    }
}
