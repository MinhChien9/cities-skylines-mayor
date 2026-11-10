using System;
using ICities;
using UnityEngine;
using SkylinesAgentBridge.Threading;

namespace SkylinesAgentBridge
{
    public sealed class AgentBridge
    {
        private static readonly AgentBridge singleton = new AgentBridge();
        private readonly SimulationDispatcher dispatcher = new SimulationDispatcher();
        private ApiServer server;
        private bool levelLoaded;
        private bool started;
        private IManagers iCitiesManagers;

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

        /// <summary>
        /// The game's supported modding surface, handed over by
        /// <see cref="AgentBridgeThreading.OnCreated(ICities.IThreading)"/>, or null when the
        /// extension has not been created yet. Treated as an opaque handle — the wrappers it
        /// exposes are the game's own implementations.
        /// </summary>
        public IManagers ICitiesManagers
        {
            get { return iCitiesManagers; }
        }

        /// <summary>
        /// The game's supported modding surface, always non-null while a level is running.
        ///
        /// Primary path: <see cref="AgentBridgeThreading.OnCreated(ICities.IThreading)"/>, the
        /// supported hook. Fallback: construct the game's own public
        /// <c>ManagersWrapper</c> directly — for a mod dropped into the Mods folder the game
        /// loads the assembly and constructs <c>IUserMod</c> but never instantiates the
        /// extension bases (confirmed against the live game: neither
        /// <c>OnCreated</c> nor <c>OnLevelLoaded</c> fired), so the hook cannot be the only
        /// route. Called on the simulation thread, which is the only place game state may be
        /// read; the wrapper is stored once and never reflected into.
        /// </summary>
        public IManagers EnsureICitiesManagers()
        {
            if (iCitiesManagers != null)
            {
                return iCitiesManagers;
            }
            try
            {
                iCitiesManagers = new ManagersWrapper();
                Debug.Log("[SkylinesAgentBridge] OnCreated did not run for this install; captured the ICities surface via the game's public ManagersWrapper fallback.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SkylinesAgentBridge] Could not obtain the ICities surface: " + ex);
            }
            return iCitiesManagers;
        }

        public void CaptureICitiesManagers(IManagers managers)
        {
            if (managers == null)
            {
                Debug.LogWarning("[SkylinesAgentBridge] OnCreated delivered a null IManagers; ICities surface unavailable.");
                return;
            }
            iCitiesManagers = managers;
            Debug.Log("[SkylinesAgentBridge] ICities.IManagers captured — terrain, milestones, saves, camera and the simulation clock are now reachable.");
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
            AgentBridgeNotifier.Destroy();
            Debug.Log("[SkylinesAgentBridge] Level unloading.");
        }

        /// <summary>
        /// Called from the game's per-frame update hook on the Unity main thread.
        /// It never mutates game state itself.
        /// </summary>
        public void PumpSimulationQueue(float realTimeDelta)
        {
            dispatcher.Pump();
            AgentBridgeNotifier.Update(realTimeDelta);
        }
    }
}
