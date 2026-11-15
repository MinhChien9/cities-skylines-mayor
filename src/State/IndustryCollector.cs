using System;
using System.Collections.Generic;
using System.Text;
using ColossalFramework;
using ColossalFramework.Math;
using UnityEngine;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// GET /state/industry: is the goods chain flowing? Industry turns raw materials into
    /// goods and shops sell them; when a link breaks the game shows <c>NoResources</c> on
    /// factories, <c>NoPlaceforGoods</c> when output piles up, and <c>NoGoods</c> on shops (the
    /// 50k run lost 33 shops that way after most industry became offices).
    ///
    /// Stocks are read the way the info panels read them: <c>BuildingAI.GetMaterialAmount</c>
    /// for inputs (the game's own buffer capacity) and the raw output buffers for factories
    /// (<c>m_customBuffer2</c>, capacity max(production x 500, 16000) as in
    /// <c>IndustrialBuildingAI.ProduceGoods</c>). Runs on the simulation thread.
    /// </summary>
    internal static class IndustryCollector
    {
        private static readonly TransferManager.TransferReason[] IndustrialInputs =
        {
            TransferManager.TransferReason.Logs, TransferManager.TransferReason.Grain,
            TransferManager.TransferReason.Oil, TransferManager.TransferReason.Ore,
            TransferManager.TransferReason.Lumber, TransferManager.TransferReason.Food,
            TransferManager.TransferReason.Petrol, TransferManager.TransferReason.Coal,
        };

        private sealed class Row
        {
            public ushort Id;
            public string Kind;
            public string Prefab;
            public string SubService;
            public int Level;
            public Vector3 Position;
            public int ProductionRate;
            public bool Active;
            public string InputReason;
            public int InputAmount;
            public int InputMax;
            public string OutputReason;
            public int OutputAmount;
            public int OutputMax;
            public string Problems;
            public int Severity;
        }

        public static CommandResult Build(int limit)
        {
            Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
            List<Row> rows = new List<Row>();
            Dictionary<string, int> bySub = new Dictionary<string, int>();
            int industrial = 0, extractors = 0, commercial = 0, activeIndustrial = 0;
            long inStock = 0, inCap = 0, outStock = 0, outCap = 0, goodsStock = 0, goodsCap = 0;
            int noResources = 0, noPlaceForGoods = 0, noWorkers = 0, noEducated = 0, noGoods = 0, shopNoCustomers = 0;

            for (int i = 1; i < buffer.Length; i++)
            {
                Building.Flags flags = buffer[i].m_flags;
                if ((flags & Building.Flags.Created) == Building.Flags.None ||
                    (flags & (Building.Flags.Abandoned | Building.Flags.BurnedDown | Building.Flags.Collapsed)) != Building.Flags.None ||
                    (flags & Building.Flags.Completed) == Building.Flags.None)
                {
                    continue;
                }
                BuildingInfo info = buffer[i].Info;
                if (info == null || info.m_buildingAI == null || info.m_class == null)
                {
                    continue;
                }
                ushort id = (ushort)i;
                Notification.ProblemStruct problems = buffer[i].m_problems;
                Row row = null;

                IndustrialExtractorAI extractor = info.m_buildingAI as IndustrialExtractorAI;
                IndustrialBuildingAI factory = info.m_buildingAI as IndustrialBuildingAI;
                CommercialBuildingAI shop = info.m_buildingAI as CommercialBuildingAI;
                if (extractor != null)
                {
                    extractors++;
                    row = NewRow(id, "extractor", info, ref buffer[i]);
                    TransferManager.TransferReason output = ExtractorOutput(info.m_class.m_subService);
                    int amount;
                    int max;
                    info.m_buildingAI.GetMaterialAmount(id, ref buffer[i], output, out amount, out max);
                    row.OutputReason = output.ToString();
                    row.OutputAmount = amount;
                    row.OutputMax = max;
                    outStock += amount;
                    outCap += max;
                }
                else if (factory != null)
                {
                    industrial++;
                    row = NewRow(id, "factory", info, ref buffer[i]);
                    for (int k = 0; k < IndustrialInputs.Length; k++)
                    {
                        int amount;
                        int max;
                        info.m_buildingAI.GetMaterialAmount(id, ref buffer[i], IndustrialInputs[k], out amount, out max);
                        if (max > 0)
                        {
                            row.InputReason = IndustrialInputs[k].ToString();
                            row.InputAmount = amount;
                            row.InputMax = max;
                            inStock += amount;
                            inCap += max;
                            break;
                        }
                    }
                    int capacity = factory.CalculateProductionCapacity((ItemClass.Level)buffer[i].m_level, new Randomizer(id), buffer[i].Width, buffer[i].Length);
                    row.OutputReason = FactoryOutput(info.m_class.m_subService).ToString();
                    row.OutputAmount = buffer[i].m_customBuffer2;
                    row.OutputMax = Mathf.Max(capacity * 500, 16000);
                    outStock += row.OutputAmount;
                    outCap += row.OutputMax;
                }
                else if (shop != null)
                {
                    commercial++;
                    row = NewRow(id, "shop", info, ref buffer[i]);
                    int amount;
                    int max;
                    info.m_buildingAI.GetMaterialAmount(id, ref buffer[i], shop.m_incomingResource, out amount, out max);
                    row.InputReason = shop.m_incomingResource.ToString();
                    row.InputAmount = amount;
                    row.InputMax = max;
                    goodsStock += amount;
                    goodsCap += max;
                    if (Has(problems, Notification.Problem1.NoGoods)) noGoods++;
                    if (Has(problems, Notification.Problem1.NoCustomers)) shopNoCustomers++;
                }
                else
                {
                    continue;
                }

                if (row.Kind != "shop")
                {
                    Count(bySub, row.SubService);
                    if (row.Active) activeIndustrial++;
                    if (Has(problems, Notification.Problem1.NoResources)) noResources++;
                    if (Has(problems, Notification.Problem1.NoPlaceforGoods)) noPlaceForGoods++;
                    if (Has(problems, Notification.Problem1.NoWorkers)) noWorkers++;
                    if (Has(problems, Notification.Problem1.NoEducatedWorkers)) noEducated++;
                }
                row.Problems = problems.IsNone ? "" : problems.ToString();
                row.Severity = Severity(row, problems);
                if (row.Severity > 0)
                {
                    rows.Add(row);
                }
            }

            rows.Sort(delegate(Row a, Row b) { return b.Severity.CompareTo(a.Severity); });

            StringBuilder json = new StringBuilder("{\"ok\":true");
            json.Append(",\"industry\":{\"factories\":").Append(industrial)
                .Append(",\"extractors\":").Append(extractors)
                .Append(",\"active\":").Append(activeIndustrial)
                .Append(",\"bySubService\":{");
            bool first = true;
            foreach (KeyValuePair<string, int> kv in bySub)
            {
                if (!first) json.Append(',');
                first = false;
                json.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
            }
            json.Append("},\"inputStock\":").Append(inStock).Append(",\"inputCapacity\":").Append(inCap)
                .Append(",\"inputFillPercent\":").Append(Percent(inStock, inCap))
                .Append(",\"outputStock\":").Append(outStock).Append(",\"outputCapacity\":").Append(outCap)
                .Append(",\"outputFillPercent\":").Append(Percent(outStock, outCap))
                .Append(",\"problems\":{\"NoResources\":").Append(noResources)
                .Append(",\"NoPlaceforGoods\":").Append(noPlaceForGoods)
                .Append(",\"NoWorkers\":").Append(noWorkers)
                .Append(",\"NoEducatedWorkers\":").Append(noEducated).Append("}}");
            json.Append(",\"commercial\":{\"shops\":").Append(commercial)
                .Append(",\"goodsStock\":").Append(goodsStock).Append(",\"goodsCapacity\":").Append(goodsCap)
                .Append(",\"goodsFillPercent\":").Append(Percent(goodsStock, goodsCap))
                .Append(",\"problems\":{\"NoGoods\":").Append(noGoods)
                .Append(",\"NoCustomers\":").Append(shopNoCustomers).Append("}}");
            int producers = industrial + extractors;
            json.Append(",\"industryPerShop\":").Append(JsonUtil.Number(commercial == 0 ? 0f : (float)producers / commercial));
            json.Append(",\"diagnosis\":[").Append(Diagnosis(producers, commercial, noGoods, noResources, noPlaceForGoods, noWorkers + noEducated)).Append(']');

            json.Append(",\"buildingsWithTrouble\":").Append(rows.Count).Append(",\"buildings\":[");
            for (int r = 0; r < rows.Count && r < limit; r++)
            {
                Row row = rows[r];
                if (r > 0) json.Append(',');
                json.Append("{\"id\":").Append(row.Id)
                    .Append(",\"kind\":\"").Append(row.Kind).Append('"')
                    .Append(",\"prefab\":\"").Append(JsonUtil.Escape(row.Prefab)).Append('"')
                    .Append(",\"subService\":\"").Append(row.SubService).Append('"')
                    .Append(",\"level\":").Append(row.Level)
                    .Append(",\"x\":").Append(Mathf.RoundToInt(row.Position.x))
                    .Append(",\"z\":").Append(Mathf.RoundToInt(row.Position.z))
                    .Append(",\"active\":").Append(JsonUtil.Bool(row.Active))
                    .Append(",\"productionRate\":").Append(row.ProductionRate);
                if (row.InputReason != null)
                {
                    json.Append(",\"input\":{\"material\":\"").Append(row.InputReason).Append("\",\"amount\":").Append(row.InputAmount)
                        .Append(",\"max\":").Append(row.InputMax).Append(",\"percent\":").Append(Percent(row.InputAmount, row.InputMax)).Append('}');
                }
                if (row.OutputReason != null)
                {
                    json.Append(",\"output\":{\"material\":\"").Append(row.OutputReason).Append("\",\"amount\":").Append(row.OutputAmount)
                        .Append(",\"max\":").Append(row.OutputMax).Append(",\"percent\":").Append(Percent(row.OutputAmount, row.OutputMax)).Append('}');
                }
                json.Append(",\"problems\":\"").Append(JsonUtil.Escape(row.Problems)).Append("\"}");
            }
            json.Append("],\"notes\":\"Only buildings in trouble are listed, worst first: a factory or shop with a supply problem or an input under 10 %, a factory whose output is over 90 % full. Stocks are in the game's units; one truckload is 8000.\"}");
            return CommandResult.FromJson(json.ToString());
        }

        private static Row NewRow(ushort id, string kind, BuildingInfo info, ref Building b)
        {
            Row row = new Row();
            row.Id = id;
            row.Kind = kind;
            row.Prefab = info.name;
            row.SubService = info.m_class.m_subService.ToString();
            row.Level = (int)b.m_level + 1;
            row.Position = b.m_position;
            row.ProductionRate = b.m_productionRate;
            row.Active = (b.m_flags & Building.Flags.Active) != Building.Flags.None;
            return row;
        }

        private static int Severity(Row row, Notification.ProblemStruct problems)
        {
            int s = 0;
            if (Has(problems, Notification.Problem1.NoGoods) || Has(problems, Notification.Problem1.NoResources)) s += 100;
            if (Has(problems, Notification.Problem1.NoPlaceforGoods)) s += 80;
            if (row.InputMax > 0 && row.InputAmount * 10 < row.InputMax) s += 50 - Percent(row.InputAmount, row.InputMax) * 5;
            if (row.Kind != "shop" && row.OutputMax > 0 && row.OutputAmount * 10 > row.OutputMax * 9) s += 40;
            return s;
        }

        private static string Diagnosis(int producers, int shops, int noGoods, int noResources, int noPlace, int noWorkers)
        {
            List<string> lines = new List<string>();
            if (shops > 0 && noGoods * 20 > shops)
            {
                lines.Add(noGoods + " of " + shops + " shops have no goods: industry makes too little or cannot deliver. Industry per shop is " +
                    ((float)producers / shops).ToString("0.00") + "; zone more industry near the shops, or fix the road access of the industry there is.");
            }
            if (noResources > 0)
            {
                lines.Add(noResources + " factories lack raw input (NoResources): specialised extractors or cargo imports cannot keep up; check road/cargo connections to the outside.");
            }
            if (noPlace > 0)
            {
                lines.Add(noPlace + " buildings cannot ship their output (NoPlaceforGoods): no buyer reachable or trucks stuck in traffic; add commercial demand or a cargo station.");
            }
            if (noWorkers > 0)
            {
                lines.Add(noWorkers + " producers are short of (educated) workers, which cuts output.");
            }
            if (lines.Count == 0)
            {
                lines.Add("The goods chain is flowing: no shop without goods, no starved or blocked factory.");
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(JsonUtil.Escape(lines[i])).Append('"');
            }
            return sb.ToString();
        }

        private static TransferManager.TransferReason ExtractorOutput(ItemClass.SubService sub)
        {
            switch (sub)
            {
                case ItemClass.SubService.IndustrialForestry: return TransferManager.TransferReason.Logs;
                case ItemClass.SubService.IndustrialFarming: return TransferManager.TransferReason.Grain;
                case ItemClass.SubService.IndustrialOil: return TransferManager.TransferReason.Oil;
                case ItemClass.SubService.IndustrialOre: return TransferManager.TransferReason.Ore;
                default: return TransferManager.TransferReason.None;
            }
        }

        private static TransferManager.TransferReason FactoryOutput(ItemClass.SubService sub)
        {
            switch (sub)
            {
                case ItemClass.SubService.IndustrialForestry: return TransferManager.TransferReason.Lumber;
                case ItemClass.SubService.IndustrialFarming: return TransferManager.TransferReason.Food;
                case ItemClass.SubService.IndustrialOil: return TransferManager.TransferReason.Petrol;
                case ItemClass.SubService.IndustrialOre: return TransferManager.TransferReason.Coal;
                default: return TransferManager.TransferReason.Goods;
            }
        }

        private static bool Has(Notification.ProblemStruct problems, Notification.Problem1 flag)
        {
            return (problems.m_Problems1 & flag) != Notification.Problem1.None;
        }

        private static void Count(Dictionary<string, int> d, string key)
        {
            int n;
            d.TryGetValue(key, out n);
            d[key] = n + 1;
        }

        private static int Percent(long amount, long max)
        {
            return max <= 0 ? 0 : (int)Math.Min(100L, amount * 100L / max);
        }
    }
}
