using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Pumps the Unity main-thread queue every frame, from the moment the mod is constructed.
    ///
    /// The game's threading extension (<see cref="AgentBridgeThreading"/>) only runs while a
    /// city is loaded. Loading a save or starting a new game happens at the main menu, where
    /// that hook does not exist yet, and it must run on the main thread (it is what the
    /// game's own Load and New Game panels do). This behaviour lives on a
    /// DontDestroyOnLoad object, so it survives every scene change. Pumping the queue from
    /// two hooks is harmless: both run on the main thread, and a drained queue is a no-op.
    /// </summary>
    public sealed class AgentBridgePump : MonoBehaviour
    {
        private static GameObject host;

        /// <summary>Creates the pump once. Must be called on the Unity main thread.</summary>
        public static void Ensure()
        {
            if (host != null)
            {
                return;
            }
            host = new GameObject("SkylinesAgentBridgePump");
            Object.DontDestroyOnLoad(host);
            host.AddComponent<AgentBridgePump>();
        }

        private void Update()
        {
            AgentBridge.Instance.MainThreadQueue.Pump();
        }
    }
}
