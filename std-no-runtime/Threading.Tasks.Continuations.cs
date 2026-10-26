// Continuations on std's Task: ContinueWith, WhenAny, WhenAll, WaitAsync,
// the option enums and TaskScheduler.Default.
//
// API ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs,
//   Future.cs, TaskContinuation.cs, TaskScheduler.cs, TaskFactory.cs (the enums).
// The members, the option values and what each option means are the BCL's;
// the mechanism is std's: a task keeps a list of actions that Complete runs
// (Threading.Tasks.cs), each inline (ExecuteSynchronously) or queued to the
// task pool, as the BCL's continuations are.
//
// Cuts:
//   - TaskScheduler: Default and Current only, both the task pool. Its
//     constructor is not public — a scheduler of one's own has nowhere to plug
//     in, since std's Task runs on the pool and nowhere else.
//   - WhenAll/WaitAsync report one exception, unwrapped (std's Task holds one;
//     no AggregateException — Threading.Tasks.Result.cs).
//   - A continuation on Task.Delay's task waits on a pool thread: that task
//     completes by being waited on (Threading.Tasks.cs, DelayTask).
//   - AttachedToParent / DenyChildAttach / HideScheduler / LongRunning /
//     PreferFairness are accepted and have no effect.

using System.Collections.Generic;

namespace System.Threading.Tasks
{
    [Flags]
    public enum TaskCreationOptions
    {
        None = 0x0,
        PreferFairness = 0x01,
        LongRunning = 0x02,
        AttachedToParent = 0x04,
        DenyChildAttach = 0x08,
        HideScheduler = 0x10,
        RunContinuationsAsynchronously = 0x40,
    }

    [Flags]
    public enum TaskContinuationOptions
    {
        None = 0,
        PreferFairness = 0x01,
        LongRunning = 0x02,
        AttachedToParent = 0x04,
        DenyChildAttach = 0x08,
        HideScheduler = 0x10,
        LazyCancellation = 0x20,
        RunContinuationsAsynchronously = 0x40,
        NotOnRanToCompletion = 0x10000,
        NotOnFaulted = 0x20000,
        NotOnCanceled = 0x40000,
        OnlyOnRanToCompletion = NotOnFaulted | NotOnCanceled,
        OnlyOnFaulted = NotOnRanToCompletion | NotOnCanceled,
        OnlyOnCanceled = NotOnRanToCompletion | NotOnFaulted,
        ExecuteSynchronously = 0x80000,
    }

    public abstract class TaskScheduler
    {
        // SharpOS cut: not protected — see the header.
        private protected TaskScheduler() { }

        private static TaskScheduler? s_default;

        /// <summary>The task pool.</summary>
        public static TaskScheduler Default => s_default ??= new PoolScheduler();

        /// <summary>The pool as well: there is no other scheduler to be running on.</summary>
        public static TaskScheduler Current => Default;

        public virtual int MaximumConcurrencyLevel => int.MaxValue;

        private sealed class PoolScheduler : TaskScheduler { }
    }

    public partial class Task
    {
        private sealed class Continuation
        {
            public readonly Action Action;
            public readonly bool Synchronous;

            public Continuation(Action action, bool synchronous)
            {
                Action = action;
                Synchronous = synchronous;
            }
        }

        // Guarded by s_continuationLock; null once Complete has taken them.
        private List<Continuation>? _continuations;

        // TaskCreationOptions.RunContinuationsAsynchronously (TaskCompletionSource).
        internal bool _runContinuationsAsynchronously;

        private object? _asyncState;

        /// <summary>The state object given when the task was created.</summary>
        public object? AsyncState => _asyncState;

        internal void SetAsyncState(object? state) => _asyncState = state;

        /// <summary>
        /// Runs <paramref name="action"/> once the task has completed: at once
        /// when it already has, otherwise from Complete. Synchronous actions
        /// run on the completing thread, the rest on the pool.
        /// </summary>
        internal void AddContinuation(Action action, bool synchronous)
        {
            if (this is DelayTask)
            {
                QueueContinuation(() =>
                {
                    try { Wait(); } catch (Exception) { }
                    action();
                });
                return;
            }

            ContinuationLock();
            if (!_completed)
            {
                (_continuations ??= new List<Continuation>()).Add(new Continuation(action, synchronous));
                ContinuationUnlock();
                return;
            }
            ContinuationUnlock();
            RunContinuation(action, synchronous);
        }

        private void RunContinuations()
        {
            ContinuationLock();
            List<Continuation>? list = _continuations;
            _continuations = null;
            ContinuationUnlock();

            if (list == null) return;
            foreach (Continuation c in list)
                RunContinuation(c.Action, c.Synchronous && !_runContinuationsAsynchronously);
        }

        private static void RunContinuation(Action action, bool synchronous)
        {
            if (synchronous) action();
            else QueueContinuation(action);
        }

        // ---- ContinueWith (Task) ----

        public Task ContinueWith(Action<Task> continuationAction)
            => ContinueWith(continuationAction, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task> continuationAction, CancellationToken cancellationToken)
            => ContinueWith(continuationAction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task> continuationAction, TaskScheduler scheduler)
            => ContinueWith(continuationAction, CancellationToken.None, TaskContinuationOptions.None, scheduler);

        public Task ContinueWith(Action<Task> continuationAction, TaskContinuationOptions continuationOptions)
            => ContinueWith(continuationAction, CancellationToken.None, continuationOptions, TaskScheduler.Current);

        public Task ContinueWith(Action<Task> continuationAction, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationAction, nameof(continuationAction));
            var next = new Task();
            Continue(next, continuationOptions, cancellationToken, scheduler, () => { continuationAction(this); next.TrySetResult(); });
            return next;
        }

        public Task ContinueWith(Action<Task, object?> continuationAction, object? state)
            => ContinueWith(continuationAction, state, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task, object?> continuationAction, object? state, CancellationToken cancellationToken)
            => ContinueWith(continuationAction, state, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task, object?> continuationAction, object? state, TaskScheduler scheduler)
            => ContinueWith(continuationAction, state, CancellationToken.None, TaskContinuationOptions.None, scheduler);

        public Task ContinueWith(Action<Task, object?> continuationAction, object? state, TaskContinuationOptions continuationOptions)
            => ContinueWith(continuationAction, state, CancellationToken.None, continuationOptions, TaskScheduler.Current);

        public Task ContinueWith(Action<Task, object?> continuationAction, object? state, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationAction, nameof(continuationAction));
            var next = new Task();
            next._asyncState = state;
            Continue(next, continuationOptions, cancellationToken, scheduler, () => { continuationAction(this, state); next.TrySetResult(); });
            return next;
        }

        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction)
            => ContinueWith(continuationFunction, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, CancellationToken cancellationToken)
            => ContinueWith(continuationFunction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, TaskScheduler scheduler)
            => ContinueWith(continuationFunction, CancellationToken.None, TaskContinuationOptions.None, scheduler);

        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, TaskContinuationOptions continuationOptions)
            => ContinueWith(continuationFunction, CancellationToken.None, continuationOptions, TaskScheduler.Current);

        public Task<TResult> ContinueWith<TResult>(Func<Task, TResult> continuationFunction, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationFunction, nameof(continuationFunction));
            var next = new Task<TResult>();
            Continue(next, continuationOptions, cancellationToken, scheduler, () => next.TrySetResult(continuationFunction(this)));
            return next;
        }

        public Task<TResult> ContinueWith<TResult>(Func<Task, object?, TResult> continuationFunction, object? state)
            => ContinueWith(continuationFunction, state, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task<TResult> ContinueWith<TResult>(Func<Task, object?, TResult> continuationFunction, object? state, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationFunction, nameof(continuationFunction));
            var next = new Task<TResult>();
            next._asyncState = state;
            Continue(next, continuationOptions, cancellationToken, scheduler, () => next.TrySetResult(continuationFunction(this, state)));
            return next;
        }

        /// <summary>
        /// The common part of every ContinueWith: validates as the BCL does,
        /// cancels the continuation task with its token (at once, unless
        /// LazyCancellation), and when this task completes either runs
        /// <paramref name="body"/> or, if the NotOn* options exclude this
        /// outcome, cancels the continuation task. An exception from the body
        /// faults it — or cancels it, for an OperationCanceledException carrying
        /// the continuation's own canceled token.
        /// </summary>
        private protected void Continue(Task next, TaskContinuationOptions options, CancellationToken cancellationToken, TaskScheduler scheduler, Action body)
        {
            ArgumentNullException.ThrowIfNull(scheduler, nameof(scheduler));
            const TaskContinuationOptions NotOnAnything =
                TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.NotOnFaulted | TaskContinuationOptions.NotOnCanceled;
            if ((options & NotOnAnything) == NotOnAnything)
                throw new ArgumentOutOfRangeException(nameof(options), "The specified TaskContinuationOptions excluded all continuation kinds.");
            if ((options & TaskContinuationOptions.LongRunning) != 0 && (options & TaskContinuationOptions.ExecuteSynchronously) != 0)
                throw new ArgumentOutOfRangeException(nameof(options), "The specified TaskContinuationOptions combined LongRunning and ExecuteSynchronously.  Synchronous continuations should not be long running.");

            next._runContinuationsAsynchronously = (options & TaskContinuationOptions.RunContinuationsAsynchronously) != 0;

            if (cancellationToken.CanBeCanceled && (options & TaskContinuationOptions.LazyCancellation) == 0)
            {
                next._cancelToken = cancellationToken;
                if (cancellationToken.IsCancellationRequested)
                    next.TrySetCanceled(cancellationToken);
                else
                    cancellationToken.Register(static s => ((Task)s!).TrySetCanceled(((Task)s!)._cancelToken), next);
            }

            AddContinuation(() =>
            {
                if (next.IsCompleted) return;   // canceled early
                if (cancellationToken.IsCancellationRequested) { next.TrySetCanceled(cancellationToken); return; }
                if (!ShouldRun(options)) { next.TrySetCanceled(default); return; }
                try
                {
                    body();
                }
                catch (OperationCanceledException oce) when (cancellationToken.IsCancellationRequested && oce.CancellationToken == cancellationToken)
                {
                    next.TrySetCanceled(cancellationToken, oce);
                }
                catch (Exception e)
                {
                    next.TrySetException(e);
                }
            }, (options & TaskContinuationOptions.ExecuteSynchronously) != 0);
        }

        // The token a continuation task is canceled with (Continue).
        private CancellationToken _cancelToken;

        private bool ShouldRun(TaskContinuationOptions options)
        {
            if (IsCanceled) return (options & TaskContinuationOptions.NotOnCanceled) == 0;
            if (IsFaulted) return (options & TaskContinuationOptions.NotOnFaulted) == 0;
            return (options & TaskContinuationOptions.NotOnRanToCompletion) == 0;
        }

        // ---- WhenAny ----

        public static Task<Task> WhenAny(params Task[] tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAnyCore<Task>(tasks, nameof(tasks));
        }

        public static Task<Task> WhenAny(Task task1, Task task2)
        {
            ArgumentNullException.ThrowIfNull(task1, nameof(task1));
            ArgumentNullException.ThrowIfNull(task2, nameof(task2));
            return WhenAnyCore<Task>(new[] { task1, task2 }, "tasks");
        }

        public static Task<Task> WhenAny(IEnumerable<Task> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAnyCore<Task>(new List<Task>(tasks), nameof(tasks));
        }

        public static Task<Task<TResult>> WhenAny<TResult>(params Task<TResult>[] tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAnyCore<Task<TResult>>(tasks, nameof(tasks));
        }

        public static Task<Task<TResult>> WhenAny<TResult>(Task<TResult> task1, Task<TResult> task2)
        {
            ArgumentNullException.ThrowIfNull(task1, nameof(task1));
            ArgumentNullException.ThrowIfNull(task2, nameof(task2));
            return WhenAnyCore<Task<TResult>>(new[] { task1, task2 }, "tasks");
        }

        public static Task<Task<TResult>> WhenAny<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAnyCore<Task<TResult>>(new List<Task<TResult>>(tasks), nameof(tasks));
        }

        private static Task<TTask> WhenAnyCore<TTask>(IList<TTask> tasks, string paramName) where TTask : Task
        {
            if (tasks.Count == 0) throw new ArgumentException("The tasks argument contains no tasks.", paramName);
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i] == null) throw new ArgumentException("The tasks argument included a null value.", paramName);

            var result = new Task<TTask>();
            for (int i = 0; i < tasks.Count; i++)
            {
                TTask task = tasks[i];
                task.AddContinuation(() => result.TrySetResult(task), synchronous: true);
                if (result.IsCompleted) break;
            }
            return result;
        }

        // ---- WhenAll ----

        public static Task WhenAll(params Task[] tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAllCore(tasks, nameof(tasks));
        }

        public static Task WhenAll(IEnumerable<Task> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAllCore(new List<Task>(tasks), nameof(tasks));
        }

        public static Task<TResult[]> WhenAll<TResult>(params Task<TResult>[] tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAllResults(tasks, nameof(tasks));
        }

        public static Task<TResult[]> WhenAll<TResult>(IEnumerable<Task<TResult>> tasks)
        {
            ArgumentNullException.ThrowIfNull(tasks, nameof(tasks));
            return WhenAllResults(new List<Task<TResult>>(tasks), nameof(tasks));
        }

        private static Task WhenAllCore(IList<Task> tasks, string paramName)
        {
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i] == null) throw new ArgumentException("The tasks argument included a null value.", paramName);

            var result = new Task();
            if (tasks.Count == 0) { result.TrySetResult(); return result; }

            int remaining = tasks.Count;
            for (int i = 0; i < tasks.Count; i++)
                tasks[i].AddContinuation(() =>
                {
                    if (Interlocked.Decrement(ref remaining) == 0) CompleteFromAll(result, tasks);
                }, synchronous: true);
            return result;
        }

        private static Task<TResult[]> WhenAllResults<TResult>(IList<Task<TResult>> tasks, string paramName)
        {
            for (int i = 0; i < tasks.Count; i++)
                if (tasks[i] == null) throw new ArgumentException("The tasks argument included a null value.", paramName);

            var result = new Task<TResult[]>();
            if (tasks.Count == 0) { result.TrySetResult(new TResult[0]); return result; }

            int remaining = tasks.Count;
            for (int i = 0; i < tasks.Count; i++)
                tasks[i].AddContinuation(() =>
                {
                    if (Interlocked.Decrement(ref remaining) != 0) return;
                    var all = new List<Task>(tasks.Count);
                    foreach (Task<TResult> t in tasks) all.Add(t);
                    if (!FailFromAll(result, all))
                    {
                        var values = new TResult[tasks.Count];
                        for (int k = 0; k < values.Length; k++) values[k] = tasks[k].ResultOnSuccess;
                        result.TrySetResult(values);
                    }
                }, synchronous: true);
            return result;
        }

        private static void CompleteFromAll(Task result, IList<Task> tasks)
        {
            if (!FailFromAll(result, tasks)) result.TrySetResult();
        }

        // BCL: faulted beats canceled; the first fault is the one reported.
        private static bool FailFromAll(Task result, IList<Task> tasks)
        {
            Task? canceled = null;
            foreach (Task t in tasks)
            {
                if (t.IsFaulted) { result.TrySetException(t._error!); return true; }
                if (t.IsCanceled) canceled ??= t;
            }
            if (canceled != null) { result.TrySetCanceled(default, canceled._error); return true; }
            return false;
        }

        // ---- WaitAsync ----

        public Task WaitAsync(CancellationToken cancellationToken) => WaitAsync(Timeout.Infinite, cancellationToken);

        public Task WaitAsync(TimeSpan timeout) => WaitAsync(ValidateTimeout(timeout), CancellationToken.None);

        public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => WaitAsync(ValidateTimeout(timeout), cancellationToken);

        private Task WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (IsCompleted || (!cancellationToken.CanBeCanceled && millisecondsTimeout == Timeout.Infinite))
                return this;
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled(cancellationToken);
            if (millisecondsTimeout == 0)
                return FromException(new TimeoutException());

            var result = new Task();
            WireWaitAsync(result, millisecondsTimeout, cancellationToken, () => CopyOutcome(result));
            return result;
        }

        private protected void WireWaitAsync(Task result, int millisecondsTimeout, CancellationToken cancellationToken, Action copyOutcome)
        {
            AddContinuation(copyOutcome, synchronous: true);
            if (cancellationToken.CanBeCanceled)
                cancellationToken.Register(() => result.TrySetCanceled(cancellationToken));
            if (millisecondsTimeout != Timeout.Infinite)
                Delay(millisecondsTimeout).AddContinuation(() => result.TrySetException(new TimeoutException()), synchronous: true);
        }

        private void CopyOutcome(Task result)
        {
            if (IsCanceled) result.TrySetCanceled(default, _error);
            else if (IsFaulted) result.TrySetException(_error!);
            else result.TrySetResult();
        }

        private protected static int ValidateTimeout(TimeSpan timeout)
        {
            long totalMilliseconds = (long)timeout.TotalMilliseconds;
            if (totalMilliseconds < -1 || totalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must represent a value between -1 and Int32.MaxValue, inclusive.");
            return totalMilliseconds > int.MaxValue ? int.MaxValue : (int)totalMilliseconds;
        }

        // ---- the continuation lock ----
        // One word for every task: continuations are rare and short-held. A
        // futex mutex, as TaskPool's: no allocation, no static constructor —
        // Task is used before the heap's statics are materialized.
        private static int s_continuationLock;

        private static void ContinuationLock()
        {
            int c = Interlocked.CompareExchange(ref s_continuationLock, 1, 0);
            if (c == 0) return;
            if (c != 2) c = Interlocked.Exchange(ref s_continuationLock, 2);
            while (c != 0)
            {
                ThreadBackend.WaitWhile(ref s_continuationLock, 2, ThreadBackend.Infinite);
                c = Interlocked.Exchange(ref s_continuationLock, 2);
            }
        }

        private static void ContinuationUnlock()
        {
            if (Interlocked.Exchange(ref s_continuationLock, 0) == 2)
                ThreadBackend.WakeAll(ref s_continuationLock);
        }
    }

    public partial class Task<TResult>
    {
        public Task ContinueWith(Action<Task<TResult>> continuationAction)
            => ContinueWith(continuationAction, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task<TResult>> continuationAction, CancellationToken cancellationToken)
            => ContinueWith(continuationAction, cancellationToken, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task<TResult>> continuationAction, TaskScheduler scheduler)
            => ContinueWith(continuationAction, CancellationToken.None, TaskContinuationOptions.None, scheduler);

        public Task ContinueWith(Action<Task<TResult>> continuationAction, TaskContinuationOptions continuationOptions)
            => ContinueWith(continuationAction, CancellationToken.None, continuationOptions, TaskScheduler.Current);

        public Task ContinueWith(Action<Task<TResult>> continuationAction, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationAction, nameof(continuationAction));
            var next = new Task();
            Continue(next, continuationOptions, cancellationToken, scheduler, () => { continuationAction(this); next.TrySetResult(); });
            return next;
        }

        public Task ContinueWith(Action<Task<TResult>, object?> continuationAction, object? state)
            => ContinueWith(continuationAction, state, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task ContinueWith(Action<Task<TResult>, object?> continuationAction, object? state, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationAction, nameof(continuationAction));
            var next = new Task();
            next.SetAsyncState(state);
            Continue(next, continuationOptions, cancellationToken, scheduler, () => { continuationAction(this, state); next.TrySetResult(); });
            return next;
        }

        public Task<TNewResult> ContinueWith<TNewResult>(Func<Task<TResult>, TNewResult> continuationFunction)
            => ContinueWith(continuationFunction, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Current);

        public Task<TNewResult> ContinueWith<TNewResult>(Func<Task<TResult>, TNewResult> continuationFunction, TaskContinuationOptions continuationOptions)
            => ContinueWith(continuationFunction, CancellationToken.None, continuationOptions, TaskScheduler.Current);

        public Task<TNewResult> ContinueWith<TNewResult>(Func<Task<TResult>, TNewResult> continuationFunction, CancellationToken cancellationToken, TaskContinuationOptions continuationOptions, TaskScheduler scheduler)
        {
            ArgumentNullException.ThrowIfNull(continuationFunction, nameof(continuationFunction));
            var next = new Task<TNewResult>();
            Continue(next, continuationOptions, cancellationToken, scheduler, () => next.TrySetResult(continuationFunction(this)));
            return next;
        }

        public new Task<TResult> WaitAsync(CancellationToken cancellationToken) => WaitAsync(Timeout.Infinite, cancellationToken);

        public new Task<TResult> WaitAsync(TimeSpan timeout) => WaitAsync(ValidateTimeout(timeout), CancellationToken.None);

        public new Task<TResult> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => WaitAsync(ValidateTimeout(timeout), cancellationToken);

        private Task<TResult> WaitAsync(int millisecondsTimeout, CancellationToken cancellationToken)
        {
            if (IsCompleted || (!cancellationToken.CanBeCanceled && millisecondsTimeout == Timeout.Infinite))
                return this;
            if (cancellationToken.IsCancellationRequested)
                return FromCanceled<TResult>(cancellationToken);
            if (millisecondsTimeout == 0)
                return FromException<TResult>(new TimeoutException());

            var result = new Task<TResult>();
            WireWaitAsync(result, millisecondsTimeout, cancellationToken, () =>
            {
                if (IsCanceled) result.TrySetCanceled(default, Exception);
                else if (IsFaulted) result.TrySetException(Exception!);
                else result.TrySetResult(ResultOnSuccess);
            });
            return result;
        }
    }
}
