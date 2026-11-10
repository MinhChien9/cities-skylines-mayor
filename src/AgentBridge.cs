using System;
using UnityEngine;

namespace SkylinesAgentBridge
{
    public sealed class AgentBridge
    {
        private static readonly AgentBridge singleton = new AgentBridge();
        private readonly SimulationDispatcher dispatcher = new SimulationDispatcher();
        private ApiServer server;
        private bool levelLoaded;
        private bool started;

        public static AgentBridge Instance
        {
            get { return singleton; }
        }

        /// <summary>
        /// True when a city (or any level) is loaded.
        ///
        /// Read from LoadingManager.m_loadingComplete, which the game maintains as a
        /// volatile bool. Using it avoids depending on the ILoadingExtension plugin hook
        /// actually firing, which is not guaranteed for a locally-installed mod.
        /// </summary>
        public bool LevelLoaded
        {
            get
            {
                if (levelLoaded)
                {
                    return true;
                }
                try
                {
                    LoadingManager loading = ColossalFramework.Singleton<LoadingManager>.instance;
                    if (loading != null && loading.m_loadingComplete)
                    {
                        levelLoaded = true;
                        return true;
                    }
                }
                catch (Exception)
                {
                }
                return false;
            }
        }

        /// <summary>
        /// Executes game work on the CS1 simulation thread.
        /// Replaces the upstream <c>CommandQueue</c>, which drained on the Unity main thread
        /// and raced with the simulation thread over the same game buffers.
        /// </summary>
        public SimulationDispatcher Dispatcher
        {
            get { return dispatcher; }
        }

        public bool IsRunning
        {
            get { return started && server != null && server.IsRunning; }
        }

        /// <summary>
        /// Starts the HTTP listener. Safe to call more than once.
        ///
        /// Called from the IUserMod constructor, which the game always instantiates in
        /// order to read the mod's name and description. That makes it the one hook we can
        /// rely on for a mod that is dropped into the Mods folder rather than installed
        /// through the content manager.
        /// </summary>
        public void Start()
        {
            if (started)
            {
                return;
            }
            started = true;

            try
            {
                server = new ApiServer(this, 32123);
                server.Start();
                Debug.Log("[SkylinesAgentBridge] API bridge listening on http://127.0.0.1:" + 32123);
            }
            catch (Exception ex)
            {
                Debug.LogError("[SkylinesAgentBridge] Failed to start the API bridge: " + ex);
            }
        }

        public void OnLevelLoaded()
        {
            levelLoaded = true;
            Debug.Log("[SkylinesAgentBridge] Level loaded. API bridge is ready.");
        }

        public void OnLevelUnloading()
        {
            levelLoaded = false;
            dispatcher.Dispose();
            AgentBridgeNotifier.Destroy();
            Debug.Log("[SkylinesAgentBridge] Level unloading. Pending API commands cleared.");
        }

        /// <summary>
        /// Called from the game's per-frame update hook on the Unity main thread.
        /// It only *transfers* queued work to the simulation thread - it never mutates
        /// game state itself.
        /// </summary>
        public void PumpSimulationQueue(float realTimeDelta)
        {
            dispatcher.Pump();
            AgentBridgeNotifier.Update(realTimeDelta);
        }
    }
}
