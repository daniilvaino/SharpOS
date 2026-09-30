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
// So this is deliberately not a scheduler. No work stealing, no continuation
// queue: a task runs on a thread of a small pool (TaskPool — threads wait for
// work instead of being spawned per task) and can be waited for, which is what
// the callers above ask for. Waits block on a word through the kernel's
// WaitOnAddress where the tier offers it, and poll with Sleep(1) where not.
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

        /// <summary>No timeout, for <see cref="WaitWhile"/>.</summary>
        public const uint Infinite = 0xFFFFFFFF;

        private static Func<ThreadEntry, bool>? s_spawn;
        private static Action<uint>? s_sleep;
        private static delegate*<void*, void*, uint, uint, bool> s_wait;
        private static delegate*<void*, void> s_wakeAll;

        public static bool IsAvailable => s_spawn != null && s_sleep != null;

        public static void Install(Func<ThreadEntry, bool> spawn, Action<uint> sleep)
        {
            s_spawn = spawn;
            s_sleep = sleep;
        }

        /// <summary>
        /// Blocking waits, where the tier has them: Win32 WaitOnAddress
        /// (address, compare, size, timeout ms → false on timeout) and
        /// WakeByAddressAll. Without them every wait here polls with
        /// Sleep(1) — which costs a whole timer tick per wait.
        /// </summary>
        public static void InstallWaits(delegate*<void*, void*, uint, uint, bool> wait, delegate*<void*, void> wakeAll)
        {
            s_wait = wait;
            s_wakeAll = wakeAll;
        }

        /// <summary>True when waits block rather than poll.</summary>
        internal static bool CanBlock => s_wait != null && s_wakeAll != null;

        internal static bool Spawn(ThreadEntry entry) => s_spawn != null && s_spawn(entry);

        internal static void Sleep(uint milliseconds)
        {
            if (s_sleep != null) s_sleep(milliseconds);
        }

        /// <summary>
        /// Returns once <paramref name="location"/> may no longer hold
        /// <paramref name="value"/>, or after the timeout. It may return
        /// early; callers re-check what they wait for. The objects holding
        /// these words never move (neither collector compacts), so their
        /// address is safe to hand to the kernel.
        /// </summary>
        internal static void WaitWhile(ref int location, int value, uint timeoutMs)
        {
            if (s_wait == null)
            {
                Sleep(1);
                return;
            }

            int expected = value;
            s_wait(System.Runtime.CompilerServices.Unsafe.AsPointer(ref location), &expected, sizeof(int), timeoutMs);
        }

        /// <summary>Wakes every thread in <see cref="WaitWhile"/> on this word.</summary>
        internal static void WakeAll(ref int location)
        {
            if (s_wakeAll != null)
                s_wakeAll(System.Runtime.CompilerServices.Unsafe.AsPointer(ref location));
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
        // 1 while set. The word waiters block on (ThreadBackend.WaitWhile).
        private int _state;

        public ManualResetEventSlim() { }
        public ManualResetEventSlim(bool initialState) { _state = initialState ? 1 : 0; }

        public bool IsSet => Interlocked.Read(ref _state) != 0;

        public void Set()
        {
            if (Interlocked.Exchange(ref _state, 1) == 0)
                ThreadBackend.WakeAll(ref _state);
        }

        public void Reset() => Interlocked.Exchange(ref _state, 0);

        public void Wait()
        {
            while (!IsSet) ThreadBackend.WaitWhile(ref _state, 0, ThreadBackend.Infinite);
        }

        public bool Wait(int millisecondsTimeout)
        {
            if (millisecondsTimeout < 0)
            {
                Wait();
                return true;
            }

            // One timed wait: the only wake on this word is Set. Without
            // blocking waits the backend sleeps a millisecond per call, so the
            // old count-the-milliseconds loop is what runs.
            if (IsSet) return true;
            if (millisecondsTimeout == 0) return false;
            for (int waited = 0; waited < millisecondsTimeout && !IsSet; waited++)
            {
                ThreadBackend.WaitWhile(ref _state, 0, (uint)(millisecondsTimeout - waited));
                if (ThreadBackend.CanBlock) break;
            }
            return IsSet;
        }

        public void Wait(CancellationToken token)
        {
            // Cancel wakes nobody, so the wait is cut into slices it can be
            // noticed between.
            while (!IsSet)
            {
                token.ThrowIfCancellationRequested();
                ThreadBackend.WaitWhile(ref _state, 0, 10);
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

            // The word Wait blocks on: 0 running, 2 running with a waiter,
            // 1 completed. The waiter mark lets completion skip the wake —
            // a call into the kernel — when nobody is waiting, which is most
            // tasks.
            private int _signal;

            // Pool bookkeeping: the work not yet started, and the link in the
            // pool's queue.
            private Action? _work;
            internal Task? _nextQueued;

            internal Task() { }

            internal Task(bool completed)
            {
                _completed = completed;
                _signal = completed ? 1 : 0;
            }

            public bool IsCompleted => _completed;
            public bool IsFaulted => _error != null;
            public Exception? Exception => _error;

            public static Task CompletedTask => new Task(completed: true);

            /// <summary>
            /// Runs <paramref name="action"/> on a pool thread (TaskPool). The
            /// callers this exists for pass loops that never return, so running
            /// inline was never an option — it would simply never come back —
            /// and the pool starts a thread whenever no idle one is waiting, so
            /// such a loop keeps a thread to itself.
            /// </summary>
            public static Task Run(Action action)
            {
                var task = new Task();
                task._work = action;
                if (!TaskPool.Queue(task))
                {
                    // No backend: say so by failing the task rather than running
                    // the loop here and hanging the caller forever.
                    task._work = null;
                    task._error = new InvalidOperationException(
                        "No thread backend installed — Task.Run has nowhere to run.");
                    task.Complete();
                }
                return task;
            }

            public static Task Run(Action action, CancellationToken token) => Run(action);

            /// <summary>Runs the queued work; called by a pool thread.</summary>
            internal void Execute()
            {
                Action? work = _work;
                _work = null;
                try { work?.Invoke(); }
                catch (Exception ex) { _error = ex; }
                finally { Complete(); }
            }

            private void Complete()
            {
                _completed = true;
                if (Interlocked.Exchange(ref _signal, 1) == 2)
                    ThreadBackend.WakeAll(ref _signal);
            }

            // Marks the word as waited on and blocks while it stays that way.
            private void WaitForSignal(uint timeoutMs)
            {
                Interlocked.CompareExchange(ref _signal, 2, 0);
                ThreadBackend.WaitWhile(ref _signal, 2, timeoutMs);
            }

            /// <summary>
            /// A task that completes after a delay. Deliberately does not hold a
            /// thread: the delay lives in Wait, because every caller here follows
            /// Delay with Wait immediately.
            /// </summary>
            public static Task Delay(int milliseconds) => new DelayTask(milliseconds);

            public static Task Delay(int milliseconds, CancellationToken token) =>
                new DelayTask(milliseconds);

            // Blocks on the completion word. It used to poll it with Sleep(1),
            // so every wait on an unfinished task cost a whole timer tick —
            // most of what a task cost on this tier (step173).
            public virtual void Wait()
            {
                while (!_completed) WaitForSignal(ThreadBackend.Infinite);
                Rethrow();
            }

            public virtual void Wait(CancellationToken token)
            {
                // Cancel wakes nobody: slices it can be noticed between.
                while (!_completed)
                {
                    token.ThrowIfCancellationRequested();
                    WaitForSignal(10);
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
                Complete();
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

        /// <summary>
        /// Threads that run queued tasks and wait for more, instead of one new
        /// thread per task.
        /// </summary>
        /// <remarks>
        /// A thread per task cost a spawn each time and never gave its memory
        /// back (72 KiB a thread; exited threads are not freed). Now threads
        /// wait for work, and one more is started only when tasks are queued,
        /// none is idle and none is already starting. That is checked when a
        /// task is queued and again whenever a thread takes one, so the pool
        /// grows one thread at a time while a backlog lasts: a burst of short
        /// tasks is drained by the threads there are, and a loop that never
        /// returns — Terminal.Gui's — holds its thread without holding up the
        /// queue behind it.
        ///
        /// Threads stay for the life of the kernel or the app; an app's are
        /// taken off the machine with it (Scheduler.LeaveApp).
        /// </remarks>
        internal static class TaskPool
        {
            private static Task? s_head;
            private static Task? s_tail;

            // Tasks waiting in the queue: the word idle threads wait on.
            private static int s_queued;

            // Threads waiting for a task, and threads spawned that have not
            // reached the loop yet. Both count as takers for queued tasks.
            private static int s_idle;
            private static int s_starting;

            // Idle threads already woken and not yet back from the wait. A
            // burst of tasks queued before the woken thread runs would
            // otherwise call into the kernel to wake it once per task.
            private static int s_waking;
            private static int s_threads;

            // 0 free, 1 held, 2 held with waiters.
            private static int s_lock;

            internal static bool Queue(Task task)
            {
                if (!ThreadBackend.IsAvailable) return false;

                bool wake;
                bool spawn;
                Lock();
                task._nextQueued = null;
                if (s_tail == null) s_head = task;
                else s_tail._nextQueued = task;
                s_tail = task;
                s_queued++;
                wake = s_idle > s_waking;
                if (wake) s_waking = s_idle;   // WakeAll wakes every one of them
                spawn = NeedThread();
                Unlock();

                if (wake)
                    ThreadBackend.WakeAll(ref s_queued);

                if (spawn && !StartThread())
                {
                    // Nobody will ever take it when there is no thread at all:
                    // take it back and let the caller fail the task. With
                    // threads about it waits for the next one to come free.
                    Lock();
                    bool orphan = s_threads == 0 && s_starting == 0 && Remove(task);
                    Unlock();
                    if (orphan) return false;
                }
                return true;
            }

            // Under the lock. Claims the start when it says yes.
            private static bool NeedThread()
            {
                if (s_head == null || s_idle > 0 || s_starting > 0) return false;
                s_starting++;
                return true;
            }

            private static bool StartThread()
            {
                if (ThreadBackend.Spawn(Worker)) return true;
                Lock();
                s_starting--;
                Unlock();
                return false;
            }

            private static void Worker()
            {
                Lock();
                s_starting--;
                s_threads++;
                Unlock();

                while (true)
                {
                    bool spawn = false;
                    Lock();
                    Task? task = s_head;
                    if (task != null)
                    {
                        s_head = task._nextQueued;
                        if (s_head == null) s_tail = null;
                        task._nextQueued = null;
                        s_queued--;
                        spawn = NeedThread();   // a backlog behind this task
                    }
                    else
                    {
                        s_idle++;
                    }
                    Unlock();

                    if (task != null)
                    {
                        if (spawn) StartThread();
                        task.Execute();
                        continue;
                    }

                    // Queue emptied under the lock; a task queued since then
                    // changed the word, and the wait returns at once.
                    ThreadBackend.WaitWhile(ref s_queued, 0, ThreadBackend.Infinite);

                    // Every thread counted idle when a wake went out comes back
                    // through here — woken, or finding the word already changed
                    // before it parked — so the count cannot overstate who is
                    // on the way.
                    Lock();
                    s_idle--;
                    if (s_waking > 0) s_waking--;
                    Unlock();
                }
            }

            private static bool Remove(Task task)
            {
                Task? prev = null;
                Task? c = s_head;
                while (c != null && c != task) { prev = c; c = c._nextQueued; }
                if (c == null) return false;
                if (prev == null) s_head = c._nextQueued;
                else prev._nextQueued = c._nextQueued;
                if (s_tail == c) s_tail = prev;
                c._nextQueued = null;
                s_queued--;
                return true;
            }

            // A futex mutex (Drepper, "Futexes Are Tricky"): uncontended, one
            // compare-exchange each way; contended, the waiter blocks on the
            // word and the holder wakes it on the way out. Contention needs a
            // holder that lost the CPU inside — only possible under preemption.
            private static void Lock()
            {
                int c = Interlocked.CompareExchange(ref s_lock, 1, 0);
                if (c == 0) return;
                if (c != 2) c = Interlocked.Exchange(ref s_lock, 2);
                while (c != 0)
                {
                    ThreadBackend.WaitWhile(ref s_lock, 2, ThreadBackend.Infinite);
                    c = Interlocked.Exchange(ref s_lock, 2);
                }
            }

            private static void Unlock()
            {
                if (Interlocked.Exchange(ref s_lock, 0) == 2)
                    ThreadBackend.WakeAll(ref s_lock);
            }
        }
    }
}
