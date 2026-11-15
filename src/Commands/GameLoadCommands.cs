using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using ColossalFramework.Globalization;
using ColossalFramework.Packaging;
using ColossalFramework.PlatformServices;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Load a save, or start a new game from a map or a scenario, without the cursor.
    ///
    /// Each command repeats what the game's own panel does when the player clicks:
    /// <c>LoadPanel.LoadRoutine</c>, <c>NewGamePanel.StartNewGameRoutine</c> and
    /// <c>NewScenarioGamePanel.StartNewGameRoutine</c>. Each builds a
    /// <see cref="SimulationMetaData"/> and calls <c>LoadingManager.LoadLevel</c>.
    ///
    /// These run on the Unity main thread, like the panels, and also at the main menu, where
    /// no simulation is running yet. <c>LoadLevel</c> only starts the load. The reply comes
    /// back at once with <c>loadCountBefore</c>, and the caller waits for <c>/health</c> to
    /// report a higher <c>loadCount</c> with <c>levelLoaded: true</c>.
    /// </summary>
    internal static class GameLoadCommands
    {
        public static CommandResult ListMapsAndScenarios()
        {
            StringBuilder maps = new StringBuilder();
            int mapCount = 0;
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.MapMetaData))
            {
                MapMetaData meta = Instantiate<MapMetaData>(item);
                if (meta == null || !meta.isPublished)
                {
                    continue;
                }
                if (mapCount++ > 0) maps.Append(',');
                maps.Append("{\"name\":\"").Append(JsonUtil.Escape(meta.mapName)).Append('"')
                    .Append(",\"displayName\":\"").Append(JsonUtil.Escape(Localized("MAPNAME", meta.mapName, meta.mapName))).Append('"')
                    .Append(",\"environment\":\"").Append(JsonUtil.Escape(meta.environment ?? "")).Append('"')
                    .Append(",\"asset\":\"").Append(JsonUtil.Escape(AssetName(item))).Append("\"}");
            }

            StringBuilder scenarios = new StringBuilder();
            int scenarioCount = 0;
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.ScenarioMetaData))
            {
                ScenarioMetaData meta = Instantiate<ScenarioMetaData>(item);
                if (meta == null || !meta.isPublished || meta.assetRef == null)
                {
                    continue;
                }
                string key = ScenarioKey(meta.assetRef);
                if (scenarioCount++ > 0) scenarios.Append(',');
                scenarios.Append("{\"name\":\"").Append(JsonUtil.Escape(meta.scenarioName)).Append('"')
                    .Append(",\"displayName\":\"").Append(JsonUtil.Escape(Localized("SCENARIO_NAME", key, meta.scenarioName))).Append('"')
                    .Append(",\"environment\":\"").Append(JsonUtil.Escape(meta.environment ?? "")).Append('"')
                    .Append(",\"asset\":\"").Append(JsonUtil.Escape(key)).Append("\"}");
            }

            return CommandResult.FromJson("{\"ok\":true,\"mapCount\":" + mapCount + ",\"maps\":[" + maps +
                "],\"scenarioCount\":" + scenarioCount + ",\"scenarios\":[" + scenarios + "]}");
        }

        /// <summary>POST /commands/load {name, dryRun?}: load a save by its name.</summary>
        public static CommandResult Load(string body)
        {
            string name = JsonUtil.GetString(body, "name", "").Trim();
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            if (name.Length == 0)
            {
                return CommandResult.Fail("name is required: the save's name as GET /state/saves lists it.");
            }
            string busy = Busy();
            if (busy != null)
            {
                return CommandResult.Fail(busy);
            }

            // The newest save of that name wins, as in the Load panel's own sort.
            Package.Asset found = null;
            SaveGameMetaData foundMeta = null;
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.SaveGameMetaData))
            {
                if (item == null || !item.isEnabled || !string.Equals(item.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                SaveGameMetaData meta = Instantiate<SaveGameMetaData>(item);
                if (meta == null || meta.assetRef == null)
                {
                    continue;
                }
                if (foundMeta == null || meta.timeStamp > foundMeta.timeStamp)
                {
                    found = item;
                    foundMeta = meta;
                }
            }
            if (foundMeta == null)
            {
                return CommandResult.Fail("No save named '" + name + "'. List them with GET /state/saves (city_saves).");
            }

            string head = "{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"action\":\"load\",\"save\":\"" + JsonUtil.Escape(found.name) + "\"" +
                ",\"cityName\":\"" + JsonUtil.Escape(foundMeta.cityName ?? "") + "\"" +
                ",\"savedAt\":\"" + JsonUtil.Escape(foundMeta.timeStamp.ToString("s")) + "\"" +
                ",\"loadCountBefore\":" + AgentBridge.Instance.LoadCount;
            if (dryRun)
            {
                return CommandResult.FromJson(head + ",\"message\":\"Save found; nothing was loaded.\"}");
            }

            // LoadPanel.LoadRoutine.
            SimulationMetaData ngs = new SimulationMetaData();
            ngs.m_CityName = foundMeta.cityName;
            ngs.m_updateMode = SimulationManager.UpdateMode.LoadGame;
            Package.Asset data = foundMeta.assetRef;
            if (data.package != null && data.package.GetPublishedFileID() != PublishedFileId.invalid)
            {
                ngs.m_disableAchievements = SimulationMetaData.MetaBool.True;
            }
            ApplyMapTheme(ngs, foundMeta.mapThemeRef);
            SavePanel.lastLoadedName = found.name;
            Singleton<LoadingManager>.instance.LoadLevel(data, "Game", "InGame", ngs);

            return CommandResult.FromJson(head + ",\"loading\":true" +
                ",\"hint\":\"Loading. Wait until GET /health reports loadCount above loadCountBefore and levelLoaded true, then check city_summary.\"}");
        }

        /// <summary>
        /// POST /commands/new-game {map | scenario, cityName?, leftHandTraffic?, dryRun?}:
        /// start a new game from a map or a scenario, matched by name or display name.
        /// </summary>
        public static CommandResult NewGame(string body)
        {
            string mapName = JsonUtil.GetString(body, "map", "").Trim();
            string scenarioName = JsonUtil.GetString(body, "scenario", "").Trim();
            string cityName = JsonUtil.GetString(body, "cityName", "").Trim();
            bool leftHand = JsonUtil.GetBool(body, "leftHandTraffic", false);
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            if ((mapName.Length == 0) == (scenarioName.Length == 0))
            {
                return CommandResult.Fail("Give exactly one of map or scenario (names from GET /state/maps).");
            }
            string busy = Busy();
            if (busy != null)
            {
                return CommandResult.Fail(busy);
            }

            SimulationMetaData ngs = new SimulationMetaData();
            ngs.m_gameInstanceIdentifier = Guid.NewGuid().ToString();
            ngs.m_invertTraffic = leftHand ? SimulationMetaData.MetaBool.True : SimulationMetaData.MetaBool.False;
            ngs.m_disableAchievements = SimulationMetaData.MetaBool.False;
            ngs.m_startingDateTime = DateTime.Now;
            ngs.m_currentDateTime = DateTime.Now;
            ngs.m_newGameAppVersion = BuildConfig.APPLICATION_VERSION;

            Package.Asset data;
            string kind;
            string matched;
            if (mapName.Length > 0)
            {
                // NewGamePanel.StartNewGameRoutine.
                MapMetaData meta = FindMap(mapName);
                if (meta == null)
                {
                    return CommandResult.Fail("No map named '" + mapName + "'. List them with GET /state/maps.");
                }
                kind = "map";
                matched = meta.mapName;
                data = meta.assetRef;
                ngs.m_updateMode = SimulationManager.UpdateMode.NewGameFromMap;
                ngs.m_MapName = meta.mapName;
                ngs.m_CityName = cityName.Length > 0 ? cityName : Localized("MAPNAME", meta.mapName, meta.mapName);
                ApplyMapTheme(ngs, meta.mapThemeRef);
            }
            else
            {
                // NewScenarioGamePanel.StartNewGameRoutine.
                ScenarioMetaData meta = FindScenario(scenarioName);
                if (meta == null)
                {
                    return CommandResult.Fail("No scenario named '" + scenarioName + "'. List them with GET /state/maps.");
                }
                kind = "scenario";
                matched = meta.scenarioName;
                data = meta.assetRef;
                ngs.m_updateMode = SimulationManager.UpdateMode.NewGameFromScenario;
                ngs.m_ScenarioAsset = ScenarioKey(data);
                ngs.m_ScenarioName = meta.scenarioName;
                ngs.m_MapName = meta.scenarioName;
                ngs.m_CityName = cityName.Length > 0 ? cityName : Localized("SCENARIO_NAME", ScenarioKey(data), meta.scenarioName);
                ApplyMapTheme(ngs, meta.mapThemeRef);
            }
            if (data == null)
            {
                return CommandResult.Fail("The " + kind + " '" + matched + "' has no data asset.");
            }

            string head = "{\"ok\":true,\"dryRun\":" + JsonUtil.Bool(dryRun) +
                ",\"action\":\"new-game\",\"" + kind + "\":\"" + JsonUtil.Escape(matched) + "\"" +
                ",\"cityName\":\"" + JsonUtil.Escape(ngs.m_CityName ?? "") + "\"" +
                ",\"leftHandTraffic\":" + JsonUtil.Bool(leftHand) +
                ",\"loadCountBefore\":" + AgentBridge.Instance.LoadCount;
            if (dryRun)
            {
                return CommandResult.FromJson(head + ",\"message\":\"Found; nothing was started.\"}");
            }

            Singleton<LoadingManager>.instance.LoadLevel(data, "Game", "InGame", ngs);
            return CommandResult.FromJson(head + ",\"loading\":true" +
                ",\"hint\":\"Loading. Wait until GET /health reports loadCount above loadCountBefore and levelLoaded true. A new game starts paused at the first milestone.\"}");
        }

        private static string Busy()
        {
            LoadingManager loading = Singleton<LoadingManager>.instance;
            if (loading == null)
            {
                return "The loading manager does not exist yet; wait for the main menu.";
            }
            if (loading.m_currentlyLoading)
            {
                return "The game is already loading a level. Wait for GET /health levelLoaded.";
            }
            if (SavePanel.isSaving)
            {
                return "The game is saving. Retry in a few seconds.";
            }
            return null;
        }

        private static MapMetaData FindMap(string wanted)
        {
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.MapMetaData))
            {
                MapMetaData meta = Instantiate<MapMetaData>(item);
                if (meta == null || !meta.isPublished || meta.assetRef == null)
                {
                    continue;
                }
                if (Matches(wanted, meta.mapName, Localized("MAPNAME", meta.mapName, meta.mapName)))
                {
                    return meta;
                }
            }
            return null;
        }

        private static ScenarioMetaData FindScenario(string wanted)
        {
            foreach (Package.Asset item in PackageManager.FilterAssets(UserAssetType.ScenarioMetaData))
            {
                ScenarioMetaData meta = Instantiate<ScenarioMetaData>(item);
                if (meta == null || !meta.isPublished || meta.assetRef == null)
                {
                    continue;
                }
                string key = ScenarioKey(meta.assetRef);
                if (Matches(wanted, meta.scenarioName, Localized("SCENARIO_NAME", key, meta.scenarioName)) ||
                    string.Equals(wanted, key, StringComparison.OrdinalIgnoreCase))
                {
                    return meta;
                }
            }
            return null;
        }

        private static bool Matches(string wanted, string name, string displayName)
        {
            return string.Equals(wanted, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(wanted, displayName, StringComparison.OrdinalIgnoreCase);
        }

        private static void ApplyMapTheme(SimulationMetaData ngs, string mapThemeRef)
        {
            if (mapThemeRef == null)
            {
                return;
            }
            Package.Asset theme = PackageManager.FindAssetByName(mapThemeRef, UserAssetType.MapThemeMetaData);
            if (theme != null)
            {
                ngs.m_MapThemeMetaData = theme.Instantiate<MapThemeMetaData>();
                ngs.m_MapThemeMetaData.SetSelfRef(theme);
            }
        }

        private static string ScenarioKey(Package.Asset asset)
        {
            return (asset.package != null ? asset.package.packageName : "") + "." + asset.name;
        }

        private static string AssetName(Package.Asset item)
        {
            return item == null ? "" : (item.package != null ? item.package.packageName + "." : "") + item.name;
        }

        private static string Localized(string id, string key, string fallback)
        {
            try
            {
                if (key != null && Locale.Exists(id, key))
                {
                    return Locale.Get(id, key);
                }
            }
            catch (Exception)
            {
            }
            return fallback ?? "";
        }

        private static T Instantiate<T>(Package.Asset item) where T : class
        {
            if (item == null || !item.isEnabled)
            {
                return null;
            }
            try
            {
                return item.Instantiate<T>();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
