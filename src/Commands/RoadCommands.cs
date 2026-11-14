using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Builds roads (and any other network: pipes, power lines, paths) through the game's
    /// own <c>NetTool.CreateNode</c> — the exact static method the road tool calls when the
    /// player clicks. That gives the agent everything the player gets for free:
    ///
    ///   * endpoints snap onto existing nodes, or split an existing segment to make a
    ///     T-junction (the endpoint lies on that segment);
    ///   * crossings with existing roads become real intersections (shared nodes);
    ///   * long roads are subdivided the way the tool subdivides them;
    ///   * collisions, steep slopes, locked map tiles and money are checked, and the
    ///     construction cost is charged.
    ///
    /// The call is made twice, exactly like <c>NetTool.CreateNodeImpl</c>: first with
    /// <c>test: true</c> (no side effects; returns the errors and the cost), then — only if
    /// that passed and this is not a dry run — for real.
    /// </summary>
    public static class RoadCommands
    {
        /// <summary>Default radius within which an endpoint snaps onto an existing node or segment.</summary>
        private const float DefaultSnapRadius = 8f;

        private static readonly FastList<NetTool.NodePosition> NodeBuffer = new FastList<NetTool.NodePosition>();

        public static CommandResult BuildRoad(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            string prefabName = JsonUtil.GetString(body, "roadPrefab", "Basic Road");
            string name = JsonUtil.GetString(body, "name", "");
            float snapRadius = Mathf.Clamp(JsonUtil.GetNumber(body, "snapRadius", DefaultSnapRadius), 0f, 32f);
            float elevation = JsonUtil.GetNumber(body, "elevation", 0f);
            bool allowWater = JsonUtil.GetBool(body, "allowWater", false);
            bool autoSplit = JsonUtil.GetBool(body, "autoSplit", true);

            Vector3 start = CommandInput.ReadPoint(body, "start");
            Vector3 end = CommandInput.ReadPoint(body, "end");
            bool curved = body != null && body.Contains("\"control\"");
            Vector3 control = curved ? CommandInput.ReadPoint(body, "control") : Vector3.zero;

            NetInfo info = PrefabCollection<NetInfo>.FindLoaded(prefabName);
            if (info == null)
            {
                return CommandResult.Fail("Network prefab was not found: " + prefabName + ". List them with GET /prefabs/networks.");
            }

            // A straight road that runs over an existing node must stop at it, exactly as a
            // player clicks on that node: NetTool reports ObjectCollision for a road drawn
            // across a dead end. Split the request into legs at every such node.
            List<Vector3> waypoints = new List<Vector3>();
            waypoints.Add(start);
            if (!curved && autoSplit)
            {
                waypoints.AddRange(NodesAlong(info, start, end, snapRadius));
            }
            waypoints.Add(end);

            bool needMoney = GameValidation.NeedMoney();
            int legs = waypoints.Count - 1;
            ToolBase.ToolErrors allErrors = ToolBase.ToolErrors.None;
            int totalCost = 0;
            float totalUnderwater = 0f;
            StringBuilder legJson = new StringBuilder();

            // Phase 1: test every leg (no side effects), like NetTool.CreateNodeImpl does.
            for (int i = 0; i < legs; i++)
            {
                Leg leg = MakeLeg(info, waypoints[i], waypoints[i + 1], curved, control, elevation, snapRadius);
                if (leg == null)
                {
                    return CommandResult.Fail("Leg " + i + ": start and end are the same point.");
                }
                ushort node;
                ushort segment;
                int cost;
                int productionRate;
                ToolBase.ToolErrors errors = NetTool.CreateNode(
                    info, leg.Start, leg.Middle, leg.End, NodeBuffer, 1000,
                    true, false, true, needMoney, false, false, 0,
                    out node, out segment, out cost, out productionRate);

                // The game happily builds a ground-level road on the sea bed (it is then
                // simply submerged), so NetTool reports no error for it. An agent never wants
                // that by accident: refuse it unless the road is raised (elevation >= 4 m
                // builds the bridge variant) or the caller explicitly allows it.
                float underwater = elevation < 4f && !allowWater && IsSurfaceNetwork(info) ? UnderwaterLength(leg) : 0f;

                allErrors |= errors;
                totalCost += cost;
                totalUnderwater += underwater;
                if (i > 0) legJson.Append(',');
                legJson.Append("{\"leg\":").Append(i)
                    .Append(",\"errors\":").Append(GameValidation.ErrorsJson(errors))
                    .Append(",\"underwaterMeters\":").Append(Mathf.RoundToInt(underwater))
                    .Append(',').Append(SnapJson("start", leg.Start))
                    .Append(',').Append(SnapJson("end", leg.End)).Append('}');
            }

            bool wet = totalUnderwater >= 4f;
            bool passed = allErrors == ToolBase.ToolErrors.None && !wet;
            if (!passed || dryRun)
            {
                string errorsJson = GameValidation.ErrorsJson(allErrors);
                string hint = GameValidation.Hint(allErrors);
                if (wet)
                {
                    errorsJson = errorsJson.Length > 2
                        ? errorsJson.Substring(0, errorsJson.Length - 1) + ",\"Underwater\"]"
                        : "[\"Underwater\"]";
                    if (hint.Length == 0)
                    {
                        hint = "About " + Mathf.RoundToInt(totalUnderwater) + " m of this road would lie under water. Route it over land (check /state/map), raise it with elevation >= 4 to build a bridge, or pass allowWater: true.";
                    }
                }
                return CommandResult.FromJson("{\"ok\":" + JsonUtil.Bool(passed) +
                    ",\"dryRun\":" + JsonUtil.Bool(dryRun) +
                    ",\"built\":false" +
                    ",\"roadPrefab\":\"" + JsonUtil.Escape(prefabName) + "\"" +
                    ",\"cost\":" + (totalCost / 100) +
                    ",\"underwaterMeters\":" + Mathf.RoundToInt(totalUnderwater) +
                    ",\"errors\":" + errorsJson +
                    ",\"hint\":\"" + JsonUtil.Escape(hint) + "\"" +
                    ",\"legs\":[" + legJson + "]}");
            }

            // Phase 2: build. Control points are re-made per leg, so leg i+1 snaps onto the
            // node leg i just created.
            NetManager net = Singleton<NetManager>.instance;
            uint buildIndexBefore = Singleton<SimulationManager>.instance.m_currentBuildIndex;
            int moneyBefore = MoneyNow();
            ushort lastNode = 0;
            for (int i = 0; i < legs; i++)
            {
                Leg leg = MakeLeg(info, waypoints[i], waypoints[i + 1], curved, control, elevation, snapRadius);
                ushort segment;
                int cost;
                int productionRate;
                ToolBase.ToolErrors errors = NetTool.CreateNode(
                    info, leg.Start, leg.Middle, leg.End, NodeBuffer, 1000,
                    false, false, true, needMoney, false, false, 0,
                    out lastNode, out segment, out cost, out productionRate);
                if (errors != ToolBase.ToolErrors.None)
                {
                    return CommandResult.FromJson("{\"ok\":false,\"built\":" + JsonUtil.Bool(i > 0) +
                        ",\"failedLeg\":" + i + "," + GameValidation.ErrorFields(errors) +
                        ",\"segmentIds\":" + IdsJson(NewSegments(buildIndexBefore)) + "}");
                }
            }

            List<ushort> created = NewSegments(buildIndexBefore);
            if (name != null && name.Length > 0)
            {
                for (int i = 0; i < created.Count; i++)
                {
                    net.SetSegmentNameImpl(created[i], name);
                }
            }

            Debug.Log("[SkylinesAgentBridge] Built " + prefabName + ": " + created.Count + " segment(s) in " + legs + " leg(s), cost " + (totalCost / 100));

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":false,\"built\":true" +
                ",\"roadPrefab\":\"" + JsonUtil.Escape(prefabName) + "\"" +
                ",\"segmentIds\":" + IdsJson(created) +
                ",\"endNodeId\":" + lastNode +
                ",\"cost\":" + (totalCost / 100) +
                ",\"moneyBefore\":" + moneyBefore +
                ",\"moneyAfter\":" + MoneyNow() +
                ",\"legs\":[" + legJson + "]}");
        }

        private sealed class Leg
        {
            public NetTool.ControlPoint Start;
            public NetTool.ControlPoint Middle;
            public NetTool.ControlPoint End;
        }

        private static Leg MakeLeg(NetInfo info, Vector3 start, Vector3 end, bool curved, Vector3 control, float elevation, float snapRadius)
        {
            Leg leg = new Leg();
            leg.Start = MakeControlPoint(info, start, elevation, snapRadius, 0);
            leg.End = MakeControlPoint(info, end, elevation, snapRadius, leg.Start.m_node);

            // The middle point never snaps: it only shapes the curve (straight = midpoint).
            leg.Middle = new NetTool.ControlPoint();
            leg.Middle.m_position = curved ? control : (leg.Start.m_position + leg.End.m_position) * 0.5f;
            leg.Middle.m_elevation = elevation;
            leg.Middle.m_position.y = NetSegment.SampleTerrainHeight(info, leg.Middle.m_position, false, elevation);

            // Directions exactly as NetTool.SimulationStep sets them: each point's direction
            // is the XZ direction from the previous point.
            leg.Middle.m_direction = DirectionXZ(leg.Start.m_position, leg.Middle.m_position);
            leg.End.m_direction = curved
                ? DirectionXZ(leg.Middle.m_position, leg.End.m_position)
                : leg.Middle.m_direction;
            leg.Start.m_direction = leg.Middle.m_direction;

            if (leg.Middle.m_direction == Vector3.zero || leg.End.m_direction == Vector3.zero)
            {
                return null;
            }
            return leg;
        }

        /// <summary>
        /// Waypoints a straight road must stop at, ordered from start to end: existing
        /// compatible nodes on the line (within half the snap radius of it), and the points
        /// where the line crosses existing compatible segments. Never at either end.
        /// </summary>
        private static List<Vector3> NodesAlong(NetInfo info, Vector3 start, Vector3 end, float snapRadius)
        {
            List<KeyValuePair<float, Vector3>> hits = new List<KeyValuePair<float, Vector3>>();
            Vector2 a = new Vector2(start.x, start.z);
            Vector2 ab = new Vector2(end.x, end.z) - a;
            float len = ab.magnitude;
            if (len < 1f)
            {
                return new List<Vector3>();
            }
            Vector2 dir = ab / len;
            float lateral = Mathf.Max(snapRadius * 0.5f, 2f);
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            for (int i = 1; i < nodes.Length; i++)
            {
                if ((nodes[i].m_flags & NetNode.Flags.Created) == NetNode.Flags.None || !Compatible(nodes[i].Info, info))
                {
                    continue;
                }
                Vector2 p = new Vector2(nodes[i].m_position.x, nodes[i].m_position.z) - a;
                float along = Vector2.Dot(p, dir);
                if (along <= snapRadius || along >= len - snapRadius)
                {
                    continue;
                }
                float across = Mathf.Abs(p.x * dir.y - p.y * dir.x);
                if (across <= lateral)
                {
                    hits.Add(new KeyValuePair<float, Vector3>(along, nodes[i].m_position));
                }
            }
            // Crossings: NetTool's test pass reports ObjectCollision for a road drawn across
            // the middle of an existing segment, so the crossing point becomes a waypoint
            // too. It then snaps onto that segment and the game splits it into a junction.
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            Vector2 b2 = new Vector2(end.x, end.z);
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None || !Compatible(segments[i].Info, info))
                {
                    continue;
                }
                Vector3 s0 = nodes[segments[i].m_startNode].m_position;
                Vector3 s1 = nodes[segments[i].m_endNode].m_position;
                if (Mathf.Max(s0.x, s1.x) < Mathf.Min(start.x, end.x) - 64f || Mathf.Min(s0.x, s1.x) > Mathf.Max(start.x, end.x) + 64f ||
                    Mathf.Max(s0.z, s1.z) < Mathf.Min(start.z, end.z) - 64f || Mathf.Min(s0.z, s1.z) > Mathf.Max(start.z, end.z) + 64f)
                {
                    continue;
                }
                Vector3 m1;
                Vector3 m2;
                NetSegment.CalculateMiddlePoints(s0, segments[i].m_startDirection, s1, segments[i].m_endDirection, true, true, out m1, out m2);
                ColossalFramework.Math.Bezier3 curve = new ColossalFramework.Math.Bezier3(s0, m1, m2, s1);
                Vector3 prev = s0;
                for (int k = 1; k <= 16; k++)
                {
                    Vector3 cur = curve.Position(k / 16f);
                    float t;
                    float u;
                    if (SegmentsIntersect(a, b2, new Vector2(prev.x, prev.z), new Vector2(cur.x, cur.z), out t, out u))
                    {
                        float along = t * len;
                        if (along > snapRadius && along < len - snapRadius)
                        {
                            Vector3 hit = Vector3.Lerp(prev, cur, u);
                            hits.Add(new KeyValuePair<float, Vector3>(along, hit));
                        }
                        break;
                    }
                    prev = cur;
                }
            }

            hits.Sort(delegate(KeyValuePair<float, Vector3> x, KeyValuePair<float, Vector3> y) { return x.Key.CompareTo(y.Key); });

            // Drop waypoints closer together than the snap radius (a crossing right next to a
            // node is the same junction).
            for (int i = hits.Count - 1; i > 0; i--)
            {
                if (hits[i].Key - hits[i - 1].Key < snapRadius)
                {
                    hits.RemoveAt(i);
                }
            }
            List<Vector3> result = new List<Vector3>();
            for (int i = 0; i < hits.Count; i++)
            {
                result.Add(hits[i].Value);
            }
            return result;
        }

        /// <summary>2D segment intersection: p = a + t(b-a) = c + u(d-c), both t and u in [0,1].</summary>
        private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out float t, out float u)
        {
            Vector2 r = b - a;
            Vector2 q = d - c;
            float denom = r.x * q.y - r.y * q.x;
            t = 0f;
            u = 0f;
            if (Mathf.Abs(denom) < 0.0001f)
            {
                return false;
            }
            Vector2 ac = c - a;
            t = (ac.x * q.y - ac.y * q.x) / denom;
            u = (ac.x * r.y - ac.y * r.x) / denom;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        /// <summary>
        /// Networks that sit on the ground surface (roads, paths, tracks). Water pipes run
        /// underground and power lines hang from pylons, so neither cares about water.
        /// </summary>
        private static bool IsSurfaceNetwork(NetInfo info)
        {
            ItemClass.Service service = info.m_class.m_service;
            if (service == ItemClass.Service.Water || service == ItemClass.Service.Electricity)
            {
                return false;
            }
            return (info.m_class.m_layer & ItemClass.Layer.Default) != 0;
        }

        private static string IdsJson(List<ushort> ids)
        {
            StringBuilder sb = new StringBuilder("[");
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.Append(']').ToString();
        }

        /// <summary>
        /// Builds a control point the way <c>NetTool.MakeControlPoint</c> would after a
        /// raycast: snapped onto an existing node if one is within <paramref name="snapRadius"/>,
        /// else onto the closest existing segment (which the tool will split), else a free
        /// point on the terrain.
        /// </summary>
        private static NetTool.ControlPoint MakeControlPoint(NetInfo info, Vector3 point, float elevation, float snapRadius, ushort excludeNode)
        {
            NetTool.ControlPoint p = new NetTool.ControlPoint();
            p.m_elevation = elevation;
            p.m_position = point;

            if (snapRadius > 0f)
            {
                ushort node = FindNode(info, point, snapRadius, excludeNode);
                if (node != 0)
                {
                    p.m_node = node;
                    p.m_position = Singleton<NetManager>.instance.m_nodes.m_buffer[node].m_position;
                    return p;
                }

                Vector3 onSegment;
                ushort segment = FindSegment(info, point, snapRadius, out onSegment);
                if (segment != 0)
                {
                    p.m_segment = segment;
                    p.m_position = onSegment;
                    return p;
                }
            }

            p.m_position.y = NetSegment.SampleTerrainHeight(info, point, false, elevation);
            return p;
        }

        private static ushort FindNode(NetInfo info, Vector3 point, float radius, ushort excludeNode)
        {
            NetManager net = Singleton<NetManager>.instance;
            float best = radius * radius;
            ushort found = 0;
            NetNode[] nodes = net.m_nodes.m_buffer;
            for (int i = 1; i < nodes.Length; i++)
            {
                if ((nodes[i].m_flags & NetNode.Flags.Created) == NetNode.Flags.None || i == excludeNode)
                {
                    continue;
                }
                if (!Compatible(nodes[i].Info, info))
                {
                    continue;
                }
                float dx = nodes[i].m_position.x - point.x;
                float dz = nodes[i].m_position.z - point.z;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    found = (ushort)i;
                }
            }
            return found;
        }

        private static ushort FindSegment(NetInfo info, Vector3 point, float radius, out Vector3 onSegment)
        {
            NetManager net = Singleton<NetManager>.instance;
            NetSegment[] segments = net.m_segments.m_buffer;
            NetNode[] nodes = net.m_nodes.m_buffer;
            float best = radius * radius;
            ushort found = 0;
            onSegment = point;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }
                if (!Compatible(segments[i].Info, info))
                {
                    continue;
                }
                // Cheap reject: the segment's middle is further than half its length + radius.
                // Length from the end nodes: m_averageLength is the mean *lane* length, and
                // pipes and power lines have no lanes, so it reads 0 for them.
                Vector3 mid = segments[i].m_middlePosition;
                float chord = VectorUtils.LengthXZ(nodes[segments[i].m_endNode].m_position - nodes[segments[i].m_startNode].m_position);
                float reach = chord * 0.75f + radius + 8f;
                if (Mathf.Abs(mid.x - point.x) > reach || Mathf.Abs(mid.z - point.z) > reach)
                {
                    continue;
                }
                Vector3 closest = segments[i].GetClosestPosition(point);
                float dx = closest.x - point.x;
                float dz = closest.z - point.z;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    found = (ushort)i;
                    onSegment = closest;
                }
            }
            return found;
        }

        /// <summary>
        /// Whether a new network may join an existing node/segment: same prefab, or both
        /// in the same service (any road joins any road; pipes join pipes).
        /// </summary>
        private static bool Compatible(NetInfo existing, NetInfo requested)
        {
            if (existing == null || requested == null || existing.m_class == null || requested.m_class == null)
            {
                return false;
            }
            if (existing == requested)
            {
                return true;
            }
            return existing.m_class.m_service == requested.m_class.m_service &&
                existing.m_class.m_layer == requested.m_class.m_layer;
        }

        private static List<ushort> NewSegments(uint buildIndexBefore)
        {
            List<ushort> created = new List<ushort>();
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) != NetSegment.Flags.None &&
                    segments[i].m_buildIndex >= buildIndexBefore)
                {
                    created.Add((ushort)i);
                }
            }
            return created;
        }

        /// <summary>
        /// Metres of the planned curve whose ground lies under water (sampled every 4 m).
        /// </summary>
        private static float UnderwaterLength(Leg leg)
        {
            NetTool.ControlPoint start = leg.Start;
            NetTool.ControlPoint middle = leg.Middle;
            NetTool.ControlPoint end = leg.End;
            Vector3 m1;
            Vector3 m2;
            NetSegment.CalculateMiddlePoints(start.m_position, middle.m_direction, end.m_position, -end.m_direction, true, true, out m1, out m2);
            ColossalFramework.Math.Bezier3 curve = new ColossalFramework.Math.Bezier3(start.m_position, m1, m2, end.m_position);
            float length = Vector3.Distance(start.m_position, middle.m_position) + Vector3.Distance(middle.m_position, end.m_position);
            int steps = Mathf.Max(2, Mathf.CeilToInt(length / 4f));
            TerrainManager terrain = Singleton<TerrainManager>.instance;
            int wet = 0;
            for (int i = 0; i <= steps; i++)
            {
                Vector3 p = curve.Position((float)i / steps);
                // World-space overloads (metres); the (float, float) ones take grid coordinates.
                if (terrain.SampleRawHeightSmoothWithWater(p, false, 0f) - terrain.SampleRawHeightSmooth(p) > 0.5f)
                {
                    wet++;
                }
            }
            return length * wet / (steps + 1);
        }

        private static Vector3 DirectionXZ(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            d.y = 0f;
            if (d.sqrMagnitude < 0.0001f)
            {
                return Vector3.zero;
            }
            return d.normalized;
        }

        private static string SnapJson(string name, NetTool.ControlPoint p)
        {
            string kind = p.m_node != 0 ? "node" : (p.m_segment != 0 ? "segment" : "free");
            return "\"" + name + "\":{\"snappedTo\":\"" + kind + "\"" +
                ",\"nodeId\":" + p.m_node +
                ",\"segmentId\":" + p.m_segment +
                ",\"x\":" + JsonUtil.Number(p.m_position.x) +
                ",\"y\":" + JsonUtil.Number(p.m_position.y) +
                ",\"z\":" + JsonUtil.Number(p.m_position.z) + "}";
        }

        internal static int MoneyNow()
        {
            return (int)(Singleton<EconomyManager>.instance.InternalCashAmount / 100);
        }
    }
}
