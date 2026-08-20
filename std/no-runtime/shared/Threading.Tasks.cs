// Tasks, the part that does not care where threads come from.
//
// Written because Terminal.Gui needs them, and it needs them in a specific and
// modest way: not for computation, but to keep background loops running while
// the user interface waits for input.
//
//     Task.Run (ProcessInputResultQueue, token)   // read and decode keys
//     Task.Run (CheckWinChange, token)            // watch for a resize
//     Task.Delay (100, token).Wait (token)        // sleep, interruptibly
//
// So this is deliberately not a scheduler. There is no thread pool, no work
// stealing, no continuation queue: a task is a thread plus a way to wait for it,
// which is exactly what the callers above ask for and nothing more. Building the
// real thing first would have been guessing at requirements we can already read.
//
// Where the threads come from is the one thing that differs between the kernel
// and an app, and that is the only thing behind ThreadBackend.

using System.Runtime.CompilerServices;

namespace System.Threading
{
    /// <summary>
    /// How this tier makes a thread and how it sleeps. The kernel wires this to
    /// its scheduler; an app will wire it to the service table once threads are
    /// exposed there. Everything above is shared.
    /// </summary>
    public static unsafe class ThreadBackend
    {
        public delegate void ThreadEntry();

        private static Func<ThreadEntry, bool>? s_spawn;
        private static Action<uint>? s_sleep;

        public static bool IsAvailable => s_spawn != null && s_sleep != null;

        public static void Install(Func<ThreadEntry, bool> spawn, Action<uint> sleep)
        {
            s_spawn = spawn;
            s_sleep = sleep;
        }

        internal static bool Spawn(ThreadEntry entry) => s_spawn != null && s_spawn(entry);

        internal static void Sleep(uint milliseconds)
        {
            if (s_sleep != null) s_sleep(milliseconds);
        }
    }

    public sealed class CancellationTokenSource
    {
        internal bool _cancelled;

        public CancellationToken Token => new CancellationToken(this);
        public bool IsCancellationRequested => _cancelled;

        public void Cancel() => _cancelled = true;
        public void Dispose() { }
    }

    public readonly struct CancellationToken
    {
        private readonly CancellationTokenSource? _source;

        internal CancellationToken(CancellationTokenSource source) { _source = source; }

        public static CancellationToken None => default;

        public bool IsCancellationRequested => _source != null && _source._cancelled;

        public void ThrowIfCancellationRequested()
        {
            if (IsCancellationRequested) throw new OperationCanceledException();
        }
    }

    public class OperationCanceledException : Exception
    {
        public OperationCanceledException() : base("The operation was canceled.") { }
        public OperationCanceledException(string message) : base(message) { }
    }

    /// <summary>
    /// A latch that stays open once set. Terminal.Gui uses it to hand control
    /// between its input thread and its main loop; it is the same shape as the
    /// kernel's own Event, and the backend below is what makes waiting cheap.
    /// </summary>
    public sealed class ManualResetEventSlim
    {
        private volatile bool _set;

        public ManualResetEventSlim() { }
        public ManualResetEventSlim(bool initialState) { _set = initialState; }

        public bool IsSet => _set;

        public void Set() => _set = true;
        public void Reset() => _set = false;

        public void Wait()
        {
            // Polling rather than blocking: a proper wait needs the backend to
            // expose one, and until an app has threads at all there is nothing
            // to block on. The interval is small enough to feel instant and
            // large enough not to burn the CPU the way our old spin-waits did.
            while (!_set) ThreadBackend.Sleep(1);
        }

        public bool Wait(int millisecondsTimeout)
        {
            for (int waited = 0; waited < millisecondsTimeout; waited++)
            {
                if (_set) return true;
                ThreadBackend.Sleep(1);
            }
            return _set;
        }

        public void Wait(CancellationToken token)
        {
            while (!_set)
            {
                token.ThrowIfCancellationRequested();
                ThreadBackend.Sleep(1);
            }
        }

        public void Dispose() { }
    }

    namespace Tasks
    {
        /// <summary>
        /// A piece of work running on its own thread, and a way to wait for it.
        /// </summary>
        public partial class Task
        {
            private volatile bool _completed;
            private Exception? _error;

            internal Task() { }

            internal Task(bool completed) { _completed = completed; }

            public bool IsCompleted => _completed;
            public bool IsFaulted => _error != null;
            public Exception? Exception => _error;

            public static Task CompletedTask => new Task(completed: true);

            /// <summary>
            /// Starts <paramref name="action"/> on a thread of its own. The
            /// callers this exists for pass loops that never return, so running
            /// inline was never an option — it would simply never come back.
            /// </summary>
            public static Task Run(Action action)
            {
                var task = new Task();
                if (!ThreadBackend.Spawn(() => task.Body(action)))
                {
                    // No backend: say so by failing the task rather than running
                    // the loop here and hanging the caller forever.
                    task._error = new InvalidOperationException(
                        "No thread backend installed — Task.Run has nowhere to run.");
                    task._completed = true;
                }
                return task;
            }

            public static Task Run(Action action, CancellationToken token) => Run(action);

            private void Body(Action action)
            {
                try { action(); }
                catch (Exception ex) { _error = ex; }
                finally { _completed = true; }
            }

            /// <summary>
            /// A task that completes after a delay. Deliberately does not hold a
            /// thread: the delay lives in Wait, because every caller here follows
            /// Delay with Wait immediately.
            /// </summary>
            public static Task Delay(int milliseconds) => new DelayTask(milliseconds);

            public static Task Delay(int milliseconds, CancellationToken token) =>
                new DelayTask(milliseconds);

            public virtual void Wait()
            {
                while (!_completed) ThreadBackend.Sleep(1);
                Rethrow();
            }

            public virtual void Wait(CancellationToken token)
            {
                while (!_completed)
                {
                    token.ThrowIfCancellationRequested();
                    ThreadBackend.Sleep(1);
                }
                Rethrow();
            }

            private protected void Rethrow()
            {
                if (_error != null) throw _error;
            }

            /// <summary>
            /// Completion driven by the async builder rather than by a thread
            /// returning. Kept internal so the only ways a task can finish stay
            /// the two the design has: work ending, or a state machine ending.
            /// </summary>
            private protected void CompleteFromBuilder(Exception? error)
            {
                _error = error;
                _completed = true;
            }

            private sealed class DelayTask : Task
            {
                private readonly int _milliseconds;

                internal DelayTask(int milliseconds) { _milliseconds = milliseconds; }

                public override void Wait() => Sleep(CancellationToken.None);

                public override void Wait(CancellationToken token) => Sleep(token);

                private void Sleep(CancellationToken token)
                {
                    // Split so cancellation is noticed while waiting rather than
                    // half a second later: the callers pass 100 ms and 500 ms and
                    // expect Ctrl+C-grade responsiveness out of them.
                    for (int left = _milliseconds; left > 0; left -= 10)
                    {
                        token.ThrowIfCancellationRequested();
                        ThreadBackend.Sleep(left < 10 ? (uint)left : 10u);
                    }
                }
            }
        }
    }
}
