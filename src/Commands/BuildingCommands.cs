using System;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
using SkylinesAgentBridge.State;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    public static class BuildingCommands
    {
        public static CommandResult PlaceBuilding(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            string prefabName = JsonUtil.GetString(body, "buildingPrefab", "");
            float angleDegrees = JsonUtil.GetNumber(body, "angleDegrees", 0f);
            Vector3 target = CommandInput.ReadPoint(body, "position");

            if (prefabName == null || prefabName.Length == 0)
            {
                return CommandResult.Fail("buildingPrefab is required.");
            }

            BuildingInfo info = PrefabCollection<BuildingInfo>.FindLoaded(prefabName);
            if (info == null)
            {
                return CommandResult.Fail("Building prefab was not found: " + prefabName);
            }
            if (AssetPolicy.IsBlockedBuildingPrefab(info))
            {
                return CommandResult.Fail("Building prefab is blocked and must not be used: " + prefabName + " (" + AssetPolicy.BlockReason(prefabName) + ")");
            }

            // Resolve exactly where the building tool would put it, and what it would say.
            BuildingPlacement.Result placement = BuildingPlacement.Resolve(info, target, angleDegrees, 0);

            if (placement.Errors != ToolBase.ToolErrors.None || dryRun)
            {
                return CommandResult.FromJson("{\"ok\":" + JsonUtil.Bool(placement.Errors == ToolBase.ToolErrors.None) +
                    ",\"dryRun\":" + JsonUtil.Bool(dryRun) +
                    ",\"built\":false" +
                    ",\"buildingPrefab\":\"" + JsonUtil.Escape(prefabName) + "\"" +
                    ",\"placementMode\":\"" + info.m_placementMode + "\"" +
                    PlacementJson(placement) +
                    "," + GameValidation.ErrorFields(placement.Errors) + "}");
            }

            int moneyBefore = RoadCommands.MoneyNow();
            BuildingPlacement.ClearSite(info, placement, 0);

            if (GameValidation.NeedMoney() && placement.Cost > 0)
            {
                Singleton<EconomyManager>.instance.FetchResource(EconomyManager.Resource.Construction, placement.Cost, info.m_class);
            }

            ushort buildingId = CreateWithSubBuildings(info, placement.Position, placement.Angle);
            if (buildingId == 0)
            {
                return CommandResult.Fail("The game refused to create the building (object limit?).");
            }

            int publicServiceIndex = ItemClass.GetPublicServiceIndex(info.m_class.m_service);
            if (publicServiceIndex != -1)
            {
                Singleton<CoverageManager>.instance.CoverageUpdated(info.m_class.m_service, info.m_class.m_subService, info.m_class.m_level);
            }
            BuildingTool.DispatchPlacementEffect(info, 0, placement.Position, placement.Angle, info.m_cellWidth, info.m_cellLength, false, false);

            Debug.Log("[SkylinesAgentBridge] Placed building " + buildingId + " (" + prefabName + "), cost " + placement.Cost);

            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":false,\"built\":true" +
                ",\"buildingId\":" + buildingId +
                ",\"buildingPrefab\":\"" + JsonUtil.Escape(prefabName) + "\"" +
                ",\"service\":\"" + JsonUtil.Escape(info.m_class.m_service.ToString()) + "\"" +
                ",\"subService\":\"" + JsonUtil.Escape(info.m_class.m_subService.ToString()) + "\"" +
                ",\"placementMode\":\"" + info.m_placementMode + "\"" +
                PlacementJson(placement) +
                ",\"moneyBefore\":" + moneyBefore +
                ",\"moneyAfter\":" + RoadCommands.MoneyNow() + "}");
        }

        /// <summary>
        /// Creates the building and its sub-buildings the way <c>BuildingTool.CreateBuilding</c>
        /// does. Returns the main building id, or 0.
        /// </summary>
        private static ushort CreateWithSubBuildings(BuildingInfo info, Vector3 position, float angle)
        {
            BuildingManager buildings = Singleton<BuildingManager>.instance;
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            ushort building;
            if (!buildings.CreateBuilding(out building, ref simulation.m_randomizer, info, position, angle, 0, simulation.m_currentBuildIndex))
            {
                return 0;
            }
            if (info.m_fixedHeight)
            {
                buildings.m_buildings.m_buffer[building].m_flags |= Building.Flags.FixedHeight;
            }
            simulation.m_currentBuildIndex++;

            ushort main = building;
            if (info.m_subBuildings != null && info.m_subBuildings.Length != 0)
            {
                Matrix4x4 matrix = default(Matrix4x4);
                matrix.SetTRS(position, Quaternion.AngleAxis(angle * 57.29578f, Vector3.down), Vector3.one);
                for (int i = 0; i < info.m_subBuildings.Length; i++)
                {
                    BuildingInfo subInfo = info.m_subBuildings[i].m_buildingInfo;
                    if (subInfo == null)
                    {
                        continue;
                    }
                    Vector3 subPosition = matrix.MultiplyPoint(info.m_subBuildings[i].m_position);
                    float subAngle = info.m_subBuildings[i].m_angle * ((float)Math.PI / 180f) + angle;
                    Segment3 connection = default(Segment3);
                    int productionRate;
                    int cost;
                    subInfo.m_buildingAI.CheckBuildPosition(0, ref subPosition, ref subAngle, 0f, 0f, ref connection, out productionRate, out cost);
                    ushort sub;
                    if (buildings.CreateBuilding(out sub, ref simulation.m_randomizer, subInfo, subPosition, subAngle, 0, simulation.m_currentBuildIndex))
                    {
                        if (info.m_subBuildings[i].m_fixedHeight)
                        {
                            buildings.m_buildings.m_buffer[sub].m_flags |= Building.Flags.FixedHeight;
                        }
                        simulation.m_currentBuildIndex++;
                        buildings.m_buildings.m_buffer[building].m_subBuilding = sub;
                        buildings.m_buildings.m_buffer[sub].m_parentBuilding = building;
                        buildings.m_buildings.m_buffer[sub].m_flags |= Building.Flags.Untouchable;
                        building = sub;
                    }
                }
            }
            return main;
        }

        private static string PlacementJson(BuildingPlacement.Result r)
        {
            float degrees = r.Angle * Mathf.Rad2Deg;
            degrees = ((degrees % 360f) + 360f) % 360f;
            return ",\"cost\":" + (r.Cost / 100) +
                ",\"snappedTo\":\"" + r.Snap + "\"" +
                ",\"position\":{\"x\":" + JsonUtil.Number(r.Position.x) +
                ",\"y\":" + JsonUtil.Number(r.Position.y) +
                ",\"z\":" + JsonUtil.Number(r.Position.z) + "}" +
                ",\"angleDegrees\":" + JsonUtil.Number(degrees);
        }

        public static CommandResult MoveBuilding(string body)
        {
            ushort id = (ushort)JsonUtil.GetNumber(body, "id", 0f);
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);

            if (id == 0)
            {
                return CommandResult.Fail("id is required.");
            }

            BuildingManager buildings = BuildingManager.instance;
            if ((buildings.m_buildings.m_buffer[id].m_flags & Building.Flags.Created) == Building.Flags.None)
            {
                return CommandResult.Fail("Building was not found: " + id);
            }

            Building oldBuilding = buildings.m_buildings.m_buffer[id];
            BuildingInfo info = oldBuilding.Info;
            if (info == null)
            {
                return CommandResult.Fail("Building prefab info was not found for building: " + id);
            }
            if (AssetPolicy.IsBlockedBuildingPrefab(info))
            {
                return CommandResult.Fail("Building prefab is blocked and must not be used for move/recreate: " + info.name + " (" + AssetPolicy.BlockReason(info.name) + ")");
            }

            Vector3 target = CommandInput.ReadPoint(body, "position");
            float angleDegrees = JsonUtil.GetNumber(body, "angleDegrees", oldBuilding.m_angle * Mathf.Rad2Deg);

            // Same validation as placing it fresh, with the building itself excluded from
            // the collision check (relocating = id), like the move tool does.
            BuildingPlacement.Result placement = BuildingPlacement.Resolve(info, target, angleDegrees, id);
            placement.Errors &= ~ToolBase.ToolErrors.NotEnoughMoney; // relocation is free
            if (placement.Errors != ToolBase.ToolErrors.None || dryRun)
            {
                return CommandResult.FromJson("{\"ok\":" + JsonUtil.Bool(placement.Errors == ToolBase.ToolErrors.None) +
                    ",\"dryRun\":" + JsonUtil.Bool(dryRun) +
                    ",\"moved\":false,\"id\":" + id +
                    ",\"buildingPrefab\":\"" + JsonUtil.Escape(info.name) + "\"" +
                    PlacementJson(placement) +
                    "," + GameValidation.ErrorFields(placement.Errors) + "}");
            }

            BuildingPlacement.ClearSite(info, placement, id);
            Singleton<BuildingManager>.instance.RelocateBuilding(id, placement.Position, placement.Angle);

            Debug.Log("[SkylinesAgentBridge] Relocated building " + id + " (" + info.name + ")");
            return CommandResult.FromJson("{\"ok\":true,\"dryRun\":false,\"moved\":true,\"id\":" + id +
                ",\"buildingPrefab\":\"" + JsonUtil.Escape(info.name) + "\"" +
                PlacementJson(placement) + "}");
        }

        public static CommandResult SetBuildingActive(string body)
        {
            ushort id = (ushort)JsonUtil.GetNumber(body, "id", 0f);
            bool active = JsonUtil.GetBool(body, "active", true);

            if (id == 0)
            {
                return CommandResult.Fail("id is required.");
            }

            BuildingManager buildings = BuildingManager.instance;
            Building building = buildings.m_buildings.m_buffer[id];
            if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
            {
                return CommandResult.Fail("Building was not found: " + id);
            }

            Building.Flags before = building.m_flags;
            BuildingInfo info = building.Info;
            if (info != null && info.m_buildingAI != null)
            {
                InvokeManualActivation(info.m_buildingAI, id, ref building, active);
            }

            if (active)
            {
                building.m_flags |= Building.Flags.Active;
            }
            else
            {
                building.m_flags &= ~Building.Flags.Active;
            }
            buildings.m_buildings.m_buffer[id] = building;

            string prefab = info == null ? "" : info.name;
            string json = "{\"ok\":true,\"id\":" + id +
                ",\"active\":" + JsonUtil.Bool(active) +
                ",\"prefab\":\"" + JsonUtil.Escape(prefab) + "\"" +
                ",\"beforeFlags\":\"" + JsonUtil.Escape(before.ToString()) + "\"" +
                ",\"afterFlags\":\"" + JsonUtil.Escape(building.m_flags.ToString()) + "\"}";
            Debug.Log("[SkylinesAgentBridge] Set building " + id + " active=" + active);
            return CommandResult.FromJson(json);
        }

        private static void InvokeManualActivation(BuildingAI ai, ushort id, ref Building building, bool active)
        {
            string methodName = active ? "ManualActivation" : "ManualDeactivation";
            MethodInfo method = ai.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                method = typeof(BuildingAI).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (method == null)
            {
                return;
            }

            object[] args = new object[] { id, building };
            method.Invoke(ai, args);
            building = (Building)args[1];
        }
    }
}
