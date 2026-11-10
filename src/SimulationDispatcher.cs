using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using ColossalFramework;
using ColossalFramework.Threading;

namespace SkylinesAgentBridge
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
    ///     ZoneTool     -> AddAction(BeginZoning()) / AddAction(EndZoning(...))
    ///     BuildingTool -> AddAction(CreateBuilding())
    ///     NetTool      -> AddAction(CreateNode(...))
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
    /// Threading rules enforced here:
    ///   * reads  of game state  -> simulation thread (avoids torn reads of struct buffers)
    ///   * writes of game state  -> simulation thread
    ///   * camera / render / UI  -> main thread
    /// </summary>
    public sealed class SimulationDispatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<Job> pending = new Queue<Job>();
        private volatile bool disposed;
        private bool mainThreadProbeSent;

        /// <summary>
        /// Managed id of the game's simulation thread, resolved on first use.
        /// Zero if it could not be determined.
        /// </summary>
        public int SimulationThreadId { get; private set; }

        /// <summary>Managed id of the Unity main thread, for diagnostics.</summary>
        public int MainThreadId { get; private set; }

        /// <summary>Number of jobs currently waiting to run on the simulation thread.</summary>
        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return pending.Count;
                }
            }
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

            // NOTE: the job is dispatched directly, not queued. Queuing it here as well
            // would leave it in `pending` forever and leak one entry per request.
            if (!TryDispatch(job))
            {
                // The game is not running a simulation (no city loaded, or mid-teardown).
                // Run inline so the caller still gets a result instead of hanging.
                job.Execute();
            }

            if (!job.Wait(timeoutMs))
            {
                return CommandResult.Fail("Timed out waiting for the simulation thread.");
            }

            return job.Result;
        }

        /// <summary>
        /// Schedules work to run on the simulation thread without waiting for it.
        /// </summary>
        public void Post(Func<CommandResult> work)
        {
            if (work == null)
            {
                return;
            }
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                pending.Enqueue(new Job(work));
            }
            TryDispatch(null);
        }

        /// <summary>
        /// Schedules work to run on the Unity main thread.
        ///
        /// Use this for anything that touches Unity objects (UI panels, GameObjects, the
        /// camera, Texture2D/RenderTexture for screenshots). Game state must NOT be read or
        /// written from here.
        /// </summary>
        public void PostMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            try
            {
                ThreadHelper.dispatcher.Dispatch(delegate
                {
                    // Runs on the Unity main thread - a reliable place to learn its id.
                    if (MainThreadId == 0)
                    {
                        MainThreadId = Thread.CurrentThread.ManagedThreadId;
                    }
                    action();
                });
            }
            catch (Exception)
            {
                // The game is tearing down; running inline is the least-bad option.
                action();
            }
        }

        /// <summary>
        /// Called from the game's per-frame hook. Drains the pending queue by handing the
        /// jobs to the simulation thread.
        ///
        /// This is the ONLY place the upstream project used to execute game mutations
        /// directly. Here we only *transfer* ownership - never mutate state ourselves.
        /// </summary>
        public void Pump()
        {
            // Pump() is only ever called from the game's per-frame hook, which runs on the
            // Unity main thread. Capture that id here so /debug/threads can compare against
            // it. It must NOT be captured from TryDispatch, because that runs on the HTTP
            // worker thread and would report the worker as the main thread.
            if (MainThreadId == 0)
            {
                MainThreadId = Thread.CurrentThread.ManagedThreadId;
            }

            DrainQueued();
        }

        /// <summary>
        /// Drains a batch of queued jobs onto the simulation thread.
        /// </summary>
        private void DrainQueued()
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    if (pending.Count == 0)
                    {
                        return;
                    }
                    job = pending.Dequeue();
                }

                SimulationManager simulation = Singleton<SimulationManager>.instance;
                if (simulation == null)
                {
                    job.Execute();
                    continue;
                }
                simulation.AddAction(job.Execute);
            }
        }

        /// <summary>
        /// Moves queued jobs onto the simulation thread. Safe to call from any thread.
        /// </summary>
        private bool TryDispatch(Job specific)
        {
            SimulationManager simulation = Singleton<SimulationManager>.instance;
            if (simulation == null)
            {
                // No simulation yet (main menu, or before the city loaded).
                if (specific != null)
                {
                    specific.Execute();
                    return true;
                }
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

                if (specific != null)
                {
                    // AddAction is exactly what ZoneTool / BuildingTool / NetTool use.
                    simulation.AddAction(specific.Execute);
                    return true;
                }

                Job job;
                lock (gate)
                {
                    if (pending.Count == 0)
                    {
                        return true;
                    }
                    job = pending.Dequeue();
                }
                simulation.AddAction(job.Execute);
                return true;            }
            catch (Exception)
            {
                if (specific != null)
                {
                    specific.Execute();
                    return true;
                }
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

        public void Dispose()
        {
            lock (gate)
            {
                disposed = true;
                while (pending.Count > 0)
                {
                    pending.Dequeue().Cancel("The dispatcher was shut down.");
                }
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

            public void Cancel(string message)
            {
                result = CommandResult.Fail(message);
                done.Set();
            }
        }
    }
}
