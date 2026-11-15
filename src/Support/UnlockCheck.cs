using ColossalFramework;

namespace SkylinesAgentBridge.Support
{
    /// <summary>
    /// Milestone gating for prefabs. The player's toolbar only offers a building or network
    /// once its unlock milestone has been passed; the game's tools themselves do not check it.
    /// Without this the bridge could place a locked building, which is a cheat (and in a
    /// scenario, a false win).
    /// </summary>
    public static class UnlockCheck
    {
        public static bool IsUnlocked(PrefabInfo info)
        {
            if (info == null)
            {
                return false;
            }
            MilestoneInfo milestone = info.GetUnlockMilestone();
            return milestone == null || Singleton<UnlockManager>.instance.Unlocked(milestone);
        }

        /// <summary>The zone tool offers a zone once UnlockManager's zone milestone has passed.</summary>
        public static bool IsZoneUnlocked(ItemClass.Zone zone)
        {
            if (zone == ItemClass.Zone.Unzoned || zone == ItemClass.Zone.None)
            {
                return true;
            }
            UnlockManager unlock = Singleton<UnlockManager>.instance;
            MilestoneInfo[] milestones = unlock.m_properties == null ? null : unlock.m_properties.m_ZoneMilestones;
            if (milestones == null || (int)zone >= milestones.Length)
            {
                return true;
            }
            MilestoneInfo milestone = milestones[(int)zone];
            return milestone == null || unlock.Unlocked(milestone);
        }

        public static string MilestoneName(PrefabInfo info)
        {
            MilestoneInfo milestone = info == null ? null : info.GetUnlockMilestone();
            return milestone == null ? "" : milestone.name;
        }

        public static string LockedMessage(PrefabInfo info, string prefabName)
        {
            return "Prefab is locked: " + prefabName + " unlocks at milestone '" + MilestoneName(info) +
                "'. The player cannot build it yet; reach the milestone first.";
        }
    }
}
