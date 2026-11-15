using System;
using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// The mod's own player settings, kept in the game's settings folder
    /// (SkylinesAgentBridge.cgs) so they survive restarts. Main thread only.
    /// </summary>
    public static class AgentBridgeSettings
    {
        private const string SettingsFileName = "SkylinesAgentBridge";

        private static bool registered;
        private static SavedBool showConsole;
        private static UICheckBox showConsoleCheckBox;

        /// <summary>
        /// Registers the settings file with the game. Called from the IUserMod
        /// constructor, before any setting is read.
        /// </summary>
        public static void Register()
        {
            // Not FindSettingsFileByName: it logs a warning whenever the file is not
            // registered yet. The game may construct the mod more than once.
            if (registered)
            {
                return;
            }
            registered = true;
            try
            {
                GameSettings.AddSettingsFile(new SettingsFile { fileName = SettingsFileName });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SkylinesAgentBridge] Settings file not registered: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether the in-game API console is shown. Its close button turns it off;
        /// the checkbox in the mod's Options panel turns it back on.
        /// </summary>
        public static bool ShowConsole
        {
            get { return Saved().value; }
            set
            {
                Saved().value = value;
                if (showConsoleCheckBox != null && showConsoleCheckBox.isChecked != value)
                {
                    showConsoleCheckBox.isChecked = value;
                }
            }
        }

        /// <summary>
        /// Builds the mod's group in Content Manager > Options.
        /// </summary>
        public static void BuildOptions(UIHelperBase helper)
        {
            UIHelperBase group = helper.AddGroup("Skylines Agent Bridge");
            showConsoleCheckBox = group.AddCheckbox(
                "Show the API console (the in-game log of bridge calls)",
                ShowConsole,
                delegate(bool isChecked) { ShowConsole = isChecked; }) as UICheckBox;
        }

        private static SavedBool Saved()
        {
            if (showConsole == null)
            {
                Register();
                showConsole = new SavedBool("showApiConsole", SettingsFileName, true, true);
            }
            return showConsole;
        }
    }
}
