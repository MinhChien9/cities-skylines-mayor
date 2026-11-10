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
    ///
    /// OnCreated is the one supported hook that hands the mod an <c>ICities.IManagers</c>
    /// handle — the game's first-class modding surface (terrain, resources, save/load,
    /// milestones, camera, simulation clock, …). It is captured once and served through
    /// <see cref="AgentBridge.ICitiesManagers"/>; never reflected into, used only as an
    /// opaque handle. See docs/12-command-surface.md §4.0.
    /// </summary>
    public sealed class AgentBridgeThreading : ThreadingExtensionBase
    {
        public override void OnCreated(IThreading threading)
        {
            AgentBridge.Instance.CaptureICitiesManagers(threading.managers);
            base.OnCreated(threading);
        }

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            AgentBridge.Instance.PumpSimulationQueue(realTimeDelta);
        }
    }
}
