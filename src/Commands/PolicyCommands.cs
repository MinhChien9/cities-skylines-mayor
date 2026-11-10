using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// GET /state/policies and POST /commands/set-policy.
    ///
    /// <c>DistrictPolicies.Policies</c> is one 127-value enum: district specializations
    /// (Forest..Financial) sit at raw values 0..12 and the boolean / level policies
    /// start at 32. The game's own grouping enums — Services, Taxation, CityPlanning,
    /// Park, Event — repeat the same policy names, so the catalogue derives each
    /// policy's group by name, and a name that matches no group is reported as
    /// Specialization / Reserved / Other.
    ///
    /// Every public call used here was verified against the live game's
    /// Assembly-CSharp.dll by reflection (2026-11-11):
    ///   IsCityPolicySet / SetCityPolicy / UnsetCityPolicy(Policies)
    ///   IsDistrictPolicySet / SetDistrictPolicy / UnsetDistrictPolicy(Policies, Byte)
    ///   IsParkPolicySet / SetParkPolicy / UnsetParkPolicy(Policies, Byte)
    ///   IsPolicyLoaded(Policies)
    /// </summary>
    public static class PolicyCommands
    {
        private const int SpecializationMaxValue = 12;
        private const int ReservedValue = 128;

        public static CommandResult BuildPoliciesJson()
        {
            DistrictManager manager = Singleton<DistrictManager>.instance;

            Dictionary<string, string> groupsByName = BuildGroupMap();
            List<int> districtIds = new List<int>();
            List<string> districtNames = new List<string>();
            CollectDistricts(manager, districtIds, districtNames);

            int loadedCount = 0;
            int cityWideCount = 0;
            int policyCount = 0;
            StringBuilder items = new StringBuilder();
            bool firstItem = true;

            foreach (DistrictPolicies.Policies policy in Enum.GetValues(typeof(DistrictPolicies.Policies)))
            {
                if (policy == DistrictPolicies.Policies.None)
                {
                    continue;
                }

                bool loaded = manager.IsPolicyLoaded(policy);
                bool cityWide = manager.IsCityPolicySet(policy);

                policyCount++;
                if (loaded)
                {
                    loadedCount++;
                }
                if (cityWide)
                {
                    cityWideCount++;
                }

                if (!firstItem)
                {
                    items.Append(",");
                }
                AppendPolicy(items, manager, policy, groupsByName, districtIds, districtNames, cityWide);
                firstItem = false;
            }

            StringBuilder districtsJson = new StringBuilder();
            for (int i = 0; i < districtIds.Count; i++)
            {
                if (i > 0)
                {
                    districtsJson.Append(",");
                }
                districtsJson.Append("{\"id\":").Append(districtIds[i]);
                districtsJson.Append(",\"name\":\"").Append(JsonUtil.Escape(districtNames[i])).Append("\"}");
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"policyCount\":").Append(policyCount);
            json.Append(",\"loadedCount\":").Append(loadedCount);
            json.Append(",\"cityWideCount\":").Append(cityWideCount);
            json.Append(",\"districtCount\":").Append(districtIds.Count);
            json.Append(",\"districts\":[").Append(districtsJson.ToString()).Append("]");
            json.Append(",\"policies\":[").Append(items.ToString()).Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult SetPolicy(string body)
        {
            string policyText = JsonUtil.GetString(body, "policy", "").Trim();
            bool set = JsonUtil.GetBool(body, "set", true);
            int districtId = (int)JsonUtil.GetNumber(body, "district", 0f);
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);

            if (string.IsNullOrEmpty(policyText))
            {
                return CommandResult.Fail("policy is required, e.g. \"Recycling\". Read GET /state/policies for the catalogue.");
            }

            DistrictPolicies.Policies policy;
            if (!TryParsePolicy(policyText, out policy))
            {
                return CommandResult.Fail("Unknown policy: " + policyText +
                    ". Read GET /state/policies for the exact names.");
            }
            if (policy == DistrictPolicies.Policies.None)
            {
                return CommandResult.Fail("None is not a usable policy value.");
            }
            if ((int)policy == ReservedValue)
            {
                return CommandResult.Fail("Reserved is not a usable policy value.");
            }

            DistrictManager manager = Singleton<DistrictManager>.instance;
            bool loaded = manager.IsPolicyLoaded(policy);

            if (set && districtId != 0)
            {
                List<int> districtIds = new List<int>();
                List<string> districtNames = new List<string>();
                CollectDistricts(manager, districtIds, districtNames);

                bool exists = false;
                string districtName = "";
                for (int i = 0; i < districtIds.Count; i++)
                {
                    if (districtIds[i] == districtId)
                    {
                        exists = true;
                        districtName = districtNames[i];
                    }
                }
                if (!exists)
                {
                    return CommandResult.Fail("District " + districtId + " does not exist. Created districts: " +
                        DescribeDistricts(districtIds, districtNames) +
                        ". District 0 (or no district key) means city-wide.");
                }

                bool isParkPolicy = GroupsFor(policy).IndexOf("Park", StringComparison.Ordinal) >= 0;
                return ApplyPolicy(manager, policy, set, dryRun, loaded, districtId, districtName, isParkPolicy);
            }

            return ApplyPolicy(manager, policy, set, dryRun, loaded, 0, "", false);
        }

        private static CommandResult ApplyPolicy(DistrictManager manager, DistrictPolicies.Policies policy,
            bool set, bool dryRun, bool loaded, int districtId, string districtName, bool parkScope)
        {
            if (set && !loaded)
            {
                return CommandResult.Fail("Policy \"" + policy + "\" is not loaded yet (loaded:false), so setting it " +
                    "would be a silent no-op. It becomes available when its unlock condition is met; see GET /state/policies.");
            }

            bool before;
            if (parkScope)
            {
                before = manager.IsParkPolicySet(policy, (byte)districtId);
            }
            else if (districtId != 0)
            {
                before = manager.IsDistrictPolicySet(policy, (byte)districtId);
            }
            else
            {
                before = manager.IsCityPolicySet(policy);
            }

            bool after = before;
            if (!dryRun)
            {
                if (parkScope)
                {
                    if (set)
                    {
                        manager.SetParkPolicy(policy, (byte)districtId);
                    }
                    else
                    {
                        manager.UnsetParkPolicy(policy, (byte)districtId);
                    }
                }
                else if (districtId != 0)
                {
                    if (set)
                    {
                        manager.SetDistrictPolicy(policy, (byte)districtId);
                    }
                    else
                    {
                        manager.UnsetDistrictPolicy(policy, (byte)districtId);
                    }
                }
                else
                {
                    if (set)
                    {
                        manager.SetCityPolicy(policy);
                    }
                    else
                    {
                        manager.UnsetCityPolicy(policy);
                    }
                }

                // Read the state back; what the game accepted is the truth, not what we asked.
                if (parkScope)
                {
                    after = manager.IsParkPolicySet(policy, (byte)districtId);
                }
                else if (districtId != 0)
                {
                    after = manager.IsDistrictPolicySet(policy, (byte)districtId);
                }
                else
                {
                    after = manager.IsCityPolicySet(policy);
                }
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"dryRun\":").Append(JsonUtil.Bool(dryRun));
            json.Append(",\"policy\":\"").Append(policy.ToString()).Append("\"");
            json.Append(",\"group\":\"").Append(GroupsFor(policy)).Append("\"");
            json.Append(",\"loaded\":").Append(JsonUtil.Bool(loaded));
            json.Append(",\"scope\":\"").Append(parkScope ? "park" : (districtId != 0 ? "district" : "city")).Append("\"");
            json.Append(",\"districtId\":").Append(districtId);
            json.Append(",\"set\":").Append(JsonUtil.Bool(after));
            json.Append(",\"before\":").Append(JsonUtil.Bool(before));
            json.Append(",\"after\":").Append(JsonUtil.Bool(after));
            if (districtName.Length > 0)
            {
                json.Append(",\"districtName\":\"").Append(JsonUtil.Escape(districtName)).Append("\"");
            }
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static void AppendPolicy(StringBuilder json, DistrictManager manager,
            DistrictPolicies.Policies policy, Dictionary<string, string> groupsByName,
            List<int> districtIds, List<string> districtNames, bool cityWide)
        {
            json.Append("{\"name\":\"").Append(policy.ToString()).Append("\"");
            json.Append(",\"value\":").Append((int)policy);
            json.Append(",\"group\":\"").Append(GroupFor(policy, groupsByName)).Append("\"");
            json.Append(",\"loaded\":").Append(JsonUtil.Bool(manager.IsPolicyLoaded(policy)));
            json.Append(",\"cityWide\":").Append(JsonUtil.Bool(cityWide));

            // Per-district state is only reported where it exists; scanning every
            // policy against every district would be silent for parks, which use
            // their own id space (m_parks) and arrive with the districts release.
            StringBuilder districtList = new StringBuilder();
            int districtHitCount = 0;
            for (int i = 0; i < districtIds.Count; i++)
            {
                if (!manager.IsDistrictPolicySet(policy, (byte)districtIds[i]))
                {
                    continue;
                }
                if (districtHitCount > 0)
                {
                    districtList.Append(",");
                }
                districtList.Append("{\"id\":").Append(districtIds[i]);
                districtList.Append(",\"name\":\"").Append(JsonUtil.Escape(districtNames[i])).Append("\"}");
                districtHitCount++;
            }

            json.Append(",\"districts\":[");
            if (districtHitCount > 0)
            {
                json.Append(districtList.ToString());
            }
            json.Append("]}");
        }

        private static void CollectDistricts(DistrictManager manager, List<int> ids, List<string> names)
        {
            District[] buffer = manager.m_districts.m_buffer;
            for (int id = 1; id < buffer.Length; id++)
            {
                District district = buffer[id];
                if (((uint)district.m_flags & (uint)District.Flags.Created) == 0)
                {
                    continue;
                }
                ids.Add(id);
                string name = "";
                try { name = manager.GetDistrictName(id); } catch (Exception) { }
                names.Add(name);
            }
        }

        private static string GroupFor(DistrictPolicies.Policies policy, Dictionary<string, string> groupsByName)
        {
            string group;
            if (groupsByName.TryGetValue(policy.ToString(), out group))
            {
                return group;
            }
            if ((int)policy <= SpecializationMaxValue && (int)policy >= 0)
            {
                return "Specialization";
            }
            if ((int)policy == ReservedValue)
            {
                return "Reserved";
            }
            return "Other";
        }

        /// <summary>Groups a policy belongs to (comma-joined; normally exactly one).</summary>
        private static string GroupsFor(DistrictPolicies.Policies policy)
        {
            string group = GroupFor(policy, BuildGroupMap());
            return group;
        }

        /// <summary>
        /// Maps policy name to the game's own group enums. Each of Services, Taxation,
        /// CityPlanning, Park and Event repeats the policy names that belong to it.
        /// </summary>
        private static Dictionary<string, string> BuildGroupMap()
        {
            Dictionary<string, string> map = new Dictionary<string, string>();
            string[] groupNames = new string[]
            {
                "Services", "Taxation", "CityPlanning", "Park", "Event"
            };

            for (int i = 0; i < groupNames.Length; i++)
            {
                Type groupType = typeof(DistrictPolicies).GetNestedType(groupNames[i]);
                if (groupType == null)
                {
                    continue;
                }
                foreach (object value in Enum.GetValues(groupType))
                {
                    string name = value.ToString();
                    if (name == "None" || name == "All")
                    {
                        continue;
                    }
                    if (!map.ContainsKey(name))
                    {
                        map[name] = groupNames[i];
                    }
                }
            }
            return map;
        }

        private static string DescribeDistricts(List<int> ids, List<string> names)
        {
            StringBuilder list = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    list.Append(", ");
                }
                list.Append(ids[i]);
                if (names[i].Length > 0)
                {
                    list.Append(" (").Append(names[i]).Append(")");
                }
                else
                {
                    list.Append(" (unnamed)");
                }
            }
            if (ids.Count == 0)
            {
                list.Append("(none — only city-wide policies are available until a district is created)");
            }
            return list.ToString();
        }

        private static bool TryParsePolicy(string value, out DistrictPolicies.Policies policy)
        {
            try
            {
                policy = (DistrictPolicies.Policies)Enum.Parse(typeof(DistrictPolicies.Policies), value, true);
                return true;
            }
            catch
            {
                policy = DistrictPolicies.Policies.None;
                return false;
            }
        }
    }
}
