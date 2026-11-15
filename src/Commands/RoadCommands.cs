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
            if (!UnlockCheck.IsUnlocked(info))
            {
                return CommandResult.Fail(UnlockCheck.LockedMessage(info, prefabName));
            }

            // A straight road that runs over an existing node must stop at it, exactly as a
            // player clicks on that node: NetTool reports ObjectCollision for a road drawn
            // across a dead end. Split the request into legs at every such node.
            // Only on the same level, though: an elevated road (or a tunnel) passes over (or
            // under) a ground road, and splitting it there would pull it down into an
            // at-grade junction (#99: an overpass across a highway failed SlopeTooSteep).
            List<Vector3> waypoints = new List<Vector3>();
            List<ushort> passedOver = new List<ushort>();
            waypoints.Add(start);
            if (!curved && autoSplit)
            {
                waypoints.AddRange(NodesAlong(info, start, end, snapRadius, elevation, passedOver));
            }
            waypoints.Add(end);
            string passedOverJson = passedOver.Count == 0 ? "" : ",\"crossesWithoutJunction\":" + IdsJson(passedOver);

            bool needMoney = GameValidation.NeedMoney();
            int legs = waypoints.Count - 1;
            ToolBase.ToolErrors allErrors = ToolBase.ToolErrors.None;
            int totalCost = 0;
            float totalUnderwater = 0f;
            StringBuilder legJson = new StringBuilder();
            string collisionHint = null;
            Dictionary<string, float> variantMeters = new Dictionary<string, float>();

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
                ToolBase.ToolErrors errors;
                string collisions = "";
                string variants = "";
                // Open our own collision scope around the test pass: CreateNode marks every
                // segment and building it collides with in the ToolController's buffers,
                // which is what the player's tool paints red. Read them, then clear them.
                ToolController properties = Singleton<ToolManager>.instance.m_properties;
                ulong[] collidingSegments = null;
                ulong[] collidingBuildings = null;
                if (properties != null)
                {
                    properties.BeginColliding(out collidingSegments, out collidingBuildings);
                }
                try
                {
                    errors = NetTool.CreateNode(
                        info, leg.Start, leg.Middle, leg.End, NodeBuffer, 1000,
                        true, false, true, needMoney, false, false, 0,
                        out node, out segment, out cost, out productionRate);
                    if ((errors & ToolBase.ToolErrors.ObjectCollision) != ToolBase.ToolErrors.None)
                    {
                        collisions = CollisionsJson(leg, collidingSegments, collidingBuildings, passedOver, ref collisionHint);
                    }
                    variants = PlannedVariantsJson(info, curved, ref variantMeters);
                }
                finally
                {
                    if (properties != null)
                    {
                        properties.ResetColliding();
                        properties.EndColliding();
                    }
                }

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
                    .Append(collisions)
                    .Append(variants)
                    .Append(',').Append(SnapJson("start", leg.Start))
                    .Append(',').Append(SnapJson("end", leg.End)).Append('}');
            }

            // NetTool's own money test asks PeekResource, which a cheat mod can answer;
            // check the real treasury for the whole build (all legs) too.
            if (needMoney && !GameValidation.CanAfford(totalCost))
            {
                allErrors |= ToolBase.ToolErrors.NotEnoughMoney;
            }
            bool wet = totalUnderwater >= 4f;
            bool passed = allErrors == ToolBase.ToolErrors.None && !wet;
            if (!passed || dryRun)
            {
                string errorsJson = GameValidation.ErrorsJson(allErrors);
                string hint = GameValidation.Hint(allErrors);
                if (collisionHint != null)
                {
                    hint = collisionHint;
                }
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
                    PowerLinesCrossedJson(info, waypoints, curved, control) +
                    VariantWarningJson(info, variantMeters) +
                    passedOverJson +
                    ",\"legs\":[" + legJson + "]}");
            }

            // Phase 2: build. Control points are re-made per leg, so leg i+1 snaps onto the
            // node leg i just created.
            NetManager net = Singleton<NetManager>.instance;
            Collateral collateral = TakeCollateral(waypoints, curved, control);
            System.Predicate<NetInfo> splitByDesign = delegate(NetInfo existing) { return Compatible(existing, info); };
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
                        ",\"segmentIds\":" + IdsJson(NewSegments(buildIndexBefore)) +
                        collateral.RemovedJson(splitByDesign) + "}");
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
            Dictionary<string, float> builtVariantMeters = new Dictionary<string, float>();
            string builtVariants = BuiltVariantsJson(info, created, builtVariantMeters);

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":false,\"built\":true" +
                ",\"roadPrefab\":\"" + JsonUtil.Escape(prefabName) + "\"" +
                ",\"segmentIds\":" + IdsJson(created) +
                ",\"endNodeId\":" + lastNode +
                ",\"cost\":" + (totalCost / 100) +
                ",\"moneyBefore\":" + moneyBefore +
                ",\"moneyAfter\":" + MoneyNow() +
                builtVariants +
                VariantWarningJson(info, builtVariantMeters) +
                passedOverJson +
                collateral.RemovedJson(splitByDesign) +
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
        /// where the line crosses existing compatible segments. Never at either end, and
        /// never at a node or crossing on another level than the new road (an overpass or
        /// underpass): those segment ids go to <paramref name="passedOver"/>.
        /// </summary>
        private static List<Vector3> NodesAlong(NetInfo info, Vector3 start, Vector3 end, float snapRadius, float elevation, List<ushort> passedOver)
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
                if (across <= lateral && SameLevel(info, nodes[i].m_position, elevation))
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
                            if (SameLevel(info, hit, elevation))
                            {
                                hits.Add(new KeyValuePair<float, Vector3>(along, hit));
                            }
                            else
                            {
                                passedOver.Add((ushort)i);
                            }
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

        /// <summary>
        /// Height difference (m) below which an existing node or crossing counts as on the
        /// new road's level. 4 m is where the bridge treats a road as raised (the elevated or
        /// bridge variant), and leaves room for a ground road's curve over uneven terrain.
        /// </summary>
        private const float SameLevelTolerance = 4f;

        /// <summary>
        /// Whether an existing point of a network lies on the level the new road would have
        /// there: the terrain plus the requested elevation, as the road tool samples it.
        /// </summary>
        private static bool SameLevel(NetInfo info, Vector3 existing, float elevation)
        {
            float planned = NetSegment.SampleTerrainHeight(info, existing, false, elevation);
            return Mathf.Abs(existing.y - planned) < SameLevelTolerance;
        }

        private static Collateral TakeCollateral(List<Vector3> waypoints, bool curved, Vector3 control)
        {
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            List<Vector3> pts = new List<Vector3>(waypoints);
            if (curved) pts.Add(control);
            for (int i = 0; i < pts.Count; i++)
            {
                minX = Mathf.Min(minX, pts[i].x); maxX = Mathf.Max(maxX, pts[i].x);
                minZ = Mathf.Min(minZ, pts[i].z); maxZ = Mathf.Max(maxZ, pts[i].z);
            }
            return Collateral.Take(minX, minZ, maxX, maxZ, 64f);
        }

        /// <summary>
        /// For a dry run: what the planned surface network would clear away. The power lines
        /// whose pylons stand in its way (the game removes the whole line, cutting whatever it
        /// feeds) and the grown buildings it runs through (demolished like the player's tool
        /// does). Returns <c>,"removesPowerLines":[...],"wouldRemove":{...},"warning":"..."</c>
        /// (only the parts that apply) or an empty string.
        /// </summary>
        private static string PowerLinesCrossedJson(NetInfo info, List<Vector3> waypoints, bool curved, Vector3 control)
        {
            if (!IsSurfaceNetwork(info))
            {
                return "";
            }
            List<Vector2> path = new List<Vector2>();
            if (curved)
            {
                Vector3 a = waypoints[0];
                Vector3 b = waypoints[waypoints.Count - 1];
                ColossalFramework.Math.Bezier3 curve = new ColossalFramework.Math.Bezier3(a,
                    Vector3.Lerp(a, control, 2f / 3f), Vector3.Lerp(b, control, 2f / 3f), b);
                for (int k = 0; k <= 16; k++)
                {
                    Vector3 p = curve.Position(k / 16f);
                    path.Add(new Vector2(p.x, p.z));
                }
            }
            else
            {
                for (int i = 0; i < waypoints.Count; i++)
                {
                    path.Add(new Vector2(waypoints[i].x, waypoints[i].z));
                }
            }
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            NetSegment[] segments = Singleton<NetManager>.instance.m_segments.m_buffer;
            StringBuilder ids = new StringBuilder();
            int count = 0;
            for (int i = 1; i < segments.Length; i++)
            {
                if ((segments[i].m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }
                NetInfo other = segments[i].Info;
                if (other == null || other.m_class == null || other.m_class.m_service != ItemClass.Service.Electricity)
                {
                    continue;
                }
                // Only a pylon (a node of the line) in the road's way gets the line
                // removed; a road crossing the wire between two pylons leaves it alone.
                float reach = info.m_halfWidth + 4f;
                if (NearPath(path, nodes[segments[i].m_startNode].m_position, reach) ||
                    NearPath(path, nodes[segments[i].m_endNode].m_position, reach))
                {
                    if (count++ > 0) ids.Append(',');
                    ids.Append(i);
                }
            }

            // Grown (zoned) buildings in the way are demolished too: the road tool treats
            // them as clearable. Test each building's footprint, grown by the road's
            // half width, against points sampled along the route.
            StringBuilder grown = new StringBuilder();
            int grownCount = 0;
            List<Vector2> samples = new List<Vector2>();
            for (int k = 1; k < path.Count; k++)
            {
                float len = Vector2.Distance(path[k - 1], path[k]);
                int steps = Mathf.Max(1, Mathf.CeilToInt(len / 2f));
                for (int j = 0; j <= steps; j++)
                {
                    samples.Add(Vector2.Lerp(path[k - 1], path[k], j / (float)steps));
                }
            }
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            for (int k = 0; k < samples.Count; k++)
            {
                minX = Mathf.Min(minX, samples[k].x); maxX = Mathf.Max(maxX, samples[k].x);
                minZ = Mathf.Min(minZ, samples[k].y); maxZ = Mathf.Max(maxZ, samples[k].y);
            }
            Building[] blds = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            float pad = info.m_halfWidth - 1f;
            for (int i = 1; i < blds.Length; i++)
            {
                if ((blds[i].m_flags & Building.Flags.Created) == Building.Flags.None) continue;
                BuildingInfo bi = blds[i].Info;
                if (bi == null || bi.m_class == null) continue;
                ItemClass.Service sv = bi.m_class.m_service;
                if (sv != ItemClass.Service.Residential && sv != ItemClass.Service.Commercial &&
                    sv != ItemClass.Service.Industrial && sv != ItemClass.Service.Office) continue;
                Vector3 c = blds[i].m_position;
                float hw = bi.m_cellWidth * 4f + pad;
                float hl = bi.m_cellLength * 4f + pad;
                float r = Mathf.Max(hw, hl) * 1.5f;
                if (c.x < minX - r || c.x > maxX + r || c.z < minZ - r || c.z > maxZ + r) continue;
                float cos = Mathf.Cos(blds[i].m_angle);
                float sin = Mathf.Sin(blds[i].m_angle);
                for (int k = 0; k < samples.Count; k++)
                {
                    float dx = samples[k].x - c.x;
                    float dz = samples[k].y - c.z;
                    // Building space: x along its width, z along its length (the game's
                    // m_angle rotates the building's local axes in the XZ plane).
                    float lx = dx * cos + dz * sin;
                    float lz = -dx * sin + dz * cos;
                    if (Mathf.Abs(lx) <= hw && Mathf.Abs(lz) <= hl)
                    {
                        if (grownCount++ > 0) grown.Append(',');
                        grown.Append("{\"id\":").Append(i).Append(",\"prefab\":\"").Append(JsonUtil.Escape(bi.name))
                            .Append("\",\"x\":").Append(JsonUtil.Number(c.x)).Append(",\"z\":").Append(JsonUtil.Number(c.z)).Append('}');
                        break;
                    }
                }
            }

            if (count == 0 && grownCount == 0)
            {
                return "";
            }
            string json = "";
            string warning = "";
            if (count > 0)
            {
                json += ",\"removesPowerLines\":[" + ids + "]";
                warning += "This route runs over a pylon of " + count + " power line segment(s). The game removes a power line whose pylon is in a road's way, " +
                    "so whatever it feeds would lose power. Shift the road, or rebuild the line around it afterwards (crossing the wire between pylons is fine). ";
            }
            if (grownCount > 0)
            {
                json += ",\"wouldRemove\":{\"buildings\":[" + grown + "]}";
                warning += "The road would demolish " + grownCount + " grown building(s) in its way (the game clears zoned buildings for a road).";
            }
            return json + ",\"warning\":\"" + JsonUtil.Escape(warning.Trim()) + "\"";
        }

        /// <summary>True when point p lies within reach metres (XZ) of the polyline.</summary>
        private static bool NearPath(List<Vector2> path, Vector3 p, float reach)
        {
            Vector2 q = new Vector2(p.x, p.z);
            for (int k = 1; k < path.Count; k++)
            {
                Vector2 a = path[k - 1];
                Vector2 ab = path[k] - a;
                float len2 = ab.sqrMagnitude;
                float t = len2 > 0.0001f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / len2) : 0f;
                if ((a + ab * t - q).sqrMagnitude <= reach * reach)
                {
                    return true;
                }
            }
            return false;
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
                // An end inside a building that owns a node of this network (a water
                // building's pipe connection, at its landward end) goes to that node: a pipe
                // ending anywhere else on the building - its centre included - leaves a free
                // node that conducts nothing (found live: a drain pipe stayed "not connected").
                ushort own = FindBuildingNode(info, point, excludeNode);
                ushort node = FindNode(info, point, snapRadius, excludeNode);
                // An existing node right at the point wins over the building's own node
                // (a pipe started on a network node that happened to lie inside a new
                // building ran from the building's node to its centre instead).
                if (own != 0 && (node == 0 || DistanceXZ(node, point) > DistanceXZ(own, point)))
                {
                    p.m_node = own;
                    p.m_position = Singleton<NetManager>.instance.m_nodes.m_buffer[own].m_position;
                    return p;
                }

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

        /// <summary>
        /// The compatible node owned by the building whose footprint contains the point (the
        /// nearest one if it owns several), or 0.
        /// </summary>
        private static ushort FindBuildingNode(NetInfo info, Vector3 point, ushort excludeNode)
        {
            Building[] blds = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            NetNode[] nodes = Singleton<NetManager>.instance.m_nodes.m_buffer;
            ushort found = 0;
            float best = float.MaxValue;
            for (int i = 1; i < blds.Length; i++)
            {
                if ((blds[i].m_flags & Building.Flags.Created) == Building.Flags.None || blds[i].m_netNode == 0)
                {
                    continue;
                }
                BuildingInfo bi = blds[i].Info;
                if (bi == null)
                {
                    continue;
                }
                Vector3 c = blds[i].m_position;
                float hw = bi.m_cellWidth * 4f;
                float hl = bi.m_cellLength * 4f;
                float dx = point.x - c.x;
                float dz = point.z - c.z;
                if (dx * dx + dz * dz > (hw + hl) * (hw + hl))
                {
                    continue;
                }
                float cos = Mathf.Cos(blds[i].m_angle);
                float sin = Mathf.Sin(blds[i].m_angle);
                float lx = dx * cos + dz * sin;
                float lz = -dx * sin + dz * cos;
                if (Mathf.Abs(lx) > hw || Mathf.Abs(lz) > hl)
                {
                    continue;
                }
                ushort n = blds[i].m_netNode;
                int guard = 0;
                while (n != 0 && guard++ < 64)
                {
                    if (n != excludeNode && (nodes[n].m_flags & NetNode.Flags.Created) != NetNode.Flags.None && Compatible(nodes[n].Info, info))
                    {
                        float ex = nodes[n].m_position.x - point.x;
                        float ez = nodes[n].m_position.z - point.z;
                        float d = ex * ex + ez * ez;
                        if (d < best)
                        {
                            best = d;
                            found = n;
                        }
                    }
                    n = nodes[n].m_nextBuildingNode;
                }
            }
            return found;
        }

        private static float DistanceXZ(ushort node, Vector3 point)
        {
            Vector3 p = Singleton<NetManager>.instance.m_nodes.m_buffer[node].m_position;
            float dx = p.x - point.x;
            float dz = p.z - point.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
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

        /// <summary>
        /// The objects the game's test pass marked as colliding, as a <c>collidesWith</c>
        /// field. A segment attached to the leg's own start or end node collides because the
        /// new segment leaves that node too close to it: <c>NetTool.CanAddSegment</c> refuses
        /// a direction within about 41 degrees (dot product above 0.75) of an existing segment
        /// at the node. That one gets <c>reason: "narrowAngle"</c> and its own hint. The
        /// segment the leg starts or ends on (which the game would split) is <c>cannotSplit</c>;
        /// anything else is <c>overlap</c>.
        /// </summary>
        private static string CollisionsJson(Leg leg, ulong[] segmentBits, ulong[] buildingBits, List<ushort> passedOver, ref string collisionHint)
        {
            ushort passedOverHit = 0;
            const int MaxReported = 20;
            NetManager net = Singleton<NetManager>.instance;
            BuildingManager bm = Singleton<BuildingManager>.instance;
            StringBuilder sb = new StringBuilder(",\"collidesWith\":[");
            int count = 0;
            int blockers = 0;
            string firstBlocker = null;
            if (segmentBits != null)
            {
                for (int w = 0; w < segmentBits.Length && count < MaxReported; w++)
                {
                    ulong bits = segmentBits[w];
                    for (int b = 0; bits != 0 && b < 64 && count < MaxReported; b++)
                    {
                        if ((bits & (1UL << b)) == 0)
                        {
                            continue;
                        }
                        bits &= ~(1UL << b);
                        int id = (w << 6) | b;
                        if (id == 0 || id >= net.m_segments.m_buffer.Length)
                        {
                            continue;
                        }
                        NetSegment s = net.m_segments.m_buffer[id];
                        ushort atNode = 0;
                        if (leg.Start.m_node != 0 && (s.m_startNode == leg.Start.m_node || s.m_endNode == leg.Start.m_node))
                        {
                            atNode = leg.Start.m_node;
                        }
                        else if (leg.End.m_node != 0 && (s.m_startNode == leg.End.m_node || s.m_endNode == leg.End.m_node))
                        {
                            atNode = leg.End.m_node;
                        }
                        // The segment the leg starts or ends on is split there; the game refuses
                        // a split too close to one of its nodes or at a too narrow angle.
                        bool split = id == leg.Start.m_segment || id == leg.End.m_segment;
                        if (split && collisionHint == null)
                        {
                            collisionHint = "The game cannot split segment " + id + " at that point: it is too close to one of the segment's nodes, or the new segment meets it at too narrow an angle. " +
                                "Start on the segment's node instead, further along it, or at a wider angle.";
                        }
                        bool blocking = atNode != 0 || split || IsBlockingSegment(s);
                        if (atNode == 0 && !split && blocking)
                        {
                            if (passedOverHit == 0 && passedOver.Contains((ushort)id))
                            {
                                passedOverHit = (ushort)id;
                            }
                            blockers++;
                            if (firstBlocker == null)
                            {
                                firstBlocker = (s.Info != null ? s.Info.name : "segment") + " (segment " + id + ")";
                            }
                        }
                        if (count++ > 0) sb.Append(',');
                        sb.Append("{\"kind\":\"segment\",\"id\":").Append(id)
                            .Append(",\"prefab\":\"").Append(JsonUtil.Escape(s.Info != null ? s.Info.name : "")).Append('"')
                            .Append(",\"reason\":\"").Append(atNode != 0 ? "narrowAngle" : (split ? "cannotSplit" : "overlap")).Append('"')
                            .Append(",\"blocking\":").Append(JsonUtil.Bool(blocking));
                        if (atNode != 0)
                        {
                            sb.Append(",\"atNode\":").Append(atNode);
                            if (collisionHint == null)
                            {
                                collisionHint = "The new segment leaves node " + atNode + " within about 41 degrees of segment " + id +
                                    ", which already runs from that node in nearly the same direction (the game needs a wider angle between segments at a node). " +
                                    "Aim the other end in a different direction, extend from the far end of segment " + id + ", or start somewhere else.";
                            }
                        }
                        sb.Append('}');
                    }
                }
            }
            if (buildingBits != null)
            {
                for (int w = 0; w < buildingBits.Length && count < MaxReported; w++)
                {
                    ulong bits = buildingBits[w];
                    for (int b = 0; bits != 0 && b < 64 && count < MaxReported; b++)
                    {
                        if ((bits & (1UL << b)) == 0)
                        {
                            continue;
                        }
                        bits &= ~(1UL << b);
                        int id = (w << 6) | b;
                        if (id == 0 || id >= bm.m_buildings.m_buffer.Length)
                        {
                            continue;
                        }
                        BuildingInfo bi = bm.m_buildings.m_buffer[id].Info;
                        Vector3 p = bm.m_buildings.m_buffer[id].m_position;
                        bool blocking = IsBlockingBuilding(bi, ref bm.m_buildings.m_buffer[id]);
                        if (blocking)
                        {
                            blockers++;
                            if (firstBlocker == null)
                            {
                                firstBlocker = (bi != null ? bi.name : "building") + " (building " + id + " at " +
                                    Mathf.RoundToInt(p.x) + ", " + Mathf.RoundToInt(p.z) + ")";
                            }
                        }
                        if (count++ > 0) sb.Append(',');
                        sb.Append("{\"kind\":\"building\",\"id\":").Append(id)
                            .Append(",\"prefab\":\"").Append(JsonUtil.Escape(bi != null ? bi.name : "")).Append('"')
                            .Append(",\"reason\":\"overlap\"")
                            .Append(",\"blocking\":").Append(JsonUtil.Bool(blocking))
                            .Append(",\"x\":").Append(JsonUtil.Number(p.x))
                            .Append(",\"z\":").Append(JsonUtil.Number(p.z)).Append('}');
                    }
                }
            }
            if (collisionHint == null && passedOverHit != 0)
            {
                // The road passes over (or under) this segment by design, so what hits it is
                // a node the game places along the leg - a pillar - or the deck itself.
                collisionHint = "The road crosses segment " + passedOverHit + " on another level, but a node the game places along it (a pillar) stands on it, or the deck is too close to it. " +
                    "The game puts nodes at the leg ends and divides a long leg evenly; this leg's nodes are at " + PlannedNodesText() + ". " +
                    "Move or shorten the leg so its ends and nodes fall between or beyond the carriageways (or build it as two legs), or raise the elevation.";
            }
            if (collisionHint == null && firstBlocker != null)
            {
                collisionHint = "Blocked by " + firstBlocker + (blockers > 1 ? " and " + (blockers - 1) + " more (collidesWith, blocking: true)" : "") +
                    ": the new segment, or a node or pylon the game places along it, would stand on it. Length is not the problem - the game places pylons and nodes along a long line itself. " +
                    "Route around it: add a waypoint (build it as two legs) or move the line. Objects with blocking: false (grown buildings, power lines) would be removed by the build instead.";
            }
            return sb.Append(']').ToString();
        }

        /// <summary>The node positions the last test pass planned, as "(x, z), (x, z)".</summary>
        private static string PlannedNodesText()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < NodeBuffer.m_size; i++)
            {
                Vector3 p = NodeBuffer.m_buffer[i].m_position;
                if (i > 0) sb.Append(", ");
                sb.Append('(').Append(Mathf.RoundToInt(p.x)).Append(", ").Append(Mathf.RoundToInt(p.z)).Append(')');
            }
            return sb.ToString();
        }

        /// <summary>
        /// The segment rule of <c>NetTool.CheckCollidingSegments</c>: a public-service network
        /// that is not auto-removed (roads, pipes, tracks), or an untouchable one, blocks; the
        /// rest (power lines) is released by the build.
        /// </summary>
        private static bool IsBlockingSegment(NetSegment s)
        {
            NetInfo info = s.Info;
            if ((s.m_flags & NetSegment.Flags.Untouchable) != NetSegment.Flags.None)
            {
                return true;
            }
            return info != null && info.m_class != null &&
                ItemClass.GetPublicServiceIndex(info.m_class.m_service) != -1 && !info.m_autoRemove;
        }

        /// <summary>
        /// The building rule of <c>BuildingTool.CheckCollidingBuildings</c> /
        /// <c>IsImportantBuilding</c>: service buildings, historical, untouchable and burning
        /// ones block; grown buildings are demolished by the build.
        /// </summary>
        private static bool IsBlockingBuilding(BuildingInfo info, ref Building building)
        {
            if (info == null || info.IgnoreBuildingCollision())
            {
                return false;
            }
            if ((building.m_flags & (Building.Flags.Historical | Building.Flags.Untouchable)) != Building.Flags.None)
            {
                return true;
            }
            if (building.m_fireIntensity != 0)
            {
                return true;
            }
            return ItemClass.GetPublicServiceIndex(info.m_class.m_service) != -1 && !info.m_autoRemove &&
                (building.m_levelUpProgress != byte.MaxValue || (building.m_flags & Building.Flags.Collapsed) == Building.Flags.None);
        }

        // ------------------------------------------------------------ variants

        /// <summary>
        /// Which variant of the network the game picks where the ground forces it: a road
        /// whose nodes lie more than 8 m below the terrain becomes its tunnel (or the slope
        /// at a portal), one above the terrain its elevated or bridge variant
        /// (<c>RoadAI.GetInfo</c>). Nothing can be zoned along a tunnel or an elevated road.
        /// CreateNode clears the per-node infos it chose before it returns, so the same
        /// <c>NetAI.GetInfo</c> call is repeated on each node's height above the terrain,
        /// which the test pass leaves in the node buffer.
        /// </summary>
        private static string PlannedVariantsJson(NetInfo info, bool curved, ref Dictionary<string, float> meters)
        {
            int size = NodeBuffer.m_size;
            if (size < 2 || info.m_netAI == null)
            {
                return "";
            }
            NetInfo[] chosen = new NetInfo[size];
            for (int i = 0; i < size; i++)
            {
                NetTool.NodePosition np = NodeBuffer.m_buffer[i];
                float el = np.m_position.y - np.m_terrainHeight;
                float length = i > 0 ? VectorUtils.LengthXZ(np.m_position - NodeBuffer.m_buffer[i - 1].m_position) : 0f;
                ToolBase.ToolErrors ignored = ToolBase.ToolErrors.None;
                chosen[i] = info.m_netAI.GetInfo(el, el, Mathf.Max(length, 1f), false, false, curved, false, ref ignored) ?? info;
            }
            StringBuilder sb = new StringBuilder();
            int count = 0;
            int startIndex = -1;
            NetInfo current = null;
            for (int i = 1; i <= size; i++)
            {
                // A stretch between nodes i-1 and i takes the variant of whichever end is not
                // the plain network (a tunnel portal is a slope at one end).
                NetInfo seg = i < size ? (chosen[i] != info ? chosen[i] : chosen[i - 1]) : null;
                if (seg == info) seg = null;
                if (seg != current || i == size)
                {
                    if (current != null && startIndex >= 0)
                    {
                        Vector3 a = NodeBuffer.m_buffer[startIndex].m_position;
                        Vector3 b = NodeBuffer.m_buffer[i - 1].m_position;
                        float len = 0f;
                        for (int k = startIndex + 1; k <= i - 1; k++)
                        {
                            len += VectorUtils.LengthXZ(NodeBuffer.m_buffer[k].m_position - NodeBuffer.m_buffer[k - 1].m_position);
                        }
                        string kind = VariantKind(current);
                        float sum;
                        meters.TryGetValue(kind, out sum);
                        meters[kind] = sum + len;
                        if (count++ > 0) sb.Append(',');
                        sb.Append("{\"kind\":\"").Append(kind).Append("\",\"prefab\":\"").Append(JsonUtil.Escape(current.name)).Append('"')
                            .Append(",\"meters\":").Append(Mathf.RoundToInt(len))
                            .Append(",\"from\":{\"x\":").Append(JsonUtil.Number(a.x)).Append(",\"z\":").Append(JsonUtil.Number(a.z)).Append('}')
                            .Append(",\"to\":{\"x\":").Append(JsonUtil.Number(b.x)).Append(",\"z\":").Append(JsonUtil.Number(b.z)).Append("}}");
                    }
                    current = seg;
                    startIndex = i - 1;
                }
            }
            return count == 0 ? "" : ",\"variants\":[" + sb + "]";
        }

        /// <summary>The created segments whose prefab is not the requested one.</summary>
        private static string BuiltVariantsJson(NetInfo info, List<ushort> created, Dictionary<string, float> meters)
        {
            NetManager net = Singleton<NetManager>.instance;
            StringBuilder sb = new StringBuilder();
            int count = 0;
            for (int i = 0; i < created.Count; i++)
            {
                NetSegment s = net.m_segments.m_buffer[created[i]];
                NetInfo si = s.Info;
                if (si == null || si == info || si.m_class == null || si.m_class.m_service != info.m_class.m_service)
                {
                    continue; // the requested prefab, or a junction split of another network
                }
                Vector3 a = net.m_nodes.m_buffer[s.m_startNode].m_position;
                Vector3 b = net.m_nodes.m_buffer[s.m_endNode].m_position;
                float len = VectorUtils.LengthXZ(b - a);
                string kind = VariantKind(si);
                float sum;
                meters.TryGetValue(kind, out sum);
                meters[kind] = sum + len;
                if (count++ > 0) sb.Append(',');
                sb.Append("{\"segmentId\":").Append(created[i])
                    .Append(",\"kind\":\"").Append(kind).Append("\",\"prefab\":\"").Append(JsonUtil.Escape(si.name)).Append('"')
                    .Append(",\"meters\":").Append(Mathf.RoundToInt(len)).Append('}');
            }
            return count == 0 ? "" : ",\"variants\":[" + sb + "]";
        }

        private static string VariantKind(NetInfo v)
        {
            string n = v.name ?? "";
            if (n.EndsWith(" Slope", System.StringComparison.OrdinalIgnoreCase)) return "tunnelPortal";
            if (n.EndsWith(" Tunnel", System.StringComparison.OrdinalIgnoreCase)) return "tunnel";
            if (n.EndsWith(" Bridge", System.StringComparison.OrdinalIgnoreCase)) return "bridge";
            if (n.EndsWith(" Elevated", System.StringComparison.OrdinalIgnoreCase)) return "elevated";
            if (v.m_netAI != null && v.m_netAI.IsUnderground()) return "tunnel";
            if (v.m_netAI != null && v.m_netAI.IsOverground()) return "elevated";
            return "variant";
        }

        private static string VariantWarningJson(NetInfo info, Dictionary<string, float> meters)
        {
            if (meters.Count == 0)
            {
                return "";
            }
            StringBuilder parts = new StringBuilder();
            StringBuilder totals = new StringBuilder();
            foreach (KeyValuePair<string, float> kv in meters)
            {
                if (parts.Length > 0) { parts.Append(", "); totals.Append(','); }
                parts.Append(Mathf.RoundToInt(kv.Value)).Append(" m ").Append(kv.Key);
                totals.Append('"').Append(kv.Key).Append("\":").Append(Mathf.RoundToInt(kv.Value));
            }
            bool zonable = info.m_class != null && info.m_class.m_service == ItemClass.Service.Road;
            string warning = "Part of this network is a tunnel, bridge or elevated variant (the ground forces it, or elevation asked for it): " + parts + "." +
                (zonable ? " Nothing can be zoned along a tunnel, slope, bridge or elevated road. Reroute round the ridge or valley (city_map heights) if you need frontage there." : "");
            return ",\"variantMeters\":{" + totals + "},\"variantWarning\":\"" + JsonUtil.Escape(warning) + "\"";
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
