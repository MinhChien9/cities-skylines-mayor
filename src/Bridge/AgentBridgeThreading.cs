using ICities;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Hooks the game's threading extension so we can pump the dispatcher from the game's
    /// per-frame update.
    ///
    /// IMPORTANT: OnUpdate runs on the **Unity main thread**. The upstream project executed
    /// city mutations here, which races with the simulation thread. Here it only transfers
    /// queued work to the simulation thread, so nothing is ever mutated from this thread.
    ///
    /// This hook is optional: the dispatcher also dispatches directly when a request
    /// arrives. It exists so queued work still drains even if no request is in flight.
    /// </summary>
    public sealed class AgentBridgeThreading : ThreadingExtensionBase
    {
        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            AgentBridge.Instance.PumpSimulationQueue(realTimeDelta);
        }
    }
}
