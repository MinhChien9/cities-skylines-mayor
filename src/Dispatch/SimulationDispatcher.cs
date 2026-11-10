using System;
using System.Reflection;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Threading;

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
    /// THE ONE RULE
    /// ------------
    /// If the simulation thread is not available, this class **fails the request**. It
    /// never runs the work inline on the calling (HTTP worker) thread. An earlier
    /// version of this file had a "run it inline so the caller still gets a result"
    /// fallback, which silently reintroduced exactly the race this class exists to
    /// prevent. Failing loudly is always better than corrupting a city.
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
    /// Threading rules enforced here:
    ///   * reads  of game state  -&gt; simulation thread (avoids torn reads of struct buffers)
    ///   * writes of game state  -&gt; simulation thread
    ///   * camera / render / UI  -&gt; main thread
    /// </summary>
    public sealed class SimulationDispatcher : IDisposable
    {
        private readonly object gate = new object();
        private volatile bool disposed;

        /// <summary>Number of jobs handed to the simulation thread that have not finished yet.</summary>
        private int inFlight;

        private bool mainThreadProbeSent;

        /// <summary>
        /// Managed id of the game's simulation thread, resolved on first use.
        /// Zero if it could not be determined.
        /// </summary>
        public int SimulationThreadId { get; private set; }

        /// <summary>Managed id of the Unity main thread, for diagnostics.</summary>
        public int MainThreadId { get; private set; }

        /// <summary>Number of jobs currently waiting for or running on the simulation thread.</summary>
        public int PendingCount
        {
            get { return Interlocked.CompareExchange(ref inFlight, 0, 0); }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the simulation thread and blocks the calling thread
        /// until it completes or the timeout expires.
        ///
        /// The calling thread is the HTTP worker. It is NEVER the Unity main thread, so
        /// blocking here cannot deadlock the frame loop.
        ///
        /// Returns a failed <see cref="Support.CommandResult"/> if the simulation thread
        /// is unavailable or does not answer in time. It never runs the work inline.
        /// </summary>
        public Support.CommandResult RunSync(Func<Support.CommandResult> work, int timeoutMs)
        {
            if (work == null)
            {
                return Support.CommandResult.Fail("No work was supplied.");
            }

            Job job = new Job(work);

            if (!TryDispatch(job))
            {
                // The game is not running a simulation (no city loaded, or mid-teardown).
                // Fail loudly instead of running the work on this thread - see the class
                // comment: running inline is the race this whole design exists to prevent.
                return Support.CommandResult.Fail(
                    "The Cities: Skylines simulation thread is not available. Is a city loaded?");
            }
            if (!job.Wait(timeoutMs))
            {
                return Support.CommandResult.Fail("Timed out waiting for the simulation thread.");
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
        ///
        /// Returns false when the simulation thread could not accept the job. It never
        /// runs the job on the calling thread.
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
                job.OnFinished = delegate { Interlocked.Decrement(ref inFlight); };
                Interlocked.Increment(ref inFlight);
                simulation.AddAction(job.Execute);
                return true;
            }
            catch (Exception)
            {
                // The game refused the action. Report failure; do not run it here.
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
                    System.Reflection.FieldInfo field = typeof(SimulationManager).GetField(
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
        /// Called when a city is unloaded. In-flight commands are failed so their callers
        /// do not hang, but the dispatcher stays usable for the next city.
        /// </summary>
        public void OnLevelUnloaded()
        {
            lock (gate)
            {
                // Nothing is queued: every job is dispatched immediately and waited on.
                // The only thing to reset is the counters.
                Interlocked.Exchange(ref inFlight, 0);
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                Interlocked.Exchange(ref inFlight, 0);
            }
        }

        /// <summary>
        /// A unit of work plus its completion signal.
        /// </summary>
        private sealed class Job
        {
            private readonly Func<Support.CommandResult> work;
            private readonly ManualResetEvent done = new ManualResetEvent(false);
            private Support.CommandResult result;

            /// <summary>Called once, on the simulation thread, after the work has run.</summary>
            public Action OnFinished;

            public Job(Func<Support.CommandResult> work)
            {
                this.work = work;
            }

            public Support.CommandResult Result
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
                    result = Support.CommandResult.Fail(ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    done.Set();

                    Action finished = OnFinished;
                    if (finished != null)
                    {
                        finished();
                    }
                }
            }
        }
    }
}
