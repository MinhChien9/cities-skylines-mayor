using System;
using System.Reflection;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Threading;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Threading
{
    /// <summary>
    /// Executes game-state work on the CS1 *simulation* thread.
    ///
    /// WHY THIS CLASS EXISTS (read before touching any command code)
    /// -----------------------------------------------------------
    /// Cities: Skylines runs two relevant threads:
    ///   * the Unity **main thread**   - rendering, Unity objects, UI, MonoBehaviour.Update
    ///   * the **simulation thread**   - a dedicated Thread named "Simulation" created by
    ///                                    SimulationManager, which owns and mutates city state
    ///                                    (ZoneManager, NetManager, BuildingManager,
    ///                                     CitizenManager, EconomyManager, DistrictManager).
    ///
    /// Every mutation the *game itself* performs is funnelled through
    /// SimulationManager.AddAction(...) from the game's own tools:
    ///
    ///     ZoneTool     -&gt; AddAction(BeginZoning()) / AddAction(EndZoning(...))
    ///     BuildingTool -&gt; AddAction(CreateBuilding())
    ///     NetTool      -&gt; AddAction(CreateNode(...))
    ///
    /// ...and SimulationManager.SimulationStep() drains that queue on the simulation thread.
    ///
    /// The upstream project this was forked from drained its command queue from
    /// ThreadingExtensionBase.OnUpdate, which the game calls from SimulationManager.Update()
    /// -- i.e. the **Unity main thread**. That races with the simulation thread over the
    /// same FastList buffers. The symptom is not a crash; it is a city that is subtly
    /// wrong and a save you cannot diagnose. Upstream works around this by catching
    /// InvalidOperationException("Already in the same thread. Call directly") and then
    /// reflecting into private *Implementation methods to bypass the game's own guard.
    ///
    /// This dispatcher does the opposite: it does the mutation on the thread that owns
    /// the state, so no guard is hit and no reflection is needed.
    ///
    /// IMPORTANT DESIGN NOTE
    /// ---------------------
    /// This dispatcher calls SimulationManager.AddAction directly instead of going through
    /// ICities.IThreading.QueueSimulationThread. Both end up in the same place, but the
    /// ICities route depends on the game's plugin/extension plumbing having discovered
    /// this assembly. That plumbing is not guaranteed for a locally-dropped mod, and if
    /// it is missing the dispatcher would silently fall back to running game mutations on
    /// the HTTP worker thread - exactly the race we are trying to eliminate. Calling
    /// AddAction directly removes that dependency entirely and matches what the game's
    /// own tools do.
    ///
    /// If the simulation thread cannot take the job, the request fails. The work is never
    /// run inline on the calling (HTTP worker) thread.
    ///
    /// Threading rules enforced here:
    ///   * reads  of game state  -&gt; simulation thread (avoids torn reads of struct buffers)
    ///   * writes of game state  -&gt; simulation thread
    ///   * camera / render / UI  -&gt; main thread
    /// </summary>
    public sealed class SimulationDispatcher
    {
        private bool mainThreadProbeSent;

        private int inFlight;

        /// <summary>
        /// Managed id of the game's simulation thread, resolved on first use.
        /// Zero if it could not be determined.
        /// </summary>
        public int SimulationThreadId { get; private set; }

        /// <summary>Managed id of the Unity main thread, for diagnostics.</summary>
        public int MainThreadId { get; private set; }

        /// <summary>Number of commands currently running on the simulation thread.</summary>
        public int PendingCount
        {
            get { return inFlight; }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the simulation thread and blocks the calling thread
        /// until it completes or the timeout expires.
        ///
        /// The calling thread is the HTTP worker. It is NEVER the Unity main thread, so
        /// blocking here cannot deadlock the frame loop.
        /// </summary>
        public CommandResult RunSync(Func<CommandResult> work, int timeoutMs)
        {
            if (work == null)
            {
                return CommandResult.Fail("No work was supplied.");
            }

            Job job = new Job(work);
            Interlocked.Increment(ref inFlight);

            if (!TryDispatch(job))
            {
                Interlocked.Decrement(ref inFlight);
                return CommandResult.Fail(
                    "The Cities: Skylines simulation thread is not available. Is a city loaded?");
            }

            bool completed = job.Wait(timeoutMs);
            Interlocked.Decrement(ref inFlight);

            if (!completed)
            {
                return CommandResult.Fail("Timed out waiting for the simulation thread.");
            }

            return job.Result;
        }

        /// <summary>
        /// Called from the game's per-frame hook, which runs on the Unity main thread.
        ///
        /// Its only job is to learn the Unity main thread's id so that /debug/threads can
        /// prove the bridge is NOT running on it. It must not capture the id from
        /// TryDispatch, because that runs on the HTTP worker thread and would report the
        /// worker as the main thread.
        /// </summary>
        public void Pump()
        {
            if (MainThreadId == 0)
            {
                MainThreadId = Thread.CurrentThread.ManagedThreadId;
            }
        }

        /// <summary>
        /// Moves a job onto the simulation thread. Safe to call from any thread.
        /// Returns false when the simulation thread could not accept the job.
        /// </summary>
        private bool TryDispatch(Job job)
        {
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            if (simulation == null)
            {
                // No simulation yet (main menu, or before the city loaded).
                return false;
            }

            try
            {
                ResolveThreadIds(simulation);

                // Learn the Unity main thread id the first time we can. Dispatching a
                // no-op runs it on the Unity main thread, which is the only place it can
                // be observed reliably (the per-frame hook may not fire for a locally
                // installed mod).
                EnsureMainThreadId();

                // AddAction is exactly what ZoneTool / BuildingTool / NetTool use.
                simulation.AddAction(job.Execute);
                return true;
            }
            catch (Exception)
            {
                // The game refused the action.
                return false;
            }
        }

        /// <summary>
        /// Dispatches a one-shot probe to the Unity main thread to learn its managed id.
        /// Guarded so it only happens once.
        /// </summary>
        private void EnsureMainThreadId()
        {
            if (MainThreadId != 0 || mainThreadProbeSent)
            {
                return;
            }
            mainThreadProbeSent = true;
            try
            {
                ThreadHelper.dispatcher.Dispatch(delegate
                {
                    MainThreadId = Thread.CurrentThread.ManagedThreadId;
                });
            }
            catch (Exception)
            {
                // The dispatcher is not ready yet; Pump() will capture it later.
                mainThreadProbeSent = false;
            }
        }

        /// <summary>
        /// Resolves the managed ids of the simulation thread and the Unity main thread so
        /// that /debug/threads can prove which thread actually ran a command.
        /// </summary>
        private void ResolveThreadIds(SimulationManager simulation)
        {
            if (SimulationThreadId == 0)
            {
                try
                {
                    FieldInfo field = typeof(SimulationManager).GetField(
                        "m_simulationThread",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    Thread thread = field != null ? field.GetValue(simulation) as Thread : null;
                    if (thread != null)
                    {
                        SimulationThreadId = thread.ManagedThreadId;
                    }
                }
                catch (Exception)
                {
                    SimulationThreadId = 0;
                }
            }

            if (MainThreadId == 0)
            {
                // NOTE: deliberately NOT set here. TryDispatch runs on the HTTP worker
                // thread, so capturing "the current thread" there would mislabel the
                // worker as the Unity main thread. MainThreadId is captured in Pump(),
                // which only runs on the Unity main thread.
            }
        }

        /// <summary>
        /// A unit of work plus its completion signal.
        /// </summary>
        private sealed class Job
        {
            private readonly Func<CommandResult> work;
            private readonly ManualResetEvent done = new ManualResetEvent(false);
            private CommandResult result;

            public Job(Func<CommandResult> work)
            {
                this.work = work;
            }

            public CommandResult Result
            {
                get { return result; }
            }

            public bool Wait(int timeoutMs)
            {
                return done.WaitOne(timeoutMs, false);
            }

            public void Execute()
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    result = CommandResult.Fail(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    done.Set();
                }
            }
        }
    }
}
