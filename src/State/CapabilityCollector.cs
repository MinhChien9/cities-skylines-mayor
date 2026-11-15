using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ICities;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// Builds <c>GET /state/capabilities</c>: game version, expansions, milestones,
    /// unlocked features and the simulation clock, read through the ICities surface
    /// (<see cref="ICities.IManagers"/>) plus UnlockManager.
    ///
    /// Each section is guarded on its own so one broken surface does not starve the
    /// others — the same policy the thread probe follows. Runs on the simulation thread.
    /// </summary>
    internal static class CapabilityCollector
    {
        public static CommandResult Build()
        {
            IManagers managers = AgentBridge.Instance.EnsureICitiesManagers();
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"iManagersCaptured\":").Append(JsonUtil.Bool(managers != null));

            AppendVersion(json, managers);
            AppendExpansions(json, managers);
            AppendLoadedContent(json, managers);
            AppendMilestones(json, managers);
            AppendFeatures(json);
            AppendClock(json, managers);
            AppendMods(json);
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static void AppendVersion(StringBuilder json, IManagers managers)
        {
            if (managers == null)
            {
                return;
            }

            try
            {
                json.Append(",\"version\":\"").Append(JsonUtil.Escape(managers.application.currentVersion)).Append("\"");
            }
            catch (Exception)
            {
            }
        }

        private static void AppendExpansions(StringBuilder json, IManagers managers)
        {
            if (managers == null)
            {
                return;
            }

            try
            {
                StringBuilder known = new StringBuilder();
                StringBuilder supported = new StringBuilder();
                bool firstKnown = true;
                bool firstSupported = true;
                foreach (Expansion value in Enum.GetValues(typeof(Expansion)))
                {
                    if (value == Expansion.None)
                    {
                        continue;
                    }

                    if (!firstKnown)
                    {
                        known.Append(",");
                    }
                    known.Append("\"").Append(value).Append("\"");
                    firstKnown = false;

                    if (managers.application.SupportsExpansion(value))
                    {
                        if (!firstSupported)
                        {
                            supported.Append(",");
                        }
                        supported.Append("\"").Append(value).Append("\"");
                        firstSupported = false;
                    }
                }

                json.Append(",\"knownExpansions\":[").Append(known).Append("]");
                json.Append(",\"supportedExpansions\":[").Append(supported).Append("]");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// What expansion content is really loaded: <c>SupportsExpansion</c> said yes to all 14
        /// expansions on an Epic game started without the Epic login, while not one of their
        /// buildings was loaded (#71, #84). The loaded building prefabs are the truth, so each
        /// expansion is judged by the services, sub-services or building AIs only it adds.
        /// </summary>
        private static readonly string[][] ExpansionMarkers =
        {
            // expansion, display name, markers (service, sub-service or "ai:<BuildingAI type>")
            new[] { "AfterDark", "After Dark", "CommercialLeisure", "CommercialTourist", "PublicTransportTaxi" },
            new[] { "Snowfall", "Snowfall", "PublicTransportTram" },
            new[] { "NaturalDisasters", "Natural Disasters", "Disaster" },
            new[] { "InMotion", "Mass Transit", "PublicTransportMonorail", "PublicTransportCableCar" },
            new[] { "GreenCities", "Green Cities", "CommercialEco", "ResidentialLowEco", "ResidentialHighEco", "OfficeHightech" },
            new[] { "Parks", "Parklife", "ai:ParkGateAI" }, // the base game has 5 BeautificationParks pillars
            new[] { "Industry", "Industries", "PlayerIndustry", "PublicTransportPost" },
            new[] { "Campuses", "Campus", "PlayerEducation", "VarsitySports" },
            new[] { "Urban", "Sunset Harbor", "Fishing", "PublicTransportTrolleybus" },
            new[] { "Airport", "Airports", "ai:AirportGateAI", "ai:AirportCargoGateAI", "ai:AirportAuxBuildingAI" },
            new[] { "PlazasAndPromenades", "Plazas & Promenades", "ServicePoint" },
            new[] { "FinanceExpansion", "Financial Districts", "OfficeFinancial", "PoliceDepartmentBank", "ai:BankOfficeAI" },
            new[] { "Hotels", "Hotels & Retreats", "Hotel" },
            new[] { "RacesAndParades", "Match Day / Races & Parades", "Race" },
        };

        private static void AppendLoadedContent(StringBuilder json, IManagers managers)
        {
            try
            {
                Dictionary<string, int> counts = new Dictionary<string, int>();
                int total = 0;
                uint loaded = (uint)PrefabCollection<BuildingInfo>.LoadedCount();
                for (uint i = 0; i < loaded; i++)
                {
                    BuildingInfo info = PrefabCollection<BuildingInfo>.GetLoaded(i);
                    if (info == null || info.m_class == null)
                    {
                        continue;
                    }
                    total++;
                    string key = info.m_class.m_subService != ItemClass.SubService.None
                        ? info.m_class.m_subService.ToString()
                        : info.m_class.m_service.ToString();
                    Increment(counts, key);
                    if (info.m_class.m_subService != ItemClass.SubService.None)
                    {
                        Increment(counts, info.m_class.m_service.ToString());
                    }
                    if (info.m_buildingAI != null)
                    {
                        Increment(counts, "ai:" + info.m_buildingAI.GetType().Name);
                    }
                }

                StringBuilder expansions = new StringBuilder();
                StringBuilder missing = new StringBuilder();
                for (int e = 0; e < ExpansionMarkers.Length; e++)
                {
                    string[] row = ExpansionMarkers[e];
                    bool supported = false;
                    try
                    {
                        Expansion value = (Expansion)Enum.Parse(typeof(Expansion), row[0]);
                        supported = managers != null && managers.application.SupportsExpansion(value);
                    }
                    catch (Exception)
                    {
                    }
                    StringBuilder evidence = new StringBuilder();
                    int found = 0;
                    for (int m = 2; m < row.Length; m++)
                    {
                        int n;
                        counts.TryGetValue(row[m], out n);
                        found += n;
                        if (m > 2) evidence.Append(',');
                        evidence.Append('"').Append(JsonUtil.Escape(row[m])).Append("\":").Append(n);
                    }
                    if (e > 0) expansions.Append(',');
                    expansions.Append("{\"expansion\":\"").Append(row[0]).Append('"')
                        .Append(",\"name\":\"").Append(JsonUtil.Escape(row[1])).Append('"')
                        .Append(",\"supported\":").Append(JsonUtil.Bool(supported))
                        .Append(",\"loaded\":").Append(JsonUtil.Bool(found > 0))
                        .Append(",\"evidence\":{").Append(evidence).Append("}}");
                    if (found == 0)
                    {
                        if (missing.Length > 0) missing.Append(',');
                        missing.Append('"').Append(JsonUtil.Escape(row[1] + (supported ? " (supported, but none of its buildings are loaded)" : ""))).Append('"');
                    }
                }

                List<string> keys = new List<string>(counts.Keys);
                keys.Sort(StringComparer.Ordinal);
                StringBuilder bySub = new StringBuilder();
                bool first = true;
                for (int k = 0; k < keys.Count; k++)
                {
                    if (keys[k].StartsWith("ai:", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!first) bySub.Append(',');
                    first = false;
                    bySub.Append('"').Append(JsonUtil.Escape(keys[k])).Append("\":").Append(counts[keys[k]]);
                }

                json.Append(",\"loadedContent\":{\"buildingPrefabs\":").Append(total)
                    .Append(",\"byServiceAndSubService\":{").Append(bySub).Append('}')
                    .Append(",\"expansions\":[").Append(expansions).Append(']')
                    .Append(",\"missingContent\":[").Append(missing).Append(']')
                    .Append(",\"notes\":\"loaded is judged from the loaded building prefabs, not from ownership: on Epic the game loads expansion content only when started with the Epic login (scripts/start-game.ps1). Check a scenario's goals against this before playing it.\"}");
            }
            catch (Exception)
            {
            }
        }

        private static void Increment(Dictionary<string, int> counts, string key)
        {
            int n;
            counts.TryGetValue(key, out n);
            counts[key] = n + 1;
        }

        private static void AppendMilestones(StringBuilder json, IManagers managers)
        {
            if (managers == null)
            {
                return;
            }

            try
            {
                string[] milestones = managers.milestones.EnumerateMilestones();
                json.Append(",\"milestones\":[");
                for (int i = 0; i < milestones.Length; i++)
                {
                    if (i > 0)
                    {
                        json.Append(",");
                    }
                    json.Append("\"").Append(JsonUtil.Escape(milestones[i])).Append("\"");
                }
                json.Append("]");
            }
            catch (Exception)
            {
            }

            // The milestone the city is currently working towards. Its internal name is
            // the same string UnlockMilestone expects.
            try
            {
                MilestoneInfo current = UnlockManager.instance.GetCurrentMilestone();
                if (current != null && current.m_name != null)
                {
                    json.Append(",\"currentMilestone\":\"").Append(JsonUtil.Escape(current.m_name)).Append("\"");
                }
            }
            catch (Exception)
            {
            }
        }

        private static void AppendFeatures(StringBuilder json)
        {
            // UnlockManager.Feature is the gate list for milestone-gated gameplay:
            // GameAreas, ParkAreas, CampusAreas, Ferry, Trolleybus, PedestrianZones, ...
            try
            {
                UnlockManager unlock = UnlockManager.instance;
                List<string> unlocked = new List<string>();
                List<string> locked = new List<string>();
                foreach (UnlockManager.Feature value in Enum.GetValues(typeof(UnlockManager.Feature)))
                {
                    if (value == UnlockManager.Feature.None)
                    {
                        continue;
                    }
                    if (unlock.Unlocked(value))
                    {
                        unlocked.Add(value.ToString());
                    }
                    else
                    {
                        locked.Add(value.ToString());
                    }
                }

                List<string> zones = new List<string>();
                foreach (ItemClass.Zone zone in Enum.GetValues(typeof(ItemClass.Zone)))
                {
                    if (zone != ItemClass.Zone.None && zone != ItemClass.Zone.Unzoned && zone != ItemClass.Zone.Distant &&
                        SkylinesAgentBridge.Support.UnlockCheck.IsZoneUnlocked(zone))
                    {
                        zones.Add(zone.ToString());
                    }
                }
                json.Append(",\"unlockedZones\":[");
                for (int i = 0; i < zones.Count; i++)
                {
                    json.Append(i > 0 ? ",\"" : "\"").Append(zones[i]).Append("\"");
                }
                json.Append("]");

                json.Append(",\"unlockedFeatures\":[");
                for (int i = 0; i < unlocked.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(",");
                    }
                    json.Append("\"").Append(unlocked[i]).Append("\"");
                }
                json.Append("],\"lockedFeatures\":[");
                for (int i = 0; i < locked.Count; i++)
                {
                    if (i > 0)
                    {
                        json.Append(",");
                    }
                    json.Append("\"").Append(locked[i]).Append("\"");
                }
                json.Append("]");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>
        /// The enabled mods, and which of them are the game's own cheat mods. Unlimited Money
        /// makes the game (and so the bridge) accept any build whatever the cash, and Unlock
        /// All opens every milestone: an agent reading the economy needs to know.
        /// </summary>
        private static void AppendMods(StringBuilder json)
        {
            try
            {
                List<string> enabled = new List<string>();
                List<string> cheats = new List<string>();
                foreach (ColossalFramework.Plugins.PluginManager.PluginInfo plugin in ColossalFramework.Plugins.PluginManager.instance.GetPluginsInfo())
                {
                    if (plugin == null || !plugin.isEnabled)
                    {
                        continue;
                    }
                    string name = plugin.name;
                    try
                    {
                        IUserMod[] mods = plugin.GetInstances<IUserMod>();
                        if (mods != null && mods.Length > 0 && !string.IsNullOrEmpty(mods[0].Name))
                        {
                            name = mods[0].Name;
                        }
                    }
                    catch (Exception)
                    {
                    }
                    enabled.Add(name);
                    string folder = plugin.name ?? "";
                    if (plugin.isBuiltin && (folder == "UnlimitedMoney" || folder == "UnlockAll" || folder == "UnlimitedOilAndOre" || folder == "UnlimitedSoil"))
                    {
                        cheats.Add(folder);
                    }
                }
                json.Append(",\"enabledMods\":[");
                for (int i = 0; i < enabled.Count; i++)
                {
                    if (i > 0) json.Append(",");
                    json.Append("\"").Append(JsonUtil.Escape(enabled[i])).Append("\"");
                }
                json.Append("],\"cheatMods\":[");
                for (int i = 0; i < cheats.Count; i++)
                {
                    if (i > 0) json.Append(",");
                    json.Append("\"").Append(cheats[i]).Append("\"");
                }
                json.Append("]");
            }
            catch (Exception)
            {
            }
        }

        private static void AppendClock(StringBuilder json, IManagers managers)
        {
            // simulationTime / frame / tick are first-class ICities reads; diffing two
            // reads measures how much simulated time actually elapsed.
            if (managers == null)
            {
                return;
            }

            try
            {
                IThreading threading = managers.threading;
                json.Append(",\"clock\":{\"simulationTime\":\"")
                    .Append(JsonUtil.Escape(threading.simulationTime.ToString("s", CultureInfo.InvariantCulture)))
                    .Append("\"");
                json.Append(",\"simulationFrame\":").Append(threading.simulationFrame);
                json.Append(",\"simulationTick\":").Append(threading.simulationTick);
                json.Append(",\"simulationPaused\":").Append(JsonUtil.Bool(threading.simulationPaused));
                json.Append(",\"simulationSpeed\":").Append(threading.simulationSpeed);
                json.Append(",\"simulationDayTimeHour\":").Append(JsonUtil.Number(threading.simulationDayTimeHour));
                json.Append(",\"simulationNightTime\":").Append(JsonUtil.Bool(threading.simulationNightTime));
                json.Append(",\"renderDayTimeHour\":").Append(JsonUtil.Number(threading.renderDayTimeHour)).Append("}");
            }
            catch (Exception)
            {
            }
        }
    }
}
