using ICities;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Hooks the game's threading extension so we can pump the dispatcher from the game's
    /// per-frame update.
    ///
    /// IMPORTANT: OnUpdate runs on the **Unity main thread**. The upstream project executed
    /// city mutations here, which races with the simulation thread. Here it only learns the
    /// main thread's id, so nothing is ever mutated from this thread.
    /// </summary>
    public sealed class AgentBridgeThreading : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            AgentBridge.Instance.PumpSimulationQueue(realTimeDelta);
        }
    }
}
