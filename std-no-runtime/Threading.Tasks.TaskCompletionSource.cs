// TaskCompletionSource and TaskCompletionSource<TResult>.
//
// API ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskCompletionSource.cs
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskCompletionSource_T.cs
// The members and their meaning are the BCL's; the bodies sit on std's Task
// completion (Threading.Tasks.Result.cs: TrySetResult/TrySetException/
// TrySetCanceled), as ValueTask's do.
//
// Cuts:
//   - TaskCreationOptions: only RunContinuationsAsynchronously has an effect
//     (Threading.Tasks.Continuations.cs); AttachedToParent is accepted.
//   - SetException(IEnumerable<Exception>): the BCL keeps them all and
//     wraps them in an AggregateException; std's Task holds one exception
//     and rethrows it unwrapped (Threading.Tasks.Result.cs), and std has no
//     AggregateException — the first one is the task's.

using System.Collections.Generic;

namespace System.Threading.Tasks
{
    public class TaskCompletionSource
    {
        private readonly Task _task = new Task();

        public TaskCompletionSource() { }

        public TaskCompletionSource(TaskCreationOptions creationOptions) : this(null, creationOptions) { }

        public TaskCompletionSource(object? state) : this(state, TaskCreationOptions.None) { }

        public TaskCompletionSource(object? state, TaskCreationOptions creationOptions)
        {
            TaskCompletionSourceHelpers.ValidateOptions(creationOptions);
            _task.SetAsyncState(state);
            _task._runContinuationsAsynchronously = (creationOptions & TaskCreationOptions.RunContinuationsAsynchronously) != 0;
        }

        public Task Task => _task;

        public void SetException(Exception exception)
        {
            if (!TrySetException(exception))
                ThrowAlreadyCompleted();
        }

        public void SetException(IEnumerable<Exception> exceptions)
        {
            if (!TrySetException(exceptions))
                ThrowAlreadyCompleted();
        }

        public bool TrySetException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception, nameof(exception));
            return _task.TrySetException(exception);
        }

        public bool TrySetException(IEnumerable<Exception> exceptions) =>
            _task.TrySetException(TaskCompletionSourceHelpers.OneException(exceptions));

        public void SetResult()
        {
            if (!TrySetResult())
                ThrowAlreadyCompleted();
        }

        public bool TrySetResult() => _task.TrySetResult();

        public void SetCanceled() => SetCanceled(default);

        public void SetCanceled(CancellationToken cancellationToken)
        {
            if (!TrySetCanceled(cancellationToken))
                ThrowAlreadyCompleted();
        }

        public bool TrySetCanceled() => TrySetCanceled(default);

        public bool TrySetCanceled(CancellationToken cancellationToken) =>
            _task.TrySetCanceled(cancellationToken);

        private static void ThrowAlreadyCompleted() =>
            throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed.");
    }

    public class TaskCompletionSource<TResult>
    {
        private readonly Task<TResult> _task = new Task<TResult>();

        public TaskCompletionSource() { }

        public TaskCompletionSource(TaskCreationOptions creationOptions) : this(null, creationOptions) { }

        public TaskCompletionSource(object? state) : this(state, TaskCreationOptions.None) { }

        public TaskCompletionSource(object? state, TaskCreationOptions creationOptions)
        {
            TaskCompletionSourceHelpers.ValidateOptions(creationOptions);
            _task.SetAsyncState(state);
            _task._runContinuationsAsynchronously = (creationOptions & TaskCreationOptions.RunContinuationsAsynchronously) != 0;
        }

        public Task<TResult> Task => _task;

        public void SetException(Exception exception)
        {
            if (!TrySetException(exception))
                ThrowAlreadyCompleted();
        }

        public void SetException(IEnumerable<Exception> exceptions)
        {
            if (!TrySetException(exceptions))
                ThrowAlreadyCompleted();
        }

        public bool TrySetException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception, nameof(exception));
            return _task.TrySetException(exception);
        }

        public bool TrySetException(IEnumerable<Exception> exceptions) =>
            _task.TrySetException(TaskCompletionSourceHelpers.OneException(exceptions));

        public void SetResult(TResult result)
        {
            if (!TrySetResult(result))
                ThrowAlreadyCompleted();
        }

        public bool TrySetResult(TResult result) => _task.TrySetResult(result);

        public void SetCanceled() => SetCanceled(default);

        public void SetCanceled(CancellationToken cancellationToken)
        {
            if (!TrySetCanceled(cancellationToken))
                ThrowAlreadyCompleted();
        }

        public bool TrySetCanceled() => TrySetCanceled(default);

        public bool TrySetCanceled(CancellationToken cancellationToken) =>
            _task.TrySetCanceled(cancellationToken);

        private static void ThrowAlreadyCompleted() =>
            throw new InvalidOperationException("An attempt was made to transition a task to a final state when it had already completed.");
    }

    internal static class TaskCompletionSourceHelpers
    {
        // BCL: a completion source's task takes only these two.
        internal static void ValidateOptions(TaskCreationOptions creationOptions)
        {
            if ((creationOptions & ~(TaskCreationOptions.AttachedToParent | TaskCreationOptions.RunContinuationsAsynchronously)) != 0)
                throw new ArgumentOutOfRangeException(nameof(creationOptions));
        }

        // The BCL: ArgumentException for an empty list or a null element.
        internal static Exception OneException(IEnumerable<Exception> exceptions)
        {
            ArgumentNullException.ThrowIfNull(exceptions, nameof(exceptions));
            var list = new List<Exception>();
            foreach (Exception e in exceptions)
            {
                if (e == null) throw new ArgumentException("The exceptions collection included at least one null element.", nameof(exceptions));
                list.Add(e);
            }
            if (list.Count == 0) throw new ArgumentException("The exceptions collection was empty.", nameof(exceptions));
            return list[0];
        }
    }
}
