using ICities;
using UnityEngine;

namespace SkylinesAgentBridge
{
    public sealed class AgentBridgeMod : IUserMod
    {
        public string Name
        {
            get { return "Skylines Agent Bridge"; }
        }

        public string Description
        {
            get { return "Localhost API bridge for AI agents to inspect and build in Cities: Skylines."; }
        }

        /// <summary>
        /// The game instantiates every IUserMod in order to read its name and description,
        /// so this constructor is the one hook that is guaranteed to run for a mod dropped
        /// into the Mods folder. The HTTP listener is started here rather than from a
        /// loading extension, because the extension plumbing depends on the mod being
        /// registered with the content manager.
        /// </summary>
        public AgentBridgeMod()
        {
            Debug.Log("[SkylinesAgentBridge] Mod constructed - starting the API bridge.");
            AgentBridgeSettings.Register();
            AgentBridge.Instance.Start();
        }

        /// <summary>
        /// The game calls this to build the mod's group in Content Manager > Options.
        /// </summary>
        public void OnSettingsUI(UIHelperBase helper)
        {
            AgentBridgeSettings.BuildOptions(helper);
        }
    }
}
