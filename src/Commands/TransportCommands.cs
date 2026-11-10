using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ColossalFramework;
using ColossalFramework.Math;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Commands
{
    /// <summary>
    /// GET /state/transport, GET /prefabs/transport and POST /commands/transport-line.
    ///
    /// The transport line model is public game API (no ICities interface exists for it):
    /// TransportManager.CreateLine(out UInt16, ref Randomizer, TransportInfo, Boolean newNumber),
    /// TransportManager.GetLineName / SetLineName / GetVehicleIndex / AssignSelectedLineVehicle /
    /// TryGetSelectedLineVehicle, and the TransportLine struct's AddStop / CanAddStop /
    /// RemoveStop / CountStops / GetStop / CountVehicles / GetVehicle / GetActive / SetActive.
    ///
    /// Every call used here was verified against the live game's Assembly-CSharp.dll by
    /// decompilation (2026-11-11). One upstream-doc correction came out of that check:
    /// TransportLine.ReplaceVehicles is internal and index-based — the public route to
    /// change a line's vehicle is AssignSelectedLineVehicle(lineID, PrefabCollection index),
    /// and the index space is the one GetVehicleIndex(name) answers.
    ///
    /// TransportManager.SetLineName returns an IEnumerator&lt;bool&gt; that completes its work
    /// on the first MoveNext (the game runs it as a one-shot coroutine), so the bridge
    /// enumerates it synchronously on the simulation thread and verifies with GetLineName.
    /// </summary>
    public static class TransportCommands
    {
        /// <summary>Per-line list caps that keep the JSON bounded.</summary>
        private const int MaxStopReports = 64;
        private const int MaxVehicleReports = 32;
        private const int MaxVehicleCandidates = 12;

        // ------------------------------------------------------------------ reads

        public static CommandResult BuildTransportJson(int limit)
        {
            if (limit < 1)
            {
                limit = 1;
            }

            TransportManager manager = Singleton<TransportManager>.instance;
            TransportLine[] buffer = manager.m_lines.m_buffer;

            int total = 0;
            int emitted = 0;
            StringBuilder items = new StringBuilder();

            for (int id = 1; id < buffer.Length; id++)
            {
                TransportLine.Flags flags = buffer[id].m_flags;
                if (flags == TransportLine.Flags.None ||
                    (flags & TransportLine.Flags.Created) == 0 ||
                    (flags & TransportLine.Flags.Deleted) != 0)
                {
                    continue;
                }

                total++;
                if (emitted >= limit)
                {
                    continue;
                }

                if (emitted > 0)
                {
                    items.Append(",");
                }
                AppendLine(items, manager, (ushort)id, buffer[id], MaxStopReports, MaxVehicleReports);
                emitted++;
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"lineCount\":").Append(total);
            json.Append(",\"returned\":").Append(emitted);
            json.Append(",\"limit\":").Append(limit);
            json.Append(",\"lines\":[").Append(items.ToString()).Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        public static CommandResult BuildTransportPrefabsJson(string typeFilter)
        {
            // The game's own catalogue of TransportInfo assets, through the same generic
            // PrefabCollection the road catalogue uses. This answers at the source the R2
            // open question about PrefabCollection<TransportInfo> enumerating the assets;
            // the live run confirms it empirically.
            int prefabCount = PrefabCollection<TransportInfo>.PrefabCount();
            List<TransportInfo> infos = new List<TransportInfo>();
            for (int i = 0; i < prefabCount; i++)
            {
                TransportInfo info = PrefabCollection<TransportInfo>.GetPrefab((uint)i);
                if (info != null)
                {
                    infos.Add(info);
                }
            }

            // Vehicle assets the lines can deploy, matched the way the game matches them
            // in TransportLine.CheckVehicleClass: same service + subService. The per-depot
            // level refinement only narrows further, so `level` is reported per vehicle.
            int vehiclePrefabCount = PrefabCollection<VehicleInfo>.LoadedCount();
            List<VehicleInfo> vehicleInfos = new List<VehicleInfo>();
            for (int i = 0; i < vehiclePrefabCount; i++)
            {
                VehicleInfo info = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                if (info != null)
                {
                    vehicleInfos.Add(info);
                }
            }

            int emitted = 0;
            StringBuilder items = new StringBuilder();
            for (int i = 0; i < infos.Count; i++)
            {
                string type = infos[i].m_transportType.ToString();
                if (typeFilter != null && typeFilter.Length > 0 &&
                    string.Compare(type, typeFilter, StringComparison.OrdinalIgnoreCase) != 0)
                {
                    continue;
                }

                if (emitted > 0)
                {
                    items.Append(",");
                }
                AppendPrefab(items, infos[i], vehicleInfos);
                emitted++;
            }

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true");
            json.Append(",\"transportInfoTotal\":").Append(infos.Count);
            json.Append(",\"vehiclePrefabTotal\":").Append(vehicleInfos.Count);
            json.Append(",\"returned\":").Append(emitted);
            json.Append(",\"prefabs\":[").Append(items.ToString()).Append("]}");
            return CommandResult.FromJson(json.ToString());
        }

        // --------------------------------------------------------------- commands

        public static CommandResult TransportLineCommand(string body)
        {
            string action = JsonUtil.GetString(body, "action", "state").Trim();
            ushort line = (ushort)JsonUtil.GetNumber(body, "line", 0f);
            bool dryRun = JsonUtil.GetBool(body, "dryRun", false);

            if (action == "create")
            {
                return CreateLine(body, dryRun);
            }

            TransportManager manager = Singleton<TransportManager>.instance;
            if (line == 0 || !IsCreatedLine(manager, line))
            {
                return CommandResult.Fail("line must be an existing transport line id > 0. " +
                    "Read GET /state/transport for the line list, or create one with action \"create\".");
            }

            if (action == "add-stop")
            {
                return AddStop(manager, line, body, dryRun);
            }
            if (action == "remove-stop")
            {
                return RemoveStop(manager, line, body, dryRun);
            }
            if (action == "activate")
            {
                return Activate(manager, line, body, dryRun);
            }
            if (action == "rename")
            {
                return Rename(manager, line, body, dryRun);
            }
            if (action == "set-vehicle")
            {
                return SetVehicle(manager, line, body, dryRun);
            }

            return CommandResult.Fail("Unknown action: " + action +
                ". Supported: create, add-stop, remove-stop, activate, rename, set-vehicle.");
        }

        private static CommandResult CreateLine(string body, bool dryRun)
        {
            string prefabName = JsonUtil.GetString(body, "transportInfo", "").Trim();
            bool newNumber = JsonUtil.GetBool(body, "newNumber", true);

            if (prefabName.Length == 0)
            {
                return CommandResult.Fail("transportInfo is required, e.g. \"Bus\". " +
                    "Read GET /prefabs/transport for the exact names.");
            }

            TransportInfo info = PrefabCollection<TransportInfo>.FindLoaded(prefabName);
            if (info == null)
            {
                return CommandResult.Fail("Transport prefab was not found: " + prefabName +
                    ". Read GET /prefabs/transport for the exact names.");
            }

            TransportManager manager = Singleton<TransportManager>.instance;
            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"create\"");
                dry.Append(",\"message\":\"Create-line validation passed; no line was created.\"");
                AppendPrefab(dry, info, null);
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            SimulationManager simulation = Singleton<SimulationManager>.instance;
            Randomizer randomizer = simulation.m_randomizer;
            ushort lineId;
            bool created = manager.CreateLine(out lineId, ref randomizer, info, newNumber);
            simulation.m_randomizer = randomizer;

            if (!created)
            {
                return CommandResult.Fail("CreateLine was refused by the game (the usual cause is the ~251 line limit).");
            }

            Debug.Log("[SkylinesAgentBridge] Created transport line " + lineId + " with prefab " + prefabName);

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"create\",\"line\":").Append(lineId);
            AppendPrefab(json, info, null);
            json.Append(",\"lineData\":");
            AppendLine(json, manager, lineId, manager.m_lines.m_buffer[lineId], 0, 0);
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult AddStop(TransportManager manager, ushort line, string body, bool dryRun)
        {
            int index = (int)JsonUtil.GetNumber(body, "index", -1f);
            Vector3 position = CommandInput.ReadPoint(body, "position");
            bool fixedPlatform = JsonUtil.GetBool(body, "fixedPlatform", false);

            if (position.y == 0f)
            {
                // The caller may set an explicit y (e.g. the height of a bridge deck);
                // otherwise raw terrain height is the best default for a stop node.
                position.y = Singleton<TerrainManager>.instance.SampleRawHeightSmoothWithWater(position, false, 0f);
            }

            TransportLine data = manager.m_lines.m_buffer[line];
            int stopCountBefore = data.CountStops(line);

            if (dryRun)
            {
                bool canAdd = data.CanAddStop(line, index, position);
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"add-stop\",\"line\":").Append(line);
                dry.Append(",\"canAddStop\":").Append(JsonUtil.Bool(canAdd));
                dry.Append(",\"stopCountBefore\":").Append(stopCountBefore);
                dry.Append(",\"message\":").Append(canAdd
                    ? "\"CanAddStop accepted the position.\""
                    : "\"CanAddStop refused the position; the game does not say why.\"");
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            // The game flags the returned bool but never says why when it refuses.
            bool added = data.AddStop(line, index, position, fixedPlatform);

            if (!added)
            {
                return CommandResult.Fail("AddStop was refused by the game for line " + line + " at index " + index +
                    ". Usual causes: the position is not reachable along the line path, or the platform could not be built." +
                    " stopCountBefore: " + stopCountBefore + ".");
            }

            Debug.Log("[SkylinesAgentBridge] Added stop to transport line " + line + " at index " + index);
            return LineStateResult(manager, line, "add-stop", stopCountBefore);
        }

        private static CommandResult RemoveStop(TransportManager manager, ushort line, string body, bool dryRun)
        {
            int index = (int)JsonUtil.GetNumber(body, "index", -2f);
            if (index < -1)
            {
                return CommandResult.Fail("index is required (-1 removes the last stop; 0..CountStops-1 removes a specific one).");
            }

            TransportLine data = manager.m_lines.m_buffer[line];
            int stopCountBefore = data.CountStops(line);
            if (stopCountBefore == 0)
            {
                return CommandResult.Fail("Line " + line + " has no stops to remove.");
            }
            if (index >= stopCountBefore)
            {
                return CommandResult.Fail("index " + index + " is out of range; the line has " +
                    stopCountBefore + " stops (0.." + (stopCountBefore - 1) + "; -1 = last).");
            }

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"remove-stop\",\"line\":").Append(line);
                dry.Append(",\"index\":").Append(index);
                dry.Append(",\"stopCountBefore\":").Append(stopCountBefore);
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            Vector3 removedPosition;
            bool removed = data.RemoveStop(line, index, out removedPosition);

            if (!removed)
            {
                return CommandResult.Fail("RemoveStop was refused by the game for line " + line + " at index " + index + ".");
            }

            Debug.Log("[SkylinesAgentBridge] Removed stop " + index + " from transport line " + line);
            return LineStateResult(manager, line, "remove-stop", stopCountBefore);
        }

        private static CommandResult Activate(TransportManager manager, ushort line, string body, bool dryRun)
        {
            // SetActive is the game's own day/night service switch (it clears or sets the
            // DisabledDay / DisabledNight line flags).
            bool active = JsonUtil.GetBool(body, "active", true);
            bool day = active && JsonUtil.GetBool(body, "day", true);
            bool night = active && JsonUtil.GetBool(body, "night", true);

            bool dayBefore;
            bool nightBefore;
            manager.m_lines.m_buffer[line].GetActive(out dayBefore, out nightBefore);

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"activate\",\"line\":").Append(line);
                dry.Append(",\"day\":{\"before\":").Append(JsonUtil.Bool(dayBefore));
                dry.Append(",\"after\":").Append(JsonUtil.Bool(day)).Append("}");
                dry.Append(",\"night\":{\"before\":").Append(JsonUtil.Bool(nightBefore));
                dry.Append(",\"after\":").Append(JsonUtil.Bool(night)).Append("}");
                dry.Append("}");
                return CommandResult.FromJson(dry.ToString());
            }

            manager.m_lines.m_buffer[line].SetActive(day, night);

            bool dayAfter;
            bool nightAfter;
            manager.m_lines.m_buffer[line].GetActive(out dayAfter, out nightAfter);

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"activate\",\"line\":").Append(line);
            json.Append(",\"day\":{\"before\":").Append(JsonUtil.Bool(dayBefore));
            json.Append(",\"after\":").Append(JsonUtil.Bool(dayAfter)).Append("}");
            json.Append(",\"night\":{\"before\":").Append(JsonUtil.Bool(nightBefore));
            json.Append(",\"after\":").Append(JsonUtil.Bool(nightAfter)).Append("}");
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult Rename(TransportManager manager, ushort line, string body, bool dryRun)
        {
            string name = JsonUtil.GetString(body, "name", "").Trim();

            string before = null;
            try { before = manager.GetLineName(line); } catch (Exception) { }

            if (name.Length == 0)
            {
                return CommandResult.Fail("name is required (a non-empty string; passing a line's generated name " +
                    "clears its custom-name flag instead).");
            }

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"rename\",\"line\":").Append(line);
                dry.Append(",\"name\":{\"before\":").Append(before == null ? "null" : "\"" + JsonUtil.Escape(before) + "\"");
                dry.Append(",\"after\":\"").Append(JsonUtil.Escape(name)).Append("\"}}");
                return CommandResult.FromJson(dry.ToString());
            }

            // SetLineName is a one-step coroutine in the live game: all of its work completes
            // on the first MoveNext. Enumerate it fully so the name is applied now, then
            // verify through the game's own GetLineName readback.
            using (IEnumerator<bool> steps = manager.SetLineName(line, name))
            {
                while (steps.MoveNext())
                {
                }
            }

            string after = null;
            try { after = manager.GetLineName(line); } catch (Exception) { }
            bool applied = string.Compare(name, after, StringComparison.Ordinal) == 0;

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"rename\",\"line\":").Append(line);
            json.Append(",\"name\":{\"before\":").Append(before == null ? "null" : "\"" + JsonUtil.Escape(before) + "\"");
            json.Append(",\"after\":").Append(after == null ? "null" : "\"" + JsonUtil.Escape(after) + "\"}");
            json.Append(",\"applied\":").Append(JsonUtil.Bool(applied));
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static CommandResult SetVehicle(TransportManager manager, ushort line, string body, bool dryRun)
        {
            string vehicleName = JsonUtil.GetString(body, "vehicle", "").Trim();

            TransportLine data = manager.m_lines.m_buffer[line];
            VehicleInfo current = null;
            try { current = data.GetLineVehicle(line); } catch (Exception) { }
            string currentName = current == null ? null : current.name;
            int currentIndex;
            manager.TryGetSelectedLineVehicle(line, out currentIndex);

            if (vehicleName.Length == 0)
            {
                return CommandResult.Fail("vehicle is required (a vehicle asset name). Compatible for line " + line + ": " +
                    DescribeCompatibleVehicles(data.Info, currentName));
            }

            int prefabIndex = manager.GetVehicleIndex(vehicleName);
            if (prefabIndex < 0)
            {
                return CommandResult.Fail("Unknown vehicle: " + vehicleName + ". Compatible for line " + line + ": " +
                    DescribeCompatibleVehicles(data.Info, currentName));
            }

            if (dryRun)
            {
                StringBuilder dry = new StringBuilder();
                dry.Append("{\"ok\":true,\"dryRun\":true,\"action\":\"set-vehicle\",\"line\":").Append(line);
                dry.Append(",\"vehicle\":{\"before\":").Append(currentName == null ? "null" : "\"" + JsonUtil.Escape(currentName) + "\"");
                dry.Append(",\"after\":\"").Append(JsonUtil.Escape(vehicleName)).Append("\"}}");
                return CommandResult.FromJson(dry.ToString());
            }

            // The game's own name -> prefab index mapping (the same call the line editor makes).
            manager.AssignSelectedLineVehicle(line, prefabIndex);

            VehicleInfo after = null;
            try { after = manager.m_lines.m_buffer[line].GetLineVehicle(line); } catch (Exception) { }
            string afterName = after == null ? null : after.name;
            bool applied = afterName == vehicleName;

            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"set-vehicle\",\"line\":").Append(line);
            json.Append(",\"vehicle\":{\"before\":").Append(currentName == null ? "null" : "\"" + JsonUtil.Escape(currentName) + "\"");
            json.Append(",\"after\":").Append(afterName == null ? "null" : "\"" + JsonUtil.Escape(afterName) + "\"}");
            json.Append(",\"prefabIndex\":{\"before\":").Append(currentIndex);
            json.Append(",\"after\":").Append(prefabIndex).Append("}");
            json.Append(",\"applied\":").Append(JsonUtil.Bool(applied));
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        // ----------------------------------------------------------------- helpers

        private static CommandResult LineStateResult(TransportManager manager, ushort line,
            string action, int stopCountBefore)
        {
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"dryRun\":false,\"action\":\"").Append(action).Append("\"");
            json.Append(",\"line\":").Append(line);
            json.Append(",\"stopCountBefore\":").Append(stopCountBefore);
            json.Append(",\"lineData\":");
            AppendLine(json, manager, line, manager.m_lines.m_buffer[line], MaxStopReports, 0);
            json.Append("}");
            return CommandResult.FromJson(json.ToString());
        }

        private static bool IsCreatedLine(TransportManager manager, ushort line)
        {
            TransportLine.Flags flags = manager.m_lines.m_buffer[line].m_flags;
            return flags != TransportLine.Flags.None &&
                (flags & TransportLine.Flags.Created) != 0 &&
                (flags & TransportLine.Flags.Deleted) == 0;
        }

        private static string DescribeCompatibleVehicles(TransportInfo info, string currentName)
        {
            StringBuilder list = new StringBuilder();
            if (info != null && info.m_class != null)
            {
                int loaded = PrefabCollection<VehicleInfo>.LoadedCount();
                int found = 0;
                for (int i = 0; i < loaded && found < MaxVehicleCandidates; i++)
                {
                    VehicleInfo vehicle = PrefabCollection<VehicleInfo>.GetLoaded((uint)i);
                    if (vehicle == null || vehicle.m_class == null ||
                        vehicle.m_class.m_service != info.m_class.m_service ||
                        vehicle.m_class.m_subService != info.m_class.m_subService)
                    {
                        continue;
                    }
                    if (list.Length > 0)
                    {
                        list.Append(", ");
                    }
                    list.Append(vehicle.name);
                    found++;
                }
            }

            if (list.Length == 0)
            {
                return "(no vehicle assets loaded for this class)";
            }
            return list.ToString() + " (current: " + (currentName == null ? "default" : currentName) + ")";
        }

        /// <summary>
        /// Serialises one line. stopReports / vehicleReports of 0 skip the corresponding
        /// list; the passenger counters are always reported.
        /// </summary>
        private static void AppendLine(StringBuilder json, TransportManager manager, ushort id, TransportLine data,
            int stopReports, int vehicleReports)
        {
            json.Append("{\"id\":").Append(id);
            json.Append(",\"lineNumber\":").Append(data.m_lineNumber);

            string name = null;
            try { name = manager.GetLineName(id); } catch (Exception) { }
            json.Append(",\"name\":").Append(name == null ? "null" : "\"" + JsonUtil.Escape(name) + "\"");

            AppendPrefab(json, data.Info, null);

            json.Append(",\"complete\":").Append(JsonUtil.Bool((data.m_flags & TransportLine.Flags.Complete) != 0));
            json.Append(",\"temporary\":").Append(JsonUtil.Bool((data.m_flags & TransportLine.Flags.Temporary) != 0));
            json.Append(",\"invalid\":").Append(JsonUtil.Bool((data.m_flags & TransportLine.Flags.Invalid) != 0));

            bool day;
            bool night;
            data.GetActive(out day, out night);
            json.Append(",\"dayActive\":").Append(JsonUtil.Bool(day));
            json.Append(",\"nightActive\":").Append(JsonUtil.Bool(night));

            json.Append(",\"totalLength\":").Append(JsonUtil.Number(data.m_totalLength));
            json.Append(",\"ticketPrice\":").Append(data.m_ticketPrice);

            int stopCount = data.CountStops(id);
            json.Append(",\"stopCount\":").Append(stopCount);
            if (stopReports > 0)
            {
                NetManager nets = Singleton<NetManager>.instance;
                StringBuilder stops = new StringBuilder();
                int emitted = 0;
                for (int i = 0; i < stopCount && emitted < stopReports; i++)
                {
                    ushort nodeId = data.GetStop(i);
                    if (nodeId == 0)
                    {
                        continue;
                    }
                    Vector3 position = nets.m_nodes.m_buffer[nodeId].m_position;
                    if (emitted > 0)
                    {
                        stops.Append(",");
                    }
                    stops.Append("{\"index\":").Append(i);
                    stops.Append(",\"node\":").Append(nodeId);
                    stops.Append(",\"position\":{\"x\":").Append(JsonUtil.Number(position.x));
                    stops.Append(",\"y\":").Append(JsonUtil.Number(position.y));
                    stops.Append(",\"z\":").Append(JsonUtil.Number(position.z)).Append("}}");
                    emitted++;
                }
                json.Append(",\"stops\":[").Append(stops.ToString()).Append("]");
            }

            int vehicleCount = data.CountVehicles(id);
            json.Append(",\"vehicleCount\":").Append(vehicleCount);
            if (vehicleReports > 0)
            {
                VehicleInfo selected = null;
                try { selected = data.GetLineVehicle(id); } catch (Exception) { }
                json.Append(",\"selectedVehicle\":").Append(selected == null
                    ? "null"
                    : "\"" + JsonUtil.Escape(selected.name) + "\"");

                VehicleManager vehicles = Singleton<VehicleManager>.instance;
                StringBuilder list = new StringBuilder();
                int emitted = 0;
                for (int i = 0; i < vehicleCount && emitted < vehicleReports; i++)
                {
                    ushort vehicleId = data.GetVehicle(i);
                    if (vehicleId == 0)
                    {
                        continue;
                    }
                    string vehicleName = null;
                    try { vehicleName = vehicles.m_vehicles.m_buffer[vehicleId].Info.name; } catch (Exception) { }
                    if (emitted > 0)
                    {
                        list.Append(",");
                    }
                    list.Append("{\"id\":").Append(vehicleId);
                    list.Append(",\"vehicle\":\"").Append(vehicleName == null ? "" : JsonUtil.Escape(vehicleName)).Append("\"}");
                    emitted++;
                }
                json.Append(",\"vehicles\":[").Append(list.ToString()).Append("]");
            }

            json.Append(",\"passengers\":").Append(BuildPassengerJson(data.m_passengers));
            json.Append("}");
        }

        /// <summary>
        /// The line's own passenger counters, summed over the game's citizen groups
        /// (resident, tourist, child, teen, young, adult, senior, car-owning). These are
        /// hit counters, not a boarding headcount — the transit panel works from the
        /// same numbers.
        /// </summary>
        private static string BuildPassengerJson(TransportPassengerData passengers)
        {
            uint temp = passengers.m_residentPassengers.m_tempCount +
                passengers.m_touristPassengers.m_tempCount +
                passengers.m_childPassengers.m_tempCount +
                passengers.m_teenPassengers.m_tempCount +
                passengers.m_youngPassengers.m_tempCount +
                passengers.m_adultPassengers.m_tempCount +
                passengers.m_seniorPassengers.m_tempCount +
                passengers.m_carOwningPassengers.m_tempCount;

            uint final = passengers.m_residentPassengers.m_finalCount +
                passengers.m_touristPassengers.m_finalCount +
                passengers.m_childPassengers.m_finalCount +
                passengers.m_teenPassengers.m_finalCount +
                passengers.m_youngPassengers.m_finalCount +
                passengers.m_adultPassengers.m_finalCount +
                passengers.m_seniorPassengers.m_finalCount +
                passengers.m_carOwningPassengers.m_finalCount;

            uint average = passengers.m_residentPassengers.m_averageCount +
                passengers.m_touristPassengers.m_averageCount +
                passengers.m_childPassengers.m_averageCount +
                passengers.m_teenPassengers.m_averageCount +
                passengers.m_youngPassengers.m_averageCount +
                passengers.m_adultPassengers.m_averageCount +
                passengers.m_seniorPassengers.m_averageCount +
                passengers.m_carOwningPassengers.m_averageCount;

            return "{\"temp\":" + temp + ",\"final\":" + final + ",\"average\":" + average + "}";
        }

        /// <summary>The fields every prefab row carries: name, classes, economics, vehicles.</summary>
        private static void AppendPrefab(StringBuilder json, TransportInfo info, List<VehicleInfo> vehicleInfos)
        {
            if (info == null)
            {
                json.Append("\"transportInfo\":null,\"transportType\":null,\"vehicleType\":null");
                json.Append(",\"service\":null,\"subService\":null,\"level\":null");
                return;
            }

            ItemClass itemClass = info.m_class;
            json.Append("\"transportInfo\":\"").Append(JsonUtil.Escape(info.name)).Append("\"");
            json.Append(",\"transportType\":\"").Append(info.m_transportType.ToString()).Append("\"");
            json.Append(",\"vehicleType\":\"").Append(info.m_vehicleType.ToString()).Append("\"");
            if (itemClass != null)
            {
                json.Append(",\"service\":\"").Append(itemClass.m_service.ToString()).Append("\"");
                json.Append(",\"subService\":\"").Append(itemClass.m_subService.ToString()).Append("\"");
                json.Append(",\"level\":\"").Append(itemClass.m_level.ToString()).Append("\"");
            }
            else
            {
                json.Append(",\"service\":null,\"subService\":null,\"level\":null");
            }
            json.Append(",\"ticketPrice\":").Append(info.m_ticketPrice);
            json.Append(",\"maintenanceCostPerVehicle\":").Append(info.m_maintenanceCostPerVehicle);
            json.Append(",\"citizenPullRequirement\":").Append(info.m_citizenPullRequirement);
            json.Append(",\"unlockMilestone\":").Append(
                info.m_UnlockMilestone == null ? "null" : "\"" + JsonUtil.Escape(info.m_UnlockMilestone.name) + "\"");

            if (vehicleInfos == null)
            {
                return;
            }

            StringBuilder vehicles = new StringBuilder();
            if (itemClass != null)
            {
                int emitted = 0;
                for (int i = 0; i < vehicleInfos.Count && emitted < MaxVehicleCandidates; i++)
                {
                    VehicleInfo vehicle = vehicleInfos[i];
                    if (vehicle.m_class == null ||
                        vehicle.m_class.m_service != itemClass.m_service ||
                        vehicle.m_class.m_subService != itemClass.m_subService)
                    {
                        continue;
                    }
                    if (emitted > 0)
                    {
                        vehicles.Append(",");
                    }
                    vehicles.Append("{\"name\":\"").Append(JsonUtil.Escape(vehicle.name)).Append("\"");
                    vehicles.Append(",\"level\":\"").Append(vehicle.m_class.m_level.ToString()).Append("\"}");
                    emitted++;
                }
            }
            json.Append(",\"vehicles\":[").Append(vehicles.ToString()).Append("]");
        }
    }
}
