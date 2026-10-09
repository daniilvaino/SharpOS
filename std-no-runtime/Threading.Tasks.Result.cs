// Task with a result, and the completion surface ValueTask needs from Task.
//
// API ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Future.cs
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskCanceledException.cs
// The members and their meaning are the BCL's; the bodies sit on std's Task
// (Threading.Tasks.cs), which is a completion word and a pool, not the BCL's
// state machine of flags and continuation objects.
//
// Cuts:
//   - Wait/Result rethrow the task's exception as is, not wrapped in an
//     AggregateException — std's Task.Wait has always done so.
//   - TaskCanceledException.Task's CancellationToken: std's Task does not
//     record the token it was canceled with; FromCanceled keeps it on the
//     exception instead (OperationCanceledException.CancellationToken).
//   - Task.Yield ignores SynchronizationContext/TaskScheduler (there is no
//     scheduler to return to): the continuation goes to the pool, or runs
//     inline when the tier has no thread backend.

using System.Runtime.CompilerServices;

namespace System.Threading.Tasks
{
    public partial class Task
    {
        // Set together with _error when the task ended canceled rather than
        // faulted. IsFaulted excludes it.
        private bool _canceled;

        // Claimed by the Try* setters so two of them cannot both complete.
        private int _completionReserved;

        public bool IsCanceled => _canceled;

        public bool IsCompletedSuccessfully => _completed && _error == null;

        public static Task<TResult> FromResult<TResult>(TResult result) => new Task<TResult>(result);

        public static Task FromException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception, nameof(exception));
            var task = new Task();
            task.TrySetException(exception);
            return task;
        }

        public static Task<TResult> FromException<TResult>(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception, nameof(exception));
            var task = new Task<TResult>();
            task.TrySetException(exception);
            return task;
        }

        public static Task FromCanceled(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                throw new ArgumentOutOfRangeException(nameof(cancellationToken));
            var task = new Task();
            task.TrySetCanceled(cancellationToken);
            return task;
        }

        public static Task<TResult> FromCanceled<TResult>(CancellationToken cancellationToken)
        {
            if (!cancellationToken.IsCancellationRequested)
                throw new ArgumentOutOfRangeException(nameof(cancellationToken));
            var task = new Task<TResult>();
            task.TrySetCanceled(cancellationToken);
            return task;
        }

        /// <summary>An awaitable that always suspends and resumes on a pool thread.</summary>
        public static YieldAwaitable Yield() => default;

        private protected bool ReserveCompletion() =>
            !_completed && Interlocked.CompareExchange(ref _completionReserved, 1, 0) == 0;

        /// <summary>Completes successfully after <see cref="ReserveCompletion"/> said yes.</summary>
        private protected void CompleteReserved() => Complete();

        internal bool TrySetResult()
        {
            if (!ReserveCompletion()) return false;
            Complete();
            return true;
        }

        internal bool TrySetException(Exception exception)
        {
            if (!ReserveCompletion()) return false;
            _error = exception;
            Complete();
            return true;
        }

        internal bool TrySetCanceled(CancellationToken tokenToRecord) => TrySetCanceled(tokenToRecord, null);

        internal bool TrySetCanceled(CancellationToken tokenToRecord, object? cancellationException)
        {
            if (!ReserveCompletion()) return false;
            _canceled = true;
            _error = cancellationException is OperationCanceledException oce
                ? oce
                : new TaskCanceledException("A task was canceled.", null, tokenToRecord);
            Complete();
            return true;
        }

        /// <summary>
        /// Runs <paramref name="action"/> on a pool thread, or right here when
        /// the tier has no thread backend — the stand-in for
        /// ThreadPool.UnsafeQueueUserWorkItem in the ported await code.
        /// Unlike Task.Run, never drops the work: a continuation that is not
        /// run is an awaiter that never resumes.
        /// </summary>
        internal static void QueueContinuation(Action action)
        {
            var task = new Task();
            task._work = action;
            if (!TaskPool.Queue(task))
            {
                task._work = null;
                action();
            }
        }
    }

    /// <summary>A task that produces a value.</summary>
    public class Task<TResult> : Task
    {
        internal TResult? m_result;

        internal Task() { }

        internal Task(TResult result) : base(completed: true)
        {
            m_result = result;
        }

        /// <summary>Waits for the task and returns its value; rethrows its failure.</summary>
        public TResult Result
        {
            get
            {
                Wait();
                return m_result!;
            }
        }

        /// <summary>The value, for callers that have already checked success.</summary>
        internal TResult ResultOnSuccess => m_result!;

        internal bool TrySetResult(TResult result)
        {
            if (!ReserveCompletion()) return false;
            m_result = result;
            CompleteReserved();
            return true;
        }

        public new TaskAwaiter<TResult> GetAwaiter() => new TaskAwaiter<TResult>(this);

        public new ConfiguredTaskAwaitable<TResult> ConfigureAwait(bool continueOnCapturedContext) =>
            new ConfiguredTaskAwaitable<TResult>(this, continueOnCapturedContext);
    }

    public class TaskCanceledException : OperationCanceledException
    {
        private readonly Task? _canceledTask;

        public TaskCanceledException() : base("A task was canceled.") { }
        public TaskCanceledException(string? message) : base(message) { }
        public TaskCanceledException(string? message, Exception? innerException) : base(message, innerException) { }
        public TaskCanceledException(string? message, Exception? innerException, CancellationToken token)
            : base(message, innerException, token) { }

        // SharpOS cut: the task's own CancellationToken (Task.CancellationToken
        // is not recorded by std's Task) — None is passed instead.
        public TaskCanceledException(Task? task) : base("A task was canceled.", CancellationToken.None)
        {
            _canceledTask = task;
        }

        public Task? Task => _canceledTask;
    }
}
