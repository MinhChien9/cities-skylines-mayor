using System.Text;
using System.Collections.Generic;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Commands;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    public static class GameState
    {
        public static CommandResult BuildSummaryJson()
        {
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            NetManager net = NetManager.instance;
            ZoneManager zones = ZoneManager.instance;
            CitizenManager citizens = CitizenManager.instance;

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"gameTime\":\"").Append(JsonUtil.Escape(simulation.m_currentGameTime.ToString("s"))).Append("\"");
            json.Append(",\"buildIndex\":").Append(simulation.m_currentBuildIndex);
            json.Append(",\"simulation\":{\"paused\":").Append(JsonUtil.Bool(simulation.SimulationPaused));
            json.Append(",\"selectedSpeed\":").Append(simulation.SelectedSimulationSpeed);
            json.Append(",\"finalSpeed\":").Append(simulation.FinalSimulationSpeed).Append("}");
            json.Append(",\"network\":{\"nodes\":").Append(net.m_nodeCount);
            json.Append(",\"segments\":").Append(net.m_segmentCount);
            json.Append(",\"lanes\":").Append(net.m_laneCount).Append("}");
            json.Append(",\"citizens\":{\"count\":").Append(citizens.m_citizenCount).Append("}");
            json.Append(",\"demand\":{\"residential\":").Append(zones.m_residentialDemand);
            json.Append(",\"commercial\":").Append(zones.m_commercialDemand);
            json.Append(",\"workplace\":").Append(zones.m_workplaceDemand).Append("}");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildDemandJson()
        {
            ZoneManager zones = ZoneManager.instance;
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"residential\":").Append(zones.m_residentialDemand);
            json.Append(",\"commercial\":").Append(zones.m_commercialDemand);
            json.Append(",\"workplace\":").Append(zones.m_workplaceDemand);
            json.Append(",\"bars\":[");
            AppendDemandBar(json, "Residential", "residential", zones.m_residentialDemand, "#7fff00", true);
            AppendDemandBar(json, "Commercial", "commercial", zones.m_commercialDemand, "#30c8ff", false);
            AppendDemandBar(json, "Workplace", "workplace", zones.m_workplaceDemand, "#ffd21a", false);
            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildChirpsJson(int limit)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 200)
            {
                limit = 200;
            }

            MessageManager manager = Singleton<MessageManager>.instance;
            MessageBase[] messages = manager == null ? null : manager.GetRecentMessages();
            int total = 0;
            int emitted = 0;
            bool first = true;
            StringBuilder items = new StringBuilder();

            if (messages != null)
            {
                for (int i = messages.Length - 1; i >= 0; i--)
                {
                    MessageBase message = messages[i];
                    if (message == null)
                    {
                        continue;
                    }

                    total++;
                    if (emitted >= limit)
                    {
                        continue;
                    }

                    if (!first)
                    {
                        items.Append(",");
                    }

                    AppendChirp(items, i, message);
                    first = false;
                    emitted++;
                }
            }

            return CommandResult.FromJson("{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"chirps\":[" + items.ToString() + "]}");
        }

        public static CommandResult BuildZonesJson()
        {
            ZoneManager manager = ZoneManager.instance;
            Dictionary<string, int> counts = new Dictionary<string, int>();
            int createdBlocks = 0;
            int totalCells = 0;
            int zonedCells = 0;

            for (ushort blockId = 1; blockId < manager.m_blocks.m_buffer.Length; blockId++)
            {
                ZoneBlock block = manager.m_blocks.m_buffer[blockId];
                if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0)
                {
                    continue;
                }

                createdBlocks++;
                int rows = ZoneHelpers.GetRowCount(block);

                for (int z = 0; z < rows; z++)
                {
                    for (int x = 0; x < 4; x++)
                    {
                        ItemClass.Zone zone = block.GetZone(x, z);
                        string zoneName = zone.ToString();
                        if (counts.ContainsKey(zoneName))
                        {
                            counts[zoneName]++;
                        }
                        else
                        {
                            counts[zoneName] = 1;
                        }

                        totalCells++;
                        if (zone != ItemClass.Zone.Unzoned)
                        {
                            zonedCells++;
                        }
                    }
                }
            }

            StringBuilder zoneItems = new StringBuilder();
            bool first = true;
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (!first)
                {
                    zoneItems.Append(",");
                }
                AppendZoneSummary(zoneItems, pair.Key, pair.Value);
                first = false;
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"zoneBlockCount\":").Append(createdBlocks);
            json.Append(",\"cellSizeMeters\":8");
            json.Append(",\"cellAreaSquareMeters\":64");
            json.Append(",\"totalCells\":").Append(totalCells);
            json.Append(",\"zonedCells\":").Append(zonedCells);
            json.Append(",\"zonedAreaSquareMeters\":").Append(zonedCells * 64);
            json.Append(",\"zones\":[").Append(zoneItems.ToString()).Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        private static void AppendDemandBar(StringBuilder json, string type, string key, int value, string color, bool first)
        {
            if (!first)
            {
                json.Append(",");
            }
            json.Append("{\"type\":\"").Append(type).Append("\"");
            json.Append(",\"key\":\"").Append(key).Append("\"");
            json.Append(",\"value\":").Append(value);
            json.Append(",\"max\":100");
            json.Append(",\"color\":\"").Append(color).Append("\"}");
        }

        private static void AppendChirp(StringBuilder json, int index, MessageBase message)
        {
            string type = message.GetType().Name;
            string senderName = "";
            string text = "";
            uint senderId = 0;

            try { senderName = message.senderName; } catch { }
            try { text = message.text; } catch { }
            try { senderId = message.senderID; } catch { }

            json.Append("{\"index\":").Append(index);
            json.Append(",\"type\":\"").Append(JsonUtil.Escape(type)).Append("\"");
            json.Append(",\"senderName\":\"").Append(JsonUtil.Escape(senderName)).Append("\"");
            json.Append(",\"senderId\":").Append(senderId);
            json.Append(",\"text\":\"").Append(JsonUtil.Escape(text)).Append("\"");

            CitizenMessage citizen = message as CitizenMessage;
            if (citizen != null)
            {
                json.Append(",\"messageId\":\"").Append(JsonUtil.Escape(citizen.m_messageID)).Append("\"");
                json.Append(",\"keyId\":\"").Append(JsonUtil.Escape(citizen.m_keyID)).Append("\"");
                json.Append(",\"tag\":\"").Append(JsonUtil.Escape(citizen.m_tag)).Append("\"");
            }
            else
            {
                GenericMessage generic = message as GenericMessage;
                if (generic != null)
                {
                    json.Append(",\"messageId\":\"").Append(JsonUtil.Escape(generic.m_messageID)).Append("\"");
                    json.Append(",\"senderKey\":\"").Append(JsonUtil.Escape(generic.m_senderID)).Append("\"");
                    json.Append(",\"randomId\":").Append(generic.m_randomID);
                }
            }

            json.Append("}");
        }

        private static void AppendZoneSummary(StringBuilder json, string zoneName, int cells)
        {
            json.Append("{\"zone\":\"").Append(JsonUtil.Escape(zoneName)).Append("\"");
            json.Append(",\"cells\":").Append(cells);
            json.Append(",\"areaSquareMeters\":").Append(cells * 64);
            json.Append(",\"areaHectares\":").Append(JsonUtil.Number(cells * 0.0064f));
            json.Append("}");
        }

        public static CommandResult BuildRoadPrefabsJson()
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"roads\":[");

            int count = PrefabCollection<NetInfo>.LoadedCount();
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                NetInfo info = PrefabCollection<NetInfo>.GetLoaded((uint)i);
                if (info == null || info.m_class == null)
                {
                    continue;
                }

                if (info.m_class.m_service != ItemClass.Service.Road)
                {
                    continue;
                }

                if (!first)
                {
                    json.Append(",");
                }

                json.Append("{\"name\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                json.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"}");
                first = false;
            }

            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildNetworkPrefabsJson(string serviceFilter)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"networks\":[");

            int count = PrefabCollection<NetInfo>.LoadedCount();
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                NetInfo info = PrefabCollection<NetInfo>.GetLoaded((uint)i);
                if (info == null || info.m_class == null)
                {
                    continue;
                }
                string service = info.m_class.m_service.ToString();
                if (serviceFilter != null && serviceFilter.Length > 0 && service != serviceFilter)
                {
                    continue;
                }

                if (!first)
                {
                    json.Append(",");
                }

                json.Append("{\"name\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                json.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
                json.Append(",\"service\":\"").Append(JsonUtil.Escape(service)).Append("\"");
                json.Append(",\"subService\":\"").Append(JsonUtil.Escape(info.m_class.m_subService.ToString())).Append("\"}");
                first = false;
            }

            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildBuildingPrefabsJson(string serviceFilter)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"buildings\":[");

            int count = PrefabCollection<BuildingInfo>.LoadedCount();
            bool first = true;
            for (int i = 0; i < count; i++)
            {
                BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded((uint)i);
                if (info == null || info.m_class == null)
                {
                    continue;
                }
                if (AssetPolicy.IsBlockedBuildingPrefab(info))
                {
                    continue;
                }

                string service = info.m_class.m_service.ToString();
                if (serviceFilter != null && serviceFilter.Length > 0 && service != serviceFilter)
                {
                    continue;
                }

                if (!first)
                {
                    json.Append(",");
                }

                json.Append("{\"name\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                json.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
                json.Append(",\"service\":\"").Append(JsonUtil.Escape(service)).Append("\"");
                json.Append(",\"subService\":\"").Append(JsonUtil.Escape(info.m_class.m_subService.ToString())).Append("\"");
                json.Append(",\"level\":\"").Append(JsonUtil.Escape(info.m_class.m_level.ToString())).Append("\"");
                json.Append(",\"width\":").Append(info.GetWidth());
                json.Append(",\"length\":").Append(info.GetLength()).Append("}");
                first = false;
            }

            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildProblemsJson(int limit)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 1000)
            {
                limit = 1000;
            }

            ProblemCollector collector = new ProblemCollector(limit);
            collector.CollectBuildings();
            collector.CollectNetNodes();
            collector.CollectNetSegments();
            return CommandResult.FromJson(collector.ToJson());
        }

        public static CommandResult BuildEconomyJson()
        {
            return EconomyCommands.BuildEconomyJson();
        }

        public static CommandResult BuildFacilitiesJson(int limit, string serviceFilter, bool includeMapObjects)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 5000)
            {
                limit = 5000;
            }

            BuildingManager manager = BuildingManager.instance;
            StringBuilder items = new StringBuilder();
            StringBuilder services = new StringBuilder();
            StringBuilder subServices = new StringBuilder();
            Dictionary<string, int> countByService = new Dictionary<string, int>();
            Dictionary<string, int> countBySubService = new Dictionary<string, int>();
            int total = 0;
            int emitted = 0;
            bool firstItem = true;

            for (ushort i = 1; i < manager.m_buildings.m_buffer.Length; i++)
            {
                Building building = manager.m_buildings.m_buffer[i];
                if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }

                BuildingInfo info = building.Info;
                if (info == null || info.m_class == null)
                {
                    continue;
                }

                if (!includeMapObjects && GameInfoHelpers.IsInternalNetworkHelperBuilding(info))
                {
                    continue;
                }

                string service = info.m_class.m_service.ToString();
                string subService = info.m_class.m_subService.ToString();
                string subServiceKey = service + "/" + subService;

                if (!includeMapObjects && (serviceFilter == null || serviceFilter.Length == 0) && !GameInfoHelpers.IsCityFacilityService(service))
                {
                    continue;
                }

                if (serviceFilter != null && serviceFilter.Length > 0 && service != serviceFilter)
                {
                    continue;
                }

                if (countByService.ContainsKey(service))
                {
                    countByService[service]++;
                }
                else
                {
                    countByService[service] = 1;
                }

                if (countBySubService.ContainsKey(subServiceKey))
                {
                    countBySubService[subServiceKey]++;
                }
                else
                {
                    countBySubService[subServiceKey] = 1;
                }

                total++;
                if (emitted >= limit)
                {
                    continue;
                }

                if (!firstItem)
                {
                    items.Append(",");
                }

                string problems = building.m_problems.IsNone ? "" : building.m_problems.ToString();
                Vector3 position = building.m_position;
                items.Append("{\"id\":").Append(i);
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                items.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
                items.Append(",\"blockedAsset\":").Append(JsonUtil.Bool(AssetPolicy.IsBlockedBuildingPrefab(info)));
                items.Append(",\"active\":").Append(JsonUtil.Bool((building.m_flags & Building.Flags.Active) != Building.Flags.None));
                items.Append(",\"flags\":\"").Append(JsonUtil.Escape(building.m_flags.ToString())).Append("\"");
                items.Append(",\"service\":\"").Append(JsonUtil.Escape(service)).Append("\"");
                items.Append(",\"subService\":\"").Append(JsonUtil.Escape(subService)).Append("\"");
                items.Append(",\"level\":\"").Append(JsonUtil.Escape(info.m_class.m_level.ToString())).Append("\"");
                items.Append(",\"width\":").Append(info.GetWidth());
                items.Append(",\"length\":").Append(info.GetLength());
                items.Append(",\"angleDegrees\":").Append(JsonUtil.Number(building.m_angle * Mathf.Rad2Deg));
                items.Append(",\"problems\":\"").Append(JsonUtil.Escape(problems)).Append("\"");
                items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(position.x));
                items.Append(",\"y\":").Append(JsonUtil.Number(position.y));
                items.Append(",\"z\":").Append(JsonUtil.Number(position.z)).Append("}}");
                firstItem = false;
                emitted++;
            }

            bool first = true;
            foreach (KeyValuePair<string, int> pair in countByService)
            {
                if (!first)
                {
                    services.Append(",");
                }
                services.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }

            first = true;
            foreach (KeyValuePair<string, int> pair in countBySubService)
            {
                if (!first)
                {
                    subServices.Append(",");
                }
                subServices.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }

            return CommandResult.FromJson("{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"serviceFilter\":\"" + JsonUtil.Escape(serviceFilter) + "\"" +
                ",\"includeMapObjects\":" + JsonUtil.Bool(includeMapObjects) +
                ",\"countsByService\":{" + services.ToString() + "}" +
                ",\"countsBySubService\":{" + subServices.ToString() + "}" +
                ",\"facilities\":[" + items.ToString() + "]}");
        }

        public static CommandResult BuildGrowablesJson(int limit, string serviceFilter)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 5000)
            {
                limit = 5000;
            }

            BuildingManager manager = BuildingManager.instance;
            StringBuilder items = new StringBuilder();
            StringBuilder services = new StringBuilder();
            StringBuilder subServices = new StringBuilder();
            Dictionary<string, int> countByService = new Dictionary<string, int>();
            Dictionary<string, int> countBySubService = new Dictionary<string, int>();
            int total = 0;
            int emitted = 0;
            bool firstItem = true;

            for (ushort i = 1; i < manager.m_buildings.m_buffer.Length; i++)
            {
                Building building = manager.m_buildings.m_buffer[i];
                if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }

                BuildingInfo info = building.Info;
                if (info == null || info.m_class == null)
                {
                    continue;
                }

                string service = info.m_class.m_service.ToString();
                if (!GameInfoHelpers.IsGrowableService(service))
                {
                    continue;
                }

                if (serviceFilter != null && serviceFilter.Length > 0 && service != serviceFilter)
                {
                    continue;
                }

                string subService = info.m_class.m_subService.ToString();
                string subServiceKey = service + "/" + subService;
                Increment(countByService, service);
                Increment(countBySubService, subServiceKey);

                total++;
                if (emitted >= limit)
                {
                    continue;
                }

                if (!firstItem)
                {
                    items.Append(",");
                }

                string problems = building.m_problems.IsNone ? "" : building.m_problems.ToString();
                Vector3 position = building.m_position;
                items.Append("{\"id\":").Append(i);
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                items.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
                items.Append(",\"active\":").Append(JsonUtil.Bool((building.m_flags & Building.Flags.Active) != Building.Flags.None));
                items.Append(",\"abandoned\":").Append(JsonUtil.Bool((building.m_flags & Building.Flags.Abandoned) != Building.Flags.None));
                items.Append(",\"flags\":\"").Append(JsonUtil.Escape(building.m_flags.ToString())).Append("\"");
                items.Append(",\"service\":\"").Append(JsonUtil.Escape(service)).Append("\"");
                items.Append(",\"subService\":\"").Append(JsonUtil.Escape(subService)).Append("\"");
                items.Append(",\"level\":\"").Append(JsonUtil.Escape(info.m_class.m_level.ToString())).Append("\"");
                items.Append(",\"width\":").Append(info.GetWidth());
                items.Append(",\"length\":").Append(info.GetLength());
                items.Append(",\"angleDegrees\":").Append(JsonUtil.Number(building.m_angle * Mathf.Rad2Deg));
                items.Append(",\"problems\":\"").Append(JsonUtil.Escape(problems)).Append("\"");
                items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(position.x));
                items.Append(",\"y\":").Append(JsonUtil.Number(position.y));
                items.Append(",\"z\":").Append(JsonUtil.Number(position.z)).Append("}}");
                firstItem = false;
                emitted++;
            }

            AppendCountMap(services, countByService);
            AppendCountMap(subServices, countBySubService);

            return CommandResult.FromJson("{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"serviceFilter\":\"" + JsonUtil.Escape(serviceFilter) + "\"" +
                ",\"countsByService\":{" + services.ToString() + "}" +
                ",\"countsBySubService\":{" + subServices.ToString() + "}" +
                ",\"growables\":[" + items.ToString() + "]}");
        }

        public static CommandResult BuildNetworksJson(int limit, string serviceFilter)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 5000)
            {
                limit = 5000;
            }

            NetManager manager = NetManager.instance;
            StringBuilder items = new StringBuilder();
            StringBuilder services = new StringBuilder();
            Dictionary<string, int> countByService = new Dictionary<string, int>();
            int total = 0;
            int emitted = 0;
            bool firstItem = true;

            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if ((segment.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }

                NetInfo info = segment.Info;
                if (info == null || info.m_class == null)
                {
                    continue;
                }

                string service = info.m_class.m_service.ToString();
                if (serviceFilter != null && serviceFilter.Length > 0 && service != serviceFilter)
                {
                    continue;
                }

                if (countByService.ContainsKey(service))
                {
                    countByService[service]++;
                }
                else
                {
                    countByService[service] = 1;
                }

                total++;
                if (emitted >= limit)
                {
                    continue;
                }

                ushort startNodeId = segment.m_startNode;
                ushort endNodeId = segment.m_endNode;
                Vector3 start = manager.m_nodes.m_buffer[startNodeId].m_position;
                Vector3 end = manager.m_nodes.m_buffer[endNodeId].m_position;
                Vector3 middle = segment.m_middlePosition;
                string problems = segment.m_problems.IsNone ? "" : segment.m_problems.ToString();
                string segmentName = manager.GetSegmentName(i);

                if (!firstItem)
                {
                    items.Append(",");
                }

                items.Append("{\"id\":").Append(i);
                items.Append(",\"prefab\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
                items.Append(",\"name\":\"").Append(JsonUtil.Escape(segmentName)).Append("\"");
                items.Append(",\"displayName\":\"").Append(JsonUtil.Escape(info.GetUncheckedLocalizedTitle())).Append("\"");
                items.Append(",\"service\":\"").Append(JsonUtil.Escape(service)).Append("\"");
                items.Append(",\"subService\":\"").Append(JsonUtil.Escape(info.m_class.m_subService.ToString())).Append("\"");
                items.Append(",\"problems\":\"").Append(JsonUtil.Escape(problems)).Append("\"");
                items.Append(",\"startNodeId\":").Append(startNodeId);
                items.Append(",\"endNodeId\":").Append(endNodeId);
                items.Append(",\"start\":{\"x\":").Append(JsonUtil.Number(start.x));
                items.Append(",\"y\":").Append(JsonUtil.Number(start.y));
                items.Append(",\"z\":").Append(JsonUtil.Number(start.z)).Append("}");
                items.Append(",\"end\":{\"x\":").Append(JsonUtil.Number(end.x));
                items.Append(",\"y\":").Append(JsonUtil.Number(end.y));
                items.Append(",\"z\":").Append(JsonUtil.Number(end.z)).Append("}");
                items.Append(",\"middle\":{\"x\":").Append(JsonUtil.Number(middle.x));
                items.Append(",\"y\":").Append(JsonUtil.Number(middle.y));
                items.Append(",\"z\":").Append(JsonUtil.Number(middle.z)).Append("}}");
                firstItem = false;
                emitted++;
            }

            bool first = true;
            foreach (KeyValuePair<string, int> pair in countByService)
            {
                if (!first)
                {
                    services.Append(",");
                }
                services.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }

            return CommandResult.FromJson("{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"serviceFilter\":\"" + JsonUtil.Escape(serviceFilter) + "\"" +
                ",\"countsByService\":{" + services.ToString() + "}" +
                ",\"segments\":[" + items.ToString() + "]}");
        }

        public static CommandResult BuildRoadAnomaliesJson(int limit, float nearMissDistance, float shortSegmentLength, bool includeDeadEnds)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 1000)
            {
                limit = 1000;
            }
            if (nearMissDistance < 1f)
            {
                nearMissDistance = 1f;
            }
            if (shortSegmentLength < 1f)
            {
                shortSegmentLength = 1f;
            }

            RoadAnomalyCollector collector = new RoadAnomalyCollector(limit, nearMissDistance, shortSegmentLength, includeDeadEnds);
            collector.Collect();
            return CommandResult.FromJson(collector.ToJson());
        }

        public static CommandResult BuildExternalConnectionsJson(int limit)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 200)
            {
                limit = 200;
            }

            ExternalConnectionCollector collector = new ExternalConnectionCollector(limit);
            collector.Collect();
            return CommandResult.FromJson(collector.ToJson());
        }

        public static CommandResult BuildBuildingAnomaliesJson(int limit)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 1000)
            {
                limit = 1000;
            }

            BuildingAnomalyCollector collector = new BuildingAnomalyCollector(limit);
            collector.Collect();
            return CommandResult.FromJson(collector.ToJson());
        }

        public static CommandResult BuildZoneAnomaliesJson(int limit, int minMinorityCells, int minUnzonedCells, bool includeUnzonedHoles)
        {
            if (limit < 0)
            {
                limit = 0;
            }
            if (limit > 1000)
            {
                limit = 1000;
            }
            if (minMinorityCells < 1)
            {
                minMinorityCells = 1;
            }
            if (minUnzonedCells < 1)
            {
                minUnzonedCells = 1;
            }

            ZoneAnomalyCollector collector = new ZoneAnomalyCollector(limit, minMinorityCells, minUnzonedCells, includeUnzonedHoles);
            collector.Collect();
            return CommandResult.FromJson(collector.ToJson());
        }
        private static void Increment(Dictionary<string, int> counts, string key)
        {
            if (counts.ContainsKey(key))
            {
                counts[key]++;
            }
            else
            {
                counts[key] = 1;
            }
        }

        private static void AppendCountMap(StringBuilder json, Dictionary<string, int> counts)
        {
            bool first = true;
            foreach (KeyValuePair<string, int> pair in counts)
            {
                if (!first)
                {
                    json.Append(",");
                }
                json.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                first = false;
            }
        }
    }
}
