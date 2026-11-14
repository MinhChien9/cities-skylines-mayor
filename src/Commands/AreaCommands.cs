using System.Text;
using ColossalFramework;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// Map tiles: GET /state/areas reads the 5 x 5 tile grid (unlocked, buyable, price,
    /// bounds); POST /commands/unlock-area buys one tile exactly as the game's area tool
    /// does (<c>GameAreaTool.UnlockArea</c>): <c>CalculateTilePrice</c>, then
    /// <c>FetchResource(LandPrice)</c>, then <c>GameAreaManager.UnlockArea</c>, which itself
    /// only checks <c>CanUnlock</c> (milestones allow the tile count, the tile touches an
    /// owned one). Buying land the game lets you buy is normal play, not a cheat.
    /// </summary>
    public static class AreaCommands
    {
        public static CommandResult BuildAreasJson()
        {
            GameAreaManager areas = Singleton<GameAreaManager>.instance;
            int resolution = Resolution(areas);
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"areaCount\":").Append(areas.m_areaCount);
            json.Append(",\"maxAreaCount\":").Append(areas.MaxAreaCount);
            json.Append(",\"gridResolution\":").Append(resolution);
            json.Append(",\"cash\":").Append(Singleton<EconomyManager>.instance.InternalCashAmount / 100);
            json.Append(",\"tiles\":[");
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    if (z > 0 || x > 0)
                    {
                        json.Append(',');
                    }
                    AppendTile(json, areas, x, z);
                }
            }
            json.Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult UnlockArea(string body)
        {
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);
            GameAreaManager areas = Singleton<GameAreaManager>.instance;
            int resolution = Resolution(areas);
            int x;
            int z;
            if (body != null && body.Contains("\"position\""))
            {
                Vector3 p = CommandInput.ReadPoint(body, "position");
                areas.GetTileXZ(p, out x, out z);
            }
            else
            {
                x = (int)JsonUtil.GetNumber(body, "x", -1f);
                z = (int)JsonUtil.GetNumber(body, "z", -1f);
            }
            if (x < 0 || z < 0 || x >= resolution || z >= resolution)
            {
                return CommandResult.Fail("Give the tile as x and z (0.." + (resolution - 1) + ", see /state/areas) or as a world position {x, z} inside it.");
            }
            int index = areas.GetTileIndex(x, z);
            if (areas.IsUnlocked(x, z))
            {
                return CommandResult.Fail("Tile " + x + "," + z + " is already owned.");
            }
            int price = areas.CalculateTilePrice(index);
            string error = null;
            if (!areas.CanUnlock(x, z))
            {
                error = areas.m_areaCount >= areas.MaxAreaCount
                    ? "No more tiles allowed yet: " + areas.m_areaCount + " of " + areas.MaxAreaCount + " owned (milestones raise the limit)."
                    : "Tile " + x + "," + z + " cannot be bought: it must touch a tile the city owns.";
            }
            else if (GameValidation.NeedMoney() && Singleton<EconomyManager>.instance.InternalCashAmount < price)
            {
                error = "Not enough money: the tile costs " + (price / 100) + ".";
            }

            StringBuilder head = new StringBuilder();
            head.Append("\"tile\":");
            AppendTile(head, areas, x, z);
            if (error != null || dryRun)
            {
                return CommandResult.FromJson("{\"ok\":" + JsonUtil.Bool(error == null) + ",\"dryRun\":" + JsonUtil.Bool(dryRun) +
                    ",\"unlocked\":false," + head +
                    (error != null ? ",\"error\":\"" + JsonUtil.Escape(error) + "\"" : "") + "}");
            }

            int moneyBefore = RoadCommands.MoneyNow();
            if (GameValidation.NeedMoney() && price != 0)
            {
                int paid = Singleton<EconomyManager>.instance.FetchResource(EconomyManager.Resource.LandPrice, price,
                    ItemClass.Service.None, ItemClass.SubService.None, ItemClass.Level.None);
                if (paid != price)
                {
                    return CommandResult.Fail("The treasury refused the land price (" + (price / 100) + ").");
                }
            }
            bool unlocked = areas.UnlockArea(index);
            Debug.Log("[SkylinesAgentBridge] Unlock area " + x + "," + z + " -> " + unlocked);
            StringBuilder after = new StringBuilder();
            AppendTile(after, areas, x, z);
            return CommandResult.FromJson("{\"ok\":" + JsonUtil.Bool(unlocked) + ",\"dryRun\":false,\"unlocked\":" + JsonUtil.Bool(unlocked) +
                ",\"tile\":" + after +
                ",\"moneyBefore\":" + moneyBefore + ",\"moneyAfter\":" + RoadCommands.MoneyNow() +
                ",\"areaCount\":" + areas.m_areaCount + "}");
        }

        private static int Resolution(GameAreaManager areas)
        {
            int n = areas.m_areaGrid == null ? 25 : areas.m_areaGrid.Length;
            return Mathf.Max(1, Mathf.RoundToInt(Mathf.Sqrt(n)));
        }

        private static void AppendTile(StringBuilder json, GameAreaManager areas, int x, int z)
        {
            int index = areas.GetTileIndex(x, z);
            bool unlocked = areas.IsUnlocked(x, z);
            float minX;
            float minZ;
            float maxX;
            float maxZ;
            areas.GetAreaBounds(x, z, out minX, out minZ, out maxX, out maxZ);
            json.Append("{\"index\":").Append(index)
                .Append(",\"x\":").Append(x).Append(",\"z\":").Append(z)
                .Append(",\"unlocked\":").Append(JsonUtil.Bool(unlocked))
                .Append(",\"canUnlock\":").Append(JsonUtil.Bool(!unlocked && areas.CanUnlock(x, z)))
                .Append(",\"price\":").Append(unlocked ? 0 : areas.CalculateTilePrice(index) / 100)
                .Append(",\"bounds\":{\"minX\":").Append(JsonUtil.Number(minX))
                .Append(",\"minZ\":").Append(JsonUtil.Number(minZ))
                .Append(",\"maxX\":").Append(JsonUtil.Number(maxX))
                .Append(",\"maxZ\":").Append(JsonUtil.Number(maxZ)).Append("}}");
        }
    }
}
