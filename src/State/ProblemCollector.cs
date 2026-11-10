using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    internal sealed class ProblemCollector
    {
        private readonly int limit;
        private readonly StringBuilder items = new StringBuilder();
        private readonly StringBuilder counts = new StringBuilder();
        private readonly StringBuilder nameCounts = new StringBuilder();
        private int emitted;
        private int total;
        private bool firstItem = true;
        private bool firstCount = true;
        private bool firstNameCount = true;
        private readonly Dictionary<string, int> countByProblem = new Dictionary<string, int>();
        private readonly Dictionary<string, int> countByProblemName = new Dictionary<string, int>();

        public ProblemCollector(int limit)
        {
            this.limit = limit;
        }

        public void CollectBuildings()
        {
            BuildingManager manager = BuildingManager.instance;
            for (ushort i = 1; i < manager.m_buildings.m_buffer.Length; i++)
            {
                Building building = manager.m_buildings.m_buffer[i];
                if ((building.m_flags & Building.Flags.Created) == Building.Flags.None)
                {
                    continue;
                }
                Add("building", i, building.m_position, building.m_problems, building.m_flags, true);
            }
        }

        public void CollectNetNodes()
        {
            NetManager manager = NetManager.instance;
            for (ushort i = 1; i < manager.m_nodes.m_buffer.Length; i++)
            {
                NetNode node = manager.m_nodes.m_buffer[i];
                if ((node.m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
                {
                    continue;
                }
                Add("netNode", i, node.m_position, node.m_problems, Building.Flags.None, false);
            }
        }

        public void CollectNetSegments()
        {
            NetManager manager = NetManager.instance;
            for (ushort i = 1; i < manager.m_segments.m_buffer.Length; i++)
            {
                NetSegment segment = manager.m_segments.m_buffer[i];
                if ((segment.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                {
                    continue;
                }
                Add("netSegment", i, segment.m_middlePosition, segment.m_problems, Building.Flags.None, false);
            }
        }

        public string ToJson()
        {
            foreach (KeyValuePair<string, int> pair in countByProblem)
            {
                if (!firstCount)
                {
                    counts.Append(",");
                }
                counts.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                firstCount = false;
            }

            foreach (KeyValuePair<string, int> pair in countByProblemName)
            {
                if (!firstNameCount)
                {
                    nameCounts.Append(",");
                }
                nameCounts.Append("\"").Append(JsonUtil.Escape(pair.Key)).Append("\":").Append(pair.Value);
                firstNameCount = false;
            }

            return "{\"ok\":true,\"total\":" + total +
                ",\"returned\":" + emitted +
                ",\"limit\":" + limit +
                ",\"counts\":{" + counts.ToString() + "}" +
                ",\"countsByProblem\":{" + nameCounts.ToString() + "}" +
                ",\"problems\":[" + items.ToString() + "]}";
        }

        private void Add(string entityType, ushort id, Vector3 position, Notification.ProblemStruct problems, Building.Flags buildingFlags, bool includeBuildingFlags)
        {
            bool hasBuildingFlagAlert = includeBuildingFlags && HasBuildingFlagAlerts(buildingFlags);
            if (problems.IsNone && !hasBuildingFlagAlert)
            {
                return;
            }

            string problemText = BuildProblemText(problems, buildingFlags, hasBuildingFlagAlert);
            string problemNamesJson = BuildProblemNamesJson(problems, buildingFlags, hasBuildingFlagAlert);
            total++;

            if (countByProblem.ContainsKey(problemText))
            {
                countByProblem[problemText]++;
            }
            else
            {
                countByProblem[problemText] = 1;
            }

            CountProblemNames(problems);
            CountBuildingFlagNames(buildingFlags, hasBuildingFlagAlert);

            if (emitted >= limit)
            {
                return;
            }

            if (!firstItem)
            {
                items.Append(",");
            }

            items.Append("{\"entityType\":\"").Append(entityType).Append("\"");
            items.Append(",\"id\":").Append(id);
            items.Append(",\"problems\":\"").Append(JsonUtil.Escape(problemText)).Append("\"");
            items.Append(",\"problemNames\":").Append(problemNamesJson);
            items.Append(",\"problem1Raw\":").Append(((ulong)problems.m_Problems1).ToString());
            items.Append(",\"problem2Raw\":").Append(((ulong)problems.m_Problems2).ToString());
            if (includeBuildingFlags)
            {
                items.Append(",\"buildingFlags\":\"").Append(JsonUtil.Escape(BuildingFlagsText(buildingFlags))).Append("\"");
                items.Append(",\"buildingFlagsRaw\":").Append(((ulong)buildingFlags).ToString());
            }
            items.Append(",\"isMajor\":").Append(JsonUtil.Bool(problems.IsMajor));
            items.Append(",\"isFatal\":").Append(JsonUtil.Bool(problems.IsFatal));
            items.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(position.x));
            items.Append(",\"y\":").Append(JsonUtil.Number(position.y));
            items.Append(",\"z\":").Append(JsonUtil.Number(position.z)).Append("}}");

            emitted++;
            firstItem = false;
        }

        private string BuildProblemText(Notification.ProblemStruct problems, Building.Flags buildingFlags, bool hasBuildingFlagAlert)
        {
            string text = problems.IsNone ? "" : problems.ToString();
            if (!hasBuildingFlagAlert)
            {
                return text;
            }

            string flagsText = BuildingFlagAlertsText(buildingFlags);
            if (text.Length == 0)
            {
                return flagsText;
            }
            return text + ", " + flagsText;
        }

        private void CountProblemNames(Notification.ProblemStruct problems)
        {
            AddProblemNameCounts(typeof(Notification.Problem1), (ulong)problems.m_Problems1);
            AddProblemNameCounts(typeof(Notification.Problem2), (ulong)problems.m_Problems2);
        }

        private void AddProblemNameCounts(Type enumType, ulong flags)
        {
            System.Array values = System.Enum.GetValues(enumType);
            for (int i = 0; i < values.Length; i++)
            {
                object value = values.GetValue(i);
                string name = value.ToString();
                if (name == "None" || name == "All")
                {
                    continue;
                }

                ulong bit = System.Convert.ToUInt64(value);
                if ((flags & bit) == 0)
                {
                    continue;
                }

                if (countByProblemName.ContainsKey(name))
                {
                    countByProblemName[name]++;
                }
                else
                {
                    countByProblemName[name] = 1;
                }
            }
        }

        private string BuildProblemNamesJson(Notification.ProblemStruct problems, Building.Flags buildingFlags, bool hasBuildingFlagAlert)
        {
            StringBuilder names = new StringBuilder();
            bool first = true;
            AppendProblemNames(names, typeof(Notification.Problem1), (ulong)problems.m_Problems1, ref first);
            AppendProblemNames(names, typeof(Notification.Problem2), (ulong)problems.m_Problems2, ref first);
            if (hasBuildingFlagAlert)
            {
                AppendBuildingFlagNames(names, buildingFlags, ref first);
            }
            return "[" + names.ToString() + "]";
        }

        private void AppendProblemNames(StringBuilder names, Type enumType, ulong flags, ref bool first)
        {
            System.Array values = System.Enum.GetValues(enumType);
            for (int i = 0; i < values.Length; i++)
            {
                object value = values.GetValue(i);
                string name = value.ToString();
                if (name == "None" || name == "All")
                {
                    continue;
                }

                ulong bit = System.Convert.ToUInt64(value);
                if ((flags & bit) == 0)
                {
                    continue;
                }

                if (!first)
                {
                    names.Append(",");
                }
                names.Append("\"").Append(JsonUtil.Escape(name)).Append("\"");
                first = false;
            }
        }

        private bool HasBuildingFlagAlerts(Building.Flags flags)
        {
            return (flags & Building.Flags.Abandoned) != Building.Flags.None ||
                (flags & Building.Flags.BurnedDown) != Building.Flags.None ||
                (flags & Building.Flags.Collapsed) != Building.Flags.None ||
                (flags & Building.Flags.Flooded) != Building.Flags.None ||
                (flags & Building.Flags.RoadAccessFailed) != Building.Flags.None;
        }

        private void CountBuildingFlagNames(Building.Flags flags, bool hasBuildingFlagAlert)
        {
            if (!hasBuildingFlagAlert)
            {
                return;
            }

            CountBuildingFlagName(flags, Building.Flags.Abandoned, "Abandoned");
            CountBuildingFlagName(flags, Building.Flags.BurnedDown, "BurnedDown");
            CountBuildingFlagName(flags, Building.Flags.Collapsed, "Collapsed");
            CountBuildingFlagName(flags, Building.Flags.Flooded, "Flooded");
            CountBuildingFlagName(flags, Building.Flags.RoadAccessFailed, "RoadAccessFailed");
        }

        private void CountBuildingFlagName(Building.Flags flags, Building.Flags flag, string name)
        {
            if ((flags & flag) == Building.Flags.None)
            {
                return;
            }

            if (countByProblemName.ContainsKey(name))
            {
                countByProblemName[name]++;
            }
            else
            {
                countByProblemName[name] = 1;
            }
        }

        private void AppendBuildingFlagNames(StringBuilder names, Building.Flags flags, ref bool first)
        {
            AppendBuildingFlagName(names, flags, Building.Flags.Abandoned, "Abandoned", ref first);
            AppendBuildingFlagName(names, flags, Building.Flags.BurnedDown, "BurnedDown", ref first);
            AppendBuildingFlagName(names, flags, Building.Flags.Collapsed, "Collapsed", ref first);
            AppendBuildingFlagName(names, flags, Building.Flags.Flooded, "Flooded", ref first);
            AppendBuildingFlagName(names, flags, Building.Flags.RoadAccessFailed, "RoadAccessFailed", ref first);
        }

        private void AppendBuildingFlagName(StringBuilder names, Building.Flags flags, Building.Flags flag, string name, ref bool first)
        {
            if ((flags & flag) == Building.Flags.None)
            {
                return;
            }

            if (!first)
            {
                names.Append(",");
            }
            names.Append("\"").Append(JsonUtil.Escape(name)).Append("\"");
            first = false;
        }

        private string BuildingFlagAlertsText(Building.Flags flags)
        {
            StringBuilder names = new StringBuilder();
            bool first = true;
            AppendBuildingFlagText(names, flags, Building.Flags.Abandoned, "Abandoned", ref first);
            AppendBuildingFlagText(names, flags, Building.Flags.BurnedDown, "BurnedDown", ref first);
            AppendBuildingFlagText(names, flags, Building.Flags.Collapsed, "Collapsed", ref first);
            AppendBuildingFlagText(names, flags, Building.Flags.Flooded, "Flooded", ref first);
            AppendBuildingFlagText(names, flags, Building.Flags.RoadAccessFailed, "RoadAccessFailed", ref first);
            return names.ToString();
        }

        private string BuildingFlagsText(Building.Flags flags)
        {
            return flags.ToString();
        }

        private void AppendBuildingFlagText(StringBuilder names, Building.Flags flags, Building.Flags flag, string name, ref bool first)
        {
            if ((flags & flag) == Building.Flags.None)
            {
                return;
            }

            if (!first)
            {
                names.Append(", ");
            }
            names.Append(name);
            first = false;
        }
    }
}
