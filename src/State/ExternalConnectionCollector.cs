using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    internal sealed class ExternalConnectionCollector
    {
        private readonly int limit;
        private readonly StringBuilder components = new StringBuilder();
        private int roadComponents;
        private int emitted;
        private int outsideRoadNodes;
        private int outsideRoadSegments;
        private int localRoadComponents;
        private int disconnectedLocalRoadComponents;
        private int cityComponentId = -1;
        private bool cityConnectedToOutside;
        private bool firstComponent = true;

        public ExternalConnectionCollector(int limit)
        {
            this.limit = limit;
        }

        public void Collect()
        {
            NetManager manager = NetManager.instance;
            bool[] isRoadSegment = new bool[manager.m_segments.m_buffer.Length];
            bool[] visited = new bool[manager.m_segments.m_buffer.Length];

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                isRoadSegment[i] = GameInfoHelpers.IsCreatedRoadSegment(segment);
                if (isRoadSegment[i] && (IsOutsideNode(manager, segment.m_startNode) || IsOutsideNode(manager, segment.m_endNode)))
                {
                    outsideRoadSegments++;
                }
            }

            for (ushort i = 1; i < manager.m_nodes.m_buffer.Length; i++)
            {
                NetNode node = manager.m_nodes.m_buffer[i];
                if ((node.m_flags & NetNode.Flags.Created) != NetNode.Flags.None && IsOutsideNode(manager, i))
                {
                    outsideRoadNodes++;
                }
            }

            int bestCityScore = -1;
            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                if (!isRoadSegment[i] || visited[i])
                {
                    continue;
                }

                ComponentStats stats = CollectComponent(manager, i, isRoadSegment, visited);
                roadComponents++;
                stats.ComponentId = roadComponents;

                if (stats.IsLocalRoadComponent)
                {
                    localRoadComponents++;
                    if (!stats.OutsideConnected)
                    {
                        disconnectedLocalRoadComponents++;
                    }
                }

                int cityScore = stats.AgentNamedSegments * 4 + stats.NonHighwaySegments;
                if (cityScore > bestCityScore)
                {
                    bestCityScore = cityScore;
                    cityComponentId = stats.ComponentId;
                    cityConnectedToOutside = stats.OutsideConnected;
                }

                AddComponent(stats);
            }
        }

        public string ToJson()
        {
            return "{\"ok\":true" +
                ",\"roadComponents\":" + roadComponents +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"outsideRoadNodes\":" + outsideRoadNodes +
                ",\"outsideRoadSegments\":" + outsideRoadSegments +
                ",\"localRoadComponents\":" + localRoadComponents +
                ",\"disconnectedLocalRoadComponents\":" + disconnectedLocalRoadComponents +
                ",\"cityComponentId\":" + cityComponentId +
                ",\"cityConnectedToOutside\":" + JsonUtil.Bool(cityConnectedToOutside) +
                ",\"components\":[" + components.ToString() + "]}";
        }

        private ComponentStats CollectComponent(NetManager manager, ushort firstSegment, bool[] isRoadSegment, bool[] visited)
        {
            ComponentStats stats = new ComponentStats();
            Queue<ushort> queue = new Queue<ushort>();
            bool[] visitedNodes = new bool[manager.m_nodes.m_buffer.Length];
            queue.Enqueue(firstSegment);
            visited[firstSegment] = true;

            while (queue.Count > 0)
            {
                ushort segmentId = queue.Dequeue();
                NetSegment segment = manager.m_segments.m_buffer[segmentId];
                stats.SegmentCount++;
                stats.SampleSegmentId = stats.SampleSegmentId == 0 ? segmentId : stats.SampleSegmentId;

                string prefab = segment.Info == null || segment.Info.name == null ? "" : segment.Info.name;
                string name = manager.GetSegmentName(segmentId);
                if (stats.SampleName.Length == 0 && name.Length > 0)
                {
                    stats.SampleName = name;
                }
                if (StringUtil.ContainsIgnoreCase(name, "Agent"))
                {
                    stats.AgentNamedSegments++;
                }
                if (!StringUtil.ContainsIgnoreCase(prefab, "Highway"))
                {
                    stats.NonHighwaySegments++;
                }

                Vector3 middle = segment.m_middlePosition;
                stats.Center += middle;

                VisitNode(manager, segment.m_startNode, isRoadSegment, visited, visitedNodes, queue, stats);
                VisitNode(manager, segment.m_endNode, isRoadSegment, visited, visitedNodes, queue, stats);
            }

            if (stats.SegmentCount > 0)
            {
                stats.Center /= stats.SegmentCount;
            }
            stats.IsLocalRoadComponent = stats.AgentNamedSegments > 0 || stats.NonHighwaySegments > 0;
            return stats;
        }

        private void VisitNode(NetManager manager, ushort nodeId, bool[] isRoadSegment, bool[] visited, bool[] visitedNodes, Queue<ushort> queue, ComponentStats stats)
        {
            if (!visitedNodes[nodeId])
            {
                visitedNodes[nodeId] = true;
                if (IsOutsideNode(manager, nodeId))
                {
                    stats.OutsideNodes++;
                    stats.OutsideConnected = true;
                }
            }

            NetNode node = manager.m_nodes.m_buffer[nodeId];
            for (int i = 0; i < 8; i++)
            {
                ushort connectedSegment = GameInfoHelpers.GetSegmentId(node, i);
                if (connectedSegment == 0 || !isRoadSegment[connectedSegment] || visited[connectedSegment])
                {
                    continue;
                }

                visited[connectedSegment] = true;
                queue.Enqueue(connectedSegment);
            }
        }

        private void AddComponent(ComponentStats stats)
        {
            if (emitted >= limit)
            {
                return;
            }
            if (!firstComponent)
            {
                components.Append(",");
            }

            components.Append("{\"componentId\":").Append(stats.ComponentId);
            components.Append(",\"segmentCount\":").Append(stats.SegmentCount);
            components.Append(",\"agentNamedSegments\":").Append(stats.AgentNamedSegments);
            components.Append(",\"nonHighwaySegments\":").Append(stats.NonHighwaySegments);
            components.Append(",\"outsideNodes\":").Append(stats.OutsideNodes);
            components.Append(",\"outsideConnected\":").Append(JsonUtil.Bool(stats.OutsideConnected));
            components.Append(",\"isLocalRoadComponent\":").Append(JsonUtil.Bool(stats.IsLocalRoadComponent));
            components.Append(",\"sampleSegmentId\":").Append(stats.SampleSegmentId);
            components.Append(",\"sampleName\":\"").Append(JsonUtil.Escape(stats.SampleName)).Append("\"");
            components.Append(",\"center\":{\"x\":").Append(JsonUtil.Number(stats.Center.x));
            components.Append(",\"y\":").Append(JsonUtil.Number(stats.Center.y));
            components.Append(",\"z\":").Append(JsonUtil.Number(stats.Center.z)).Append("}}");

            emitted++;
            firstComponent = false;
        }
        private static bool IsOutsideNode(NetManager manager, ushort nodeId)
        {
            NetNode node = manager.m_nodes.m_buffer[nodeId];
            if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
            {
                return false;
            }

            string flags = node.m_flags.ToString();
            if (StringUtil.ContainsIgnoreCase(flags, "Outside"))
            {
                return true;
            }

            Vector3 position = node.m_position;
            return Mathf.Abs(position.x) >= 8600f || Mathf.Abs(position.z) >= 8600f;
        }
        internal sealed class ComponentStats
        {
            public int ComponentId;
            public int SegmentCount;
            public int AgentNamedSegments;
            public int NonHighwaySegments;
            public int OutsideNodes;
            public bool OutsideConnected;
            public bool IsLocalRoadComponent;
            public ushort SampleSegmentId;
            public string SampleName = "";
            public Vector3 Center = Vector3.zero;
        }
    }
}
