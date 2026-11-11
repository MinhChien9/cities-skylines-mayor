using System;
using System.Collections.Generic;
using System.Threading;
using SkylinesAgentBridge.Support;

namespace SkylinesAgentBridge.Threading
{
    /// <summary>
    /// The ONE sanctioned exception to the simulation-thread rule.
    ///
    /// WHY IT EXISTS
    /// -------------
    /// Almost everything the bridge does touches city state (ZoneManager, NetManager,
    /// BuildingManager, ...), and city state is owned by the simulation thread — so
    /// <see cref="SimulationDispatcher"/> routes all of it there and refuses to run
    /// anything inline when that thread is unavailable.
    ///
    /// The camera is different. Unity objects — the Camera, its transform, the render
    /// pipeline — are owned by the **Unity main thread**. Reading a camera transform
    /// from the simulation thread is exactly as racy as mutating a zone buffer from
    /// the HTTP worker. Camera framing (ICities.ICamera) and world→screen projection
    /// (Camera.WorldToScreenPoint) must therefore run on the main thread.
    ///
    /// WHAT MAY RUN HERE
    /// -----------------
    ///   * camera framing / state reads  (ICities.ICamera)
    ///   * world→screen projection       (UnityEngine.Camera.WorldToScreenPoint)
    ///   * render-only queries           (Screen dimensions, the window rect)
    ///   * UI-only toggles that live on the camera (the edge-scrolling SavedBool)
    ///
    /// WHAT MAY NEVER RUN HERE
    /// -----------------------
    /// Any read or write of city state. This queue is render-only by contract; the
    /// review rule is "if it touches a Manager, it goes through SimulationDispatcher".
    ///
    /// HOW IT WORKS
    /// ------------
    /// HTTP workers enqueue jobs and block; <see cref="Pump"/> is called from the
    /// game's per-frame hook (AgentBridgeThreading.OnUpdate), which the game runs on
    /// the Unity main thread every frame — including while the simulation is paused,
    /// so the camera can be aimed while the clock is stopped.
    /// </summary>
    public sealed class MainThreadQueue
    {
        private readonly object gate = new object();
        private readonly Queue<Job> queue = new Queue<Job>();

        private int lastRunThreadId;
        private long completed;
        private long failed;

        /// <summary>Managed id of the thread that ran the most recent job — the main thread.</summary>
        public int LastRunThreadId
        {
            get { return lastRunThreadId; }
        }

        /// <summary>Jobs queued but not yet drained by a frame.</summary>
        public int PendingCount
        {
            get
            {
                lock (gate)
                {
                    return queue.Count;
                }
            }
        }

        /// <summary>Jobs completed since the queue was created (diagnostics).</summary>
        public long CompletedCount
        {
            get { return Interlocked.Read(ref completed); }
        }

        /// <summary>Jobs that threw since the queue was created (diagnostics).</summary>
        public long FailedCount
        {
            get { return Interlocked.Read(ref failed); }
        }

        /// <summary>
        /// Runs <paramref name="work"/> on the Unity main thread and blocks the calling
        /// thread (an HTTP worker) until it completes or the timeout expires.
        ///
        /// Callers must never enqueue city-state work here; see the class comment.
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
                queue.Enqueue(job);
            }

            if (!job.Wait(timeoutMs))
            {
                return CommandResult.Fail(
                    "Timed out waiting for the Unity main thread. Is the game rendering frames?");
            }

            return job.Result;
        }

        /// <summary>
        /// Drains every queued job. Called from the game's per-frame hook on the Unity
        /// main thread; also safe to call from anywhere else for diagnostics.
        /// </summary>
        public void Pump()
        {
            while (true)
            {
                Job job;
                lock (gate)
                {
                    if (queue.Count == 0)
                    {
                        return;
                    }
                    job = queue.Dequeue();
                }

                lastRunThreadId = Thread.CurrentThread.ManagedThreadId;
                job.Execute();
                if (job.Result.Ok)
                {
                    Interlocked.Increment(ref completed);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
        }

        /// <summary>
        /// A unit of work plus its completion signal — same shape as the
        /// simulation dispatcher's job, so the two read alike.
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
