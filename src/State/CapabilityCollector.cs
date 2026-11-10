using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using ICities;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// Builds <c>GET /state/capabilities</c>: what the running game supports, read through
    /// the supported <c>ICities</c> surface (<see cref="ICities.IManagers"/>) plus
    /// <c>UnlockManager</c> for the milestone/feature state.
    ///
    /// Reports:
    ///   * game version (<see cref="IApplication"/>) and which DLC it can see
    ///   * the milestone names (<see cref="IMilestones"/>) and the current one
    ///   * the known milestone-gated features, locked/unlocked (<c>UnlockManager</c>)
    ///   * the simulation clock (<see cref="IThreading"/>) — a first-class read that
    ///     replaces estimating progress from the date string in /state/summary
    ///
    /// Every section is wrapped in its own guard so one broken surface does not starve the
    /// others — the same policy the thread probe follows.
    ///
    /// IMPORTANT: this runs on the simulation thread, and
    /// <see cref="AgentBridge.EnsureICitiesManagers"/> may construct the wrapper there on
    /// first use (OnCreated does not fire for a locally-dropped mod).
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
                // The wrapper can throw if application state is not ready yet; the other
                // sections still have their answer.
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
                Array values = Enum.GetValues(typeof(Expansion));
                StringBuilder supported = new StringBuilder();
                StringBuilder detected = new StringBuilder();
                bool firstSupported = true;
                bool firstDetected = true;
                foreach (Expansion value in values)
                {
                    if (value == Expansion.None)
                    {
                        continue;
                    }

                    if (firstDetected)
                    {
                        firstDetected = false;
                    }
                    else
                    {
                        detected.Append(",");
                    }
                    detected.Append("\"").Append(JsonUtil.Escape(value.ToString())).Append("\"");

                    bool isSupported = managers.application.SupportsExpansion(value);
                    if (isSupported)
                    {
                        if (firstSupported)
                        {
                            firstSupported = false;
                        }
                        else
                        {
                            supported.Append(",");
                        }
                        supported.Append("\"").Append(JsonUtil.Escape(value.ToString())).Append("\"");
                    }
                }

                json.Append(",\"installedExpansions\":[").Append(detected.ToString()).Append("]");
                json.Append(",\"supportedExpansions\":[").Append(supported.ToString()).Append("]");
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
                Array.Sort(milestones, StringComparer.Ordinal);
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

                // The milestone the city is currently working towards. Report its internal
                // name — the same string UnlockMilestone expects.
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
            catch (Exception)
            {
            }
        }

        private static void AppendFeatures(StringBuilder json)
        {
            // UnlockManager.Feature is the gate list for everything milestone-gated:
            // GameAreas, ParkAreas, CampusAreas, Ferry, Trolleybus, PedestrianZones, ...
            // This is how the agent asks "can I use metro yet?" instead of guessing.
            try
            {
                UnlockManager unlock = UnlockManager.instance;
                Array values = Enum.GetValues(typeof(UnlockManager.Feature));
                List<string> unlocked = new List<string>();
                List<string> locked = new List<string>();
                foreach (UnlockManager.Feature value in values)
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

                AppendStringArray(json, "unlockedFeatures", unlocked);
                AppendStringArray(json, "lockedFeatures", locked);
            }
            catch (Exception)
            {
            }
        }

        private static void AppendClock(StringBuilder json, IManagers managers)
        {
            // IThreading carries the simulation clock as a first-class read:
            // simulationTime / simulationFrame / simulationTick. Comparing two reads tells
            // the agent how much simulated time actually elapsed — the guard against
            // drawing conclusions after zero sim-days.
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

        private static void AppendStringArray(StringBuilder json, string name, List<string> values)
        {
            json.Append(",\"").Append(name).Append("\":[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    json.Append(",");
                }
                json.Append("\"").Append(JsonUtil.Escape(values[i])).Append("\"");
            }
            json.Append("]");
        }
    }
}
