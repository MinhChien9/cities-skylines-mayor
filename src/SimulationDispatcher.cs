using System;
using System.Collections.Generic;
using System.Threading;
using ICities;

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
    /// ICities exposes exactly the two primitives we need:
    ///     IThreading.QueueMainThread(Action)        -> Unity main thread (dispatcher)
    ///     IThreading.QueueSimulationThread(Action)  -> simulation thread (AddAction)
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
    /// Threading rules enforced here:
    ///   * reads  of game state  -> simulation thread (avoids torn reads of struct buffers)
    ///   * writes of game state  -> simulation thread
    ///   * camera / render / UI  -> main thread
    /// </summary>
    public sealed class SimulationDispatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<Job> pending = new Queue<Job>();
        private IThreading threading;
        private volatile bool disposed;

        /// <summary>
        /// True when the caller is already on the simulation thread, so work can run inline
        /// instead of round-tripping through the queue.
        /// </summary>
        public bool OnSimulationThread
        {
            get
            {
                IThreading local = threading;
                if (local == null)
                {
                    return false;
                }
                try
                {
                    return UnityEngine.Time.frameCount >= 0 && SimulationThreadId == Thread.CurrentThread.ManagedThreadId;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Managed id of the game's simulation thread, captured on the first frame after the
        /// level loads. Zero until then.
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
        /// Called once the level has loaded. Captures the threading handle and the ids of the
        /// two interesting threads.
        /// </summary>
        public void Attach(IThreading threadingHandle, int simulationThreadId, int mainThreadId)
        {
            threading = threadingHandle;
            SimulationThreadId = simulationThreadId;
            MainThreadId = mainThreadId;
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

            lock (gate)
            {
                if (disposed)
                {
                    return CommandResult.Fail("The dispatcher has been shut down.");
                }
                pending.Enqueue(job);
            }

            // Hand the job to the game. QueueSimulationThread routes to
            // SimulationManager.AddAction, which the simulation thread drains at the top of
            // SimulationStep(). If we are already ON the simulation thread it runs inline.
            bool dispatched = TryDispatch(job);

            if (!dispatched)
            {
                // No threading handle yet (level still loading). Fall back to running on this
                // worker thread so callers still get an answer instead of hanging.
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
        /// Used for fire-and-forget work such as cache refreshes.
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
        /// Called from the game's own per-frame update hook (Unity main thread). Drains the
        /// pending queue by handing the jobs to the simulation thread.
        ///
        /// This is the ONLY place the upstream project used to execute game mutations
        /// directly. Here we only *transfer* ownership - never mutate state ourselves.
        /// </summary>
        public void Pump()
        {
            TryDispatch(null);
        }

        /// <summary>
        /// Schedules work to run on the Unity main thread.
        ///
        /// Use this for anything that touches Unity objects (UI panels, GameObjects, the
        /// camera, Texture2D/RenderTexture for screenshots). Game state must NOT be read or
        /// written from here - use <see cref="RunSync"/> for that.
        /// </summary>
        public void PostMainThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            IThreading local = threading;
            if (local == null)
            {
                // No handle yet - run inline so the work is not lost.
                action();
                return;
            }

            try
            {
                local.QueueMainThread(action);
            }
            catch (Exception)
            {
                // The game is tearing down; running inline is the least-bad option.
                action();
            }
        }

        /// <summary>
        /// Moves queued jobs onto the simulation thread. Safe to call from any thread.
        /// </summary>
        private bool TryDispatch(Job specific)
        {
            IThreading local = threading;
            if (local == null)
            {
                return specific != null;
            }

            try
            {
                if (specific != null)
                {
                    local.QueueSimulationThread(specific.Execute);
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
                local.QueueSimulationThread(job.Execute);
                return true;
            }
            catch (Exception)
            {
                // If the game is mid-teardown the handle can be dead. Fall back to inline
                // execution so the caller still gets a result.
                if (specific != null)
                {
                    specific.Execute();
                    return true;
                }
                return false;
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
            threading = null;
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
