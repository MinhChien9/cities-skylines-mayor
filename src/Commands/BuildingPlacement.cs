using System;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// A port of the placement half of <c>BuildingTool.SimulationStep</c>: given a prefab and
    /// a point the agent aims at, find the position and angle the building tool would put it
    /// at, and the errors the tool would show.
    ///
    /// The tool's rules are reproduced per placement mode, calling the game's own public
    /// static checks (<c>BuildingTool.CheckSpace</c>, <c>BuildingTool.SnapToPath</c>,
    /// <c>BuildingAI.CheckBuildPosition</c>, <c>ZoneManager.CheckSpace</c>,
    /// <c>TerrainManager.GetShorePos</c>, <c>Building.SampleBuildingHeight</c>):
    ///
    ///   Roadside           — snap to the nearest zone-grid slot within 8 m, facing the road
    ///                        (the private <c>FindClosestZone</c>, ported below); else snap
    ///                        to the nearest road within 40 m (<c>SnapToPath</c>)
    ///   Shoreline          — snap to the water's edge within 50 m, facing the water
    ///   ShorelineOrGround  — the shore if one is within 50 m, else free placement
    ///   PathsideOrGround   — snap to a road/path if one is close, else free placement
    ///   OnGround / OnSurface / OnTerrain / OnWater — free placement at the requested angle
    ///
    /// Everything here runs on the simulation thread, like the tool's own SimulationStep.
    /// </summary>
    internal static class BuildingPlacement
    {
        public sealed class Result
        {
            public Vector3 Position;
            public float Angle;
            public ToolBase.ToolErrors Errors;
            public int Cost;
            public string Snap = "none";
        }

        public static Result Resolve(BuildingInfo info, Vector3 target, float angleDegrees, ushort relocating)
        {
            Result r = new Result();
            ToolController tools = Singleton<ToolManager>.instance.m_properties;
            ulong[] collidingSegments;
            ulong[] collidingBuildings;
            tools.BeginColliding(out collidingSegments, out collidingBuildings);
            try
            {
                Evaluate(info, target, angleDegrees, relocating, collidingSegments, collidingBuildings, tools, r);
            }
            finally
            {
                tools.EndColliding();
            }

            if (GameValidation.NeedMoney() && !GameValidation.CanAfford(r.Cost))
            {
                r.Errors |= ToolBase.ToolErrors.NotEnoughMoney;
            }
            return r;
        }

        /// <summary>
        /// Clears the trees, props and non-important buildings/segments the building will
        /// stand on — what the tool does (CheckSpace with test: false) right before it
        /// creates the building.
        /// </summary>
        public static void ClearSite(BuildingInfo info, Result r, ushort relocating)
        {
            ToolController tools = Singleton<ToolManager>.instance.m_properties;
            ulong[] collidingSegments;
            ulong[] collidingBuildings;
            tools.BeginColliding(out collidingSegments, out collidingBuildings);
            try
            {
                float minY;
                float maxY;
                float buildingY;
                Building.SampleBuildingHeight(r.Position, r.Angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
                Vector3 pos = r.Position;
                pos.y = buildingY;
                BuildingTool.CheckSpace(info, info.m_placementMode, relocating, pos, minY, buildingY + info.m_collisionHeight, r.Angle,
                    info.m_cellWidth, info.m_cellLength, false, collidingSegments, collidingBuildings);
            }
            finally
            {
                tools.EndColliding();
            }
        }

        private static void Evaluate(BuildingInfo info, Vector3 target, float angleDegrees, ushort relocating,
            ulong[] segs, ulong[] blds, ToolController tools, Result r)
        {
            float angle = angleDegrees * Mathf.Deg2Rad;
            float waterHeight = 0f;
            float elevation = 0f;
            Segment3 connection = default(Segment3);
            int productionRate;
            int cost;
            Vector3 position = target;
            position.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(position);

            switch (info.m_placementMode)
            {
                case BuildingInfo.PlacementMode.Roadside:
                {
                    ToolBase.ToolErrors errors = ToolBase.ToolErrors.GridNotFound;
                    float minD = 8f;
                    float min2 = 1000000f;
                    Vector3 snapped = position;
                    float snappedAngle = angle;
                    FindClosestZoneSlot(info, position, relocating, ref minD, ref min2, ref snapped, ref snappedAngle);
                    if (minD < 8f)
                    {
                        position = snapped;
                        angle = snappedAngle;
                        r.Snap = "zoneGrid";
                        errors = TryZoneSlot(info, relocating, ref position, angle, segs, blds, tools);
                    }
                    if (errors != ToolBase.ToolErrors.None)
                    {
                        Vector3 pathPos;
                        Vector3 dir;
                        if (BuildingTool.SnapToPath(target, out pathPos, out dir, 40f, true))
                        {
                            for (int m = 0; m < 9; m++)
                            {
                                Vector3 candidate = pathPos - dir * ((float)info.m_cellLength * 4f + (float)m * 0.5f);
                                float candidateAngle = Mathf.Atan2(-dir.x, dir.z);
                                float minY, maxY, buildingY;
                                Building.SampleBuildingHeight(candidate, candidateAngle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
                                tools.ResetColliding();
                                ToolBase.ToolErrors e = info.m_buildingAI.CheckBuildPosition(relocating, ref candidate, ref candidateAngle, waterHeight, elevation, ref connection, out productionRate, out cost);
                                e |= BuildingTool.CheckSpace(info, BuildingInfo.PlacementMode.PathsideOrGround, relocating, candidate, minY, candidate.y + info.m_collisionHeight, candidateAngle, info.m_cellWidth, info.m_cellLength, true, segs, blds);
                                if (maxY - minY > info.m_maxHeightOffset)
                                {
                                    e |= ToolBase.ToolErrors.SlopeTooSteep;
                                }
                                if (e == ToolBase.ToolErrors.None)
                                {
                                    position = candidate;
                                    angle = candidateAngle;
                                    errors = ToolBase.ToolErrors.None;
                                    r.Snap = "road";
                                    break;
                                }
                            }
                        }
                    }
                    errors |= info.m_buildingAI.CheckBuildPosition(relocating, ref position, ref angle, waterHeight, elevation, ref connection, out productionRate, out cost);
                    r.Errors = errors;
                    r.Cost = cost;
                    break;
                }

                case BuildingInfo.PlacementMode.Shoreline:
                case BuildingInfo.PlacementMode.ShorelineOrGround:
                {
                    Vector3 shore;
                    Vector3 direction;
                    ToolBase.ToolErrors errors;
                    cost = 0;
                    if (Singleton<TerrainManager>.instance.GetShorePos(position, 50f, out shore, out direction, out waterHeight) &&
                        Singleton<TerrainManager>.instance.GetShorePos(shore, 50f, out shore, out direction, out waterHeight))
                    {
                        position = shore + direction.normalized * info.m_placementOffset;
                        angle = Mathf.Atan2(direction.x, -direction.z);
                        float minY, maxY, buildingY;
                        Building.SampleBuildingHeight(position, angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
                        minY = Mathf.Min(waterHeight, minY);
                        buildingY = Mathf.Max(position.y, buildingY);
                        float groundY = position.y;
                        position.y = buildingY;
                        errors = info.m_buildingAI.CheckBuildPosition(relocating, ref position, ref angle, waterHeight, elevation, ref connection, out productionRate, out cost);
                        errors |= BuildingTool.CheckSpace(info, BuildingInfo.PlacementMode.Shoreline, relocating, position, minY, buildingY + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true, segs, blds);
                        if (groundY - waterHeight > 128f)
                        {
                            errors |= ToolBase.ToolErrors.HeightTooHigh;
                        }
                        if (buildingY <= waterHeight)
                        {
                            errors = (errors & ~(ToolBase.ToolErrors.HeightTooHigh | ToolBase.ToolErrors.CannotConnect | ToolBase.ToolErrors.CannotBuildOnWater)) | ToolBase.ToolErrors.ShoreNotFound;
                        }
                        r.Snap = "shore";
                    }
                    else
                    {
                        errors = ToolBase.ToolErrors.ShoreNotFound;
                    }
                    if (errors != ToolBase.ToolErrors.None && info.m_placementMode == BuildingInfo.PlacementMode.ShorelineOrGround)
                    {
                        // No usable shore within 50 m: the tool then places it like OnGround.
                        tools.ResetColliding();
                        position = target;
                        position.y = Singleton<TerrainManager>.instance.SampleRawHeightSmooth(position);
                        FreePlacement(info, BuildingInfo.PlacementMode.OnGround, relocating, ref position, ref angle, angleDegrees, segs, blds, r, true);
                        break;
                    }
                    r.Errors = errors;
                    r.Cost = cost;
                    break;
                }

                case BuildingInfo.PlacementMode.PathsideOrGround:
                {
                    Vector3 pathPos;
                    Vector3 dir;
                    if (BuildingTool.SnapToPath(position, out pathPos, out dir, (float)Mathf.Min(info.m_cellWidth, info.m_cellLength) * 3.9f, info.m_hasPedestrianPaths))
                    {
                        position = pathPos - dir * info.m_cellLength * 4f;
                        angle = Mathf.Atan2(-dir.x, dir.z);
                        float minY, maxY, buildingY;
                        Building.SampleBuildingHeight(position, angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
                        ToolBase.ToolErrors errors = info.m_buildingAI.CheckBuildPosition(relocating, ref position, ref angle, waterHeight, elevation, ref connection, out productionRate, out cost);
                        errors |= BuildingTool.CheckSpace(info, BuildingInfo.PlacementMode.PathsideOrGround, relocating, position, minY, position.y + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true, segs, blds);
                        if (maxY - minY > info.m_maxHeightOffset)
                        {
                            errors |= ToolBase.ToolErrors.SlopeTooSteep;
                        }
                        r.Errors = errors;
                        r.Cost = cost;
                        r.Snap = "road";
                    }
                    else
                    {
                        FreePlacement(info, BuildingInfo.PlacementMode.OnGround, relocating, ref position, ref angle, angleDegrees, segs, blds, r, true);
                    }
                    break;
                }

                case BuildingInfo.PlacementMode.OnSurface:
                case BuildingInfo.PlacementMode.OnTerrain:
                    FreePlacement(info, info.m_placementMode, relocating, ref position, ref angle, angleDegrees, segs, blds, r, false);
                    break;

                case BuildingInfo.PlacementMode.OnGround:
                    FreePlacement(info, BuildingInfo.PlacementMode.OnGround, relocating, ref position, ref angle, angleDegrees, segs, blds, r, true);
                    break;

                default:
                    // OnWater, Concourse, PitLane: expansion-specific snapping the bridge does
                    // not reproduce. Validate at the requested spot without snapping.
                    FreePlacement(info, info.m_placementMode, relocating, ref position, ref angle, angleDegrees, segs, blds, r, false);
                    break;
            }

            r.Position = position;
            r.Angle = angle;
        }

        private static void FreePlacement(BuildingInfo info, BuildingInfo.PlacementMode mode, ushort relocating,
            ref Vector3 position, ref float angle, float angleDegrees, ulong[] segs, ulong[] blds, Result r, bool checkSlope)
        {
            Quaternion rotation = Quaternion.AngleAxis(angleDegrees, Vector3.down);
            position -= rotation * info.m_centerOffset;
            angle = angleDegrees * Mathf.Deg2Rad;
            float minY, maxY, buildingY;
            Building.SampleBuildingHeight(position, angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
            position.y = buildingY;
            Segment3 connection = default(Segment3);
            int productionRate;
            int cost;
            ToolBase.ToolErrors errors = info.m_buildingAI.CheckBuildPosition(relocating, ref position, ref angle, 0f, 0f, ref connection, out productionRate, out cost);
            errors |= BuildingTool.CheckSpace(info, mode, relocating, position, minY, buildingY + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true, segs, blds);
            if (checkSlope && (errors & ToolBase.ToolErrors.CannotBuildOnWater) == 0 && maxY - minY > info.m_maxHeightOffset)
            {
                errors |= ToolBase.ToolErrors.SlopeTooSteep;
            }
            r.Errors = errors;
            r.Cost = cost;
            r.Snap = "free";
        }

        /// <summary>
        /// The Roadside zone-slot check from BuildingTool.SimulationStep: the slot must be
        /// free on the zone grid (ZoneManager.CheckSpace); if it is off by a cell, slide
        /// along the road up to half the building's width.
        /// </summary>
        private static ToolBase.ToolErrors TryZoneSlot(BuildingInfo info, ushort relocating, ref Vector3 position, float angle,
            ulong[] segs, ulong[] blds, ToolController tools)
        {
            int offset;
            if (Singleton<ZoneManager>.instance.CheckSpace(position, angle, info.m_cellWidth, info.m_cellLength, out offset))
            {
                return CheckAt(info, relocating, ref position, angle, segs, blds);
            }
            if (offset == 0)
            {
                return ToolBase.ToolErrors.GridNotFound;
            }
            Vector3 step = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 8f * Math.Sign(offset);
            int half = info.m_cellWidth >> 1;
            for (int k = 1; k <= half; k++)
            {
                Vector3 candidate = position + step * k;
                if (Singleton<ZoneManager>.instance.CheckSpace(candidate, angle, info.m_cellWidth, info.m_cellLength, out offset))
                {
                    tools.ResetColliding();
                    ToolBase.ToolErrors e = CheckAt(info, relocating, ref candidate, angle, segs, blds);
                    if (e == ToolBase.ToolErrors.None)
                    {
                        position = candidate;
                    }
                    return e;
                }
            }
            return ToolBase.ToolErrors.GridNotFound;
        }

        private static ToolBase.ToolErrors CheckAt(BuildingInfo info, ushort relocating, ref Vector3 position, float angle, ulong[] segs, ulong[] blds)
        {
            float minY, maxY, buildingY;
            Building.SampleBuildingHeight(position, angle, info.m_cellWidth, info.m_cellLength, info, out minY, out maxY, out buildingY);
            Vector3 pos = position;
            pos.y = buildingY;
            ToolBase.ToolErrors e = BuildingTool.CheckSpace(info, info.m_placementMode, relocating, pos, minY, buildingY + info.m_collisionHeight, angle, info.m_cellWidth, info.m_cellLength, true, segs, blds);
            if (maxY - minY > info.m_maxHeightOffset)
            {
                e |= ToolBase.ToolErrors.SlopeTooSteep;
            }
            if (e == ToolBase.ToolErrors.None)
            {
                position.y = buildingY;
            }
            return e;
        }

        /// <summary>
        /// Scans the zone grid around <paramref name="refPos"/> for the closest valid zone
        /// cell and the building slot in front of it. Ported from the private
        /// <c>BuildingTool.FindClosestZone</c> and the grid walk in SimulationStep.
        /// </summary>
        private static void FindClosestZoneSlot(BuildingInfo info, Vector3 refPos, ushort relocating,
            ref float minD, ref float min2, ref Vector3 minPos, ref float minAngle)
        {
            ZoneManager zones = Singleton<ZoneManager>.instance;
            float x0 = refPos.x - 8f;
            float z0 = refPos.z - 8f;
            float x1 = refPos.x + 8f;
            float z1 = refPos.z + 8f;
            int gx0 = Mathf.Max((int)((x0 - 46f) / 64f + 75f), 0);
            int gz0 = Mathf.Max((int)((z0 - 46f) / 64f + 75f), 0);
            int gx1 = Mathf.Min((int)((x1 + 46f) / 64f + 75f), 149);
            int gz1 = Mathf.Min((int)((z1 + 46f) / 64f + 75f), 149);
            for (int i = gz0; i <= gz1; i++)
            {
                for (int j = gx0; j <= gx1; j++)
                {
                    ushort block = zones.m_zoneGrid[i * 150 + j];
                    int guard = 0;
                    while (block != 0)
                    {
                        Vector3 p = zones.m_blocks.m_buffer[block].m_position;
                        float outside = Mathf.Max(Mathf.Max(x0 - 46f - p.x, z0 - 46f - p.z), Mathf.Max(p.x - x1 - 46f, p.z - z1 - 46f));
                        if (outside < 0f && !OwnedByRelocating(block, relocating))
                        {
                            FindClosestZone(info, block, refPos, ref minD, ref min2, ref minPos, ref minAngle);
                        }
                        block = zones.m_blocks.m_buffer[block].m_nextGridBlock;
                        if (++guard >= 49152)
                        {
                            break;
                        }
                    }
                }
            }
        }

        private static bool OwnedByRelocating(ushort block, ushort relocating)
        {
            // The tool skips the moved building's own zone blocks; the bridge does not track
            // building-owned blocks (only some assets have them), so nothing is skipped.
            return false;
        }

        private static void FindClosestZone(BuildingInfo info, ushort block, Vector3 refPos,
            ref float minD, ref float min2, ref Vector3 minPos, ref float minAngle)
        {
            ZoneBlock zoneBlock = Singleton<ZoneManager>.instance.m_blocks.m_buffer[block];
            if (Mathf.Abs(zoneBlock.m_position.x - refPos.x) >= 52f || Mathf.Abs(zoneBlock.m_position.z - refPos.z) >= 52f)
            {
                return;
            }
            int rowCount = zoneBlock.RowCount;
            Vector3 forward = new Vector3(Mathf.Cos(zoneBlock.m_angle), 0f, Mathf.Sin(zoneBlock.m_angle)) * 8f;
            Vector3 side = new Vector3(forward.z, 0f, -forward.x);
            for (int i = 0; i < rowCount; i++)
            {
                Vector3 rowOffset = ((float)i - 3.5f) * side;
                for (int j = 0; j < 4; j++)
                {
                    if ((zoneBlock.m_valid & (1UL << ((i << 3) | j))) == 0)
                    {
                        continue;
                    }
                    Vector3 cell = zoneBlock.m_position + ((float)j - 3.5f) * forward + rowOffset;
                    float d = Mathf.Sqrt((cell.x - refPos.x) * (cell.x - refPos.x) + (cell.z - refPos.z) * (cell.z - refPos.z));
                    float d2 = Vector3.Dot(forward, refPos - zoneBlock.m_position);
                    if (!(d <= minD - 0.2f) && (!(d < minD + 0.2f) || !(d2 < min2)))
                    {
                        continue;
                    }
                    minD = d;
                    min2 = d2;
                    float depth = (float)info.m_cellLength * 0.5f + Mathf.Max(0f, info.m_placementOffset - (float)zoneBlock.Distance) * 0.125f - 4f;
                    if ((info.m_cellWidth & 1) == 0)
                    {
                        Vector3 a = cell + side * 0.5f;
                        Vector3 b = cell - side * 0.5f;
                        float da = (a.x - refPos.x) * (a.x - refPos.x) + (a.z - refPos.z) * (a.z - refPos.z);
                        float db = (b.x - refPos.x) * (b.x - refPos.x) + (b.z - refPos.z) * (b.z - refPos.z);
                        minPos = zoneBlock.m_position + depth * forward + ((float)i - (da < db ? 3f : 4f)) * side;
                    }
                    else
                    {
                        minPos = zoneBlock.m_position + depth * forward + ((float)i - 3.5f) * side;
                    }
                    minPos.y = refPos.y;
                    minAngle = zoneBlock.m_angle + (float)Math.PI / 2f;
                }
            }
        }
    }
}
