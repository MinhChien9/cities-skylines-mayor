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
            AppendMilestones(json, managers);
            AppendFeatures(json);
            AppendClock(json, managers);
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
