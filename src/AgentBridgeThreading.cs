using System;
using ColossalFramework;
using ICities;
using UnityEngine;

namespace SkylinesAgentBridge
{
    /// <summary>
    /// Hooks the game's threading extension so we can (a) capture the IThreading handle and
    /// (b) learn the managed ids of the Unity main thread and the simulation thread.
    ///
    /// IMPORTANT: OnUpdate runs on the **Unity main thread**. The upstream project executed
    /// city mutations here, which races with the simulation thread. We only use this hook to
    /// capture thread identity and to pump the dispatcher, which hands work to the
    /// simulation thread via IThreading.QueueSimulationThread.
    /// </summary>
    public sealed class AgentBridgeThreading : ThreadingExtensionBase
    {
        public override void OnCreated(IThreading threading)
        {
            base.OnCreated(threading);

            // The simulation thread is created by SimulationManager and named "Simulation".
            // Capture its managed id so diagnostics can prove which thread ran a command.
            int simulationThreadId = 0;
            try
            {
                System.Reflection.FieldInfo field = typeof(SimulationManager).GetField(
                    "m_simulationThread",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (field != null)
                {
                    System.Threading.Thread thread = field.GetValue(Singleton<SimulationManager>.instance) as System.Threading.Thread;
                    if (thread != null)
                    {
                        simulationThreadId = thread.ManagedThreadId;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[SkylinesAgentBridge] Could not resolve the simulation thread id: " + ex.Message);
            }

            AgentBridge.Instance.Dispatcher.Attach(
                threading,
                simulationThreadId,
                System.Threading.Thread.CurrentThread.ManagedThreadId);
        }

        public override void OnUpdate(float realTimeDelta, float simulationTimeDelta)
        {
            AgentBridge.Instance.PumpSimulationQueue(realTimeDelta);
        }
    }
}
