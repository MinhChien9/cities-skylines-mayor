using UnityEngine;

namespace SkylinesAgentBridge
{
    public sealed class AgentBridge
    {
        private static readonly AgentBridge singleton = new AgentBridge();
        private readonly SimulationDispatcher dispatcher = new SimulationDispatcher();
        private ApiServer server;
        private bool levelLoaded;

        public static AgentBridge Instance
        {
            get { return singleton; }
        }

        public bool LevelLoaded
        {
            get { return levelLoaded; }
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

        public void OnLevelLoaded()
        {
            levelLoaded = true;
            EnsureServer();
            AgentBridgeNotifier.Notify("API ready: CS1 Mayor Bridge");
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

        private void EnsureServer()
        {
            if (server != null && server.IsRunning)
            {
                return;
            }

            server = new ApiServer(this, 32123);
            server.Start();
        }
    }
}
