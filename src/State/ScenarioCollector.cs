using System.Text;
using ColossalFramework;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.State
{
    /// <summary>
    /// GET /state/scenario: the scenario's win and lose triggers with every condition's live
    /// progress, read the way the in-game "Scenario goals" tab reads them
    /// (UnlockManager.m_scenarioTriggers, MilestoneInfo.GetLocalizedProgress / IsPassed).
    /// </summary>
    public static class ScenarioCollector
    {
        public static CommandResult BuildScenarioJson()
        {
            TriggerMilestone[] triggers = Singleton<UnlockManager>.instance.m_scenarioTriggers;
            StringBuilder json = new StringBuilder();
            json.Append("{\"ok\":true,\"isScenario\":").Append(JsonUtil.Bool(triggers != null && triggers.Length != 0));
            json.Append(",\"triggers\":[");
            bool first = true;
            if (triggers != null)
            {
                foreach (TriggerMilestone trigger in triggers)
                {
                    if (trigger == null)
                    {
                        continue;
                    }
                    if (!first)
                    {
                        json.Append(",");
                    }
                    first = false;
                    AppendTrigger(json, trigger);
                }
            }
            json.Append("]");
            AppendMilestones(json);
            json.Append(",\"notes\":\"kind is win/lose from the trigger's effects (WinEffect/LoseEffect); other triggers are scenario events. conditionOrder Simultaneous = all conditions must hold at the same time. current/min/max are the game's own progress-bar values.\"}");
            return CommandResult.FromJson(json.ToString());
        }

        /// <summary>The next progression milestone and what each zone type waits for.</summary>
        private static void AppendMilestones(StringBuilder json)
        {
            UnlockManager unlock = Singleton<UnlockManager>.instance;
            MilestoneInfo current = unlock.GetCurrentMilestone();
            json.Append(",\"currentMilestone\":");
            if (current == null)
            {
                json.Append("null");
            }
            else
            {
                MilestoneInfo.ProgressInfo progress = current.GetLocalizedProgress();
                json.Append("{\"name\":\"").Append(JsonUtil.Escape(current.name)).Append("\"");
                json.Append(",\"title\":\"").Append(JsonUtil.Escape(current.GetLocalizedName())).Append("\"");
                json.Append(",\"description\":\"").Append(JsonUtil.Escape(progress.m_description)).Append("\"");
                json.Append(",\"progress\":\"").Append(JsonUtil.Escape(progress.m_progress)).Append("\"}");
            }
            json.Append(",\"zoneMilestones\":{");
            MilestoneInfo[] zones = unlock.m_properties == null ? null : unlock.m_properties.m_ZoneMilestones;
            bool first = true;
            if (zones != null)
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    if (!first)
                    {
                        json.Append(",");
                    }
                    first = false;
                    MilestoneInfo m = zones[i];
                    json.Append("\"").Append(((ItemClass.Zone)i).ToString()).Append("\":");
                    json.Append(m == null ? "null" : "{\"milestone\":\"" + JsonUtil.Escape(m.name) + "\",\"unlocked\":" + JsonUtil.Bool(unlock.Unlocked(m)) + "}");
                }
            }
            json.Append("}");
        }

        private static void AppendTrigger(StringBuilder json, TriggerMilestone trigger)
        {
            string kind = "event";
            if (trigger.m_effects != null)
            {
                foreach (TriggerEffect effect in trigger.m_effects)
                {
                    if (effect is WinEffect) { kind = "win"; }
                    else if (effect is LoseEffect) { kind = "lose"; }
                }
            }
            json.Append("{\"name\":\"").Append(JsonUtil.Escape(trigger.GetLocalizedName())).Append("\"");
            json.Append(",\"kind\":\"").Append(kind).Append("\"");
            json.Append(",\"conditionOrder\":\"").Append(trigger.m_conditionOrder.ToString()).Append("\"");
            json.Append(",\"passed\":").Append(JsonUtil.Bool(trigger.IsPassed()));
            json.Append(",\"conditions\":");
            AppendConditions(json, trigger.m_conditions);
            json.Append("}");
        }

        private static void AppendConditions(StringBuilder json, MilestoneInfo[] conditions)
        {
            json.Append("[");
            bool first = true;
            if (conditions != null)
            {
                foreach (MilestoneInfo condition in conditions)
                {
                    if (condition == null || condition.m_hidden)
                    {
                        continue;
                    }
                    if (!first)
                    {
                        json.Append(",");
                    }
                    first = false;
                    MilestoneInfo.ProgressInfo progress = condition.GetLocalizedProgress();
                    json.Append("{\"type\":\"").Append(condition.GetType().Name).Append("\"");
                    json.Append(",\"description\":\"").Append(JsonUtil.Escape(progress.m_description)).Append("\"");
                    json.Append(",\"progress\":\"").Append(JsonUtil.Escape(progress.m_progress)).Append("\"");
                    json.Append(",\"current\":").Append(JsonUtil.Number(progress.m_current));
                    json.Append(",\"min\":").Append(JsonUtil.Number(progress.m_min));
                    json.Append(",\"max\":").Append(JsonUtil.Number(progress.m_max));
                    json.Append(",\"passed\":").Append(JsonUtil.Bool(condition.IsPassed()));
                    TriggerMilestone nested = condition as TriggerMilestone;
                    if (nested != null)
                    {
                        json.Append(",\"conditionOrder\":\"").Append(nested.m_conditionOrder.ToString()).Append("\"");
                        json.Append(",\"conditions\":");
                        AppendConditions(json, nested.m_conditions);
                    }
                    json.Append("}");
                }
            }
            json.Append("]");
        }
    }
}
