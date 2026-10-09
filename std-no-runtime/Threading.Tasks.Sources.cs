// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Sources/IValueTaskSource.cs
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Sources/ManualResetValueTaskSourceCore.cs
//
// IValueTaskSource is verbatim. ManualResetValueTaskSourceCore keeps its
// fields, version/sentinel protocol and completion logic; what goes is the
// context it would capture, which this environment does not have to flow:
//
// Cuts (each marked `SharpOS cut:` below):
//   - ExecutionContext capture/flow (FlowExecutionContext is accepted and
//     ignored): there is no ExecutionContext.
//   - SynchronizationContext / TaskScheduler capture (UseSchedulingContext is
//     accepted and ignored): std's awaiters capture neither, and continuations
//     run inline or on the pool, as everywhere else in std's async support.
//     With them go CapturedSchedulerAndExecutionContext and the
//     ScheduleCapturedContext / InvokeContinuationWithContext helpers.
//   - ThreadPool.UnsafeQueueUserWorkItem → Task.QueueContinuation (std's task
//     pool; inline when the tier has no threads).

using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace System.Threading.Tasks.Sources
{
    /// <summary>
    /// Flags passed from <see cref="ValueTask"/> and <see cref="ValueTask{TResult}"/> to
    /// <see cref="IValueTaskSource.OnCompleted"/> and <see cref="IValueTaskSource{TResult}.OnCompleted"/>
    /// to control behavior.
    /// </summary>
    [Flags]
    public enum ValueTaskSourceOnCompletedFlags
    {
        /// <summary>
        /// No requirements are placed on how the continuation is invoked.
        /// </summary>
        None,
        /// <summary>
        /// Set if OnCompleted should capture the current scheduling context (e.g. SynchronizationContext)
        /// and use it when queueing the continuation for execution.  If this is not set, the implementation
        /// may choose to execute the continuation in an arbitrary location.
        /// </summary>
        UseSchedulingContext = 0x1,
        /// <summary>
        /// Set if OnCompleted should capture the current ExecutionContext and use it to run the continuation.
        /// </summary>
        FlowExecutionContext = 0x2,
    }

    /// <summary>Indicates the status of an <see cref="IValueTaskSource"/> or <see cref="IValueTaskSource{TResult}"/>.</summary>
    public enum ValueTaskSourceStatus
    {
        /// <summary>The operation has not yet completed.</summary>
        Pending = 0,
        /// <summary>The operation completed successfully.</summary>
        Succeeded = 1,
        /// <summary>The operation completed with an error.</summary>
        Faulted = 2,
        /// <summary>The operation completed due to cancellation.</summary>
        Canceled = 3
    }

    /// <summary>Represents an object that can be wrapped by a <see cref="ValueTask"/>.</summary>
    public interface IValueTaskSource
    {
        /// <summary>Gets the status of the current operation.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        ValueTaskSourceStatus GetStatus(short token);

        /// <summary>Schedules the continuation action for this <see cref="IValueTaskSource"/>.</summary>
        /// <param name="continuation">The continuation to invoke when the operation has completed.</param>
        /// <param name="state">The state object to pass to <paramref name="continuation"/> when it's invoked.</param>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        /// <param name="flags">The flags describing the behavior of the continuation.</param>
        void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags);

        /// <summary>Gets the result of the <see cref="IValueTaskSource"/>.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        void GetResult(short token);
    }

    /// <summary>Represents an object that can be wrapped by a <see cref="ValueTask{TResult}"/>.</summary>
    /// <typeparam name="TResult">Specifies the type of data returned from the object.</typeparam>
    public interface IValueTaskSource<out TResult>
    {
        /// <summary>Gets the status of the current operation.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        ValueTaskSourceStatus GetStatus(short token);

        /// <summary>Schedules the continuation action for this <see cref="IValueTaskSource{TResult}"/>.</summary>
        /// <param name="continuation">The continuation to invoke when the operation has completed.</param>
        /// <param name="state">The state object to pass to <paramref name="continuation"/> when it's invoked.</param>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        /// <param name="flags">The flags describing the behavior of the continuation.</param>
        void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags);

        /// <summary>Gets the result of the <see cref="IValueTaskSource{TResult}"/>.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        TResult GetResult(short token);
    }

    /// <summary>Provides the core logic for implementing a manual-reset <see cref="IValueTaskSource"/> or <see cref="IValueTaskSource{TResult}"/>.</summary>
    /// <typeparam name="TResult">Specifies the type of results of the operation represented by this instance.</typeparam>
    [StructLayout(LayoutKind.Auto)]
    public struct ManualResetValueTaskSourceCore<TResult>
    {
        /// <summary>
        /// The callback to invoke when the operation completes if <see cref="OnCompleted"/> was called before the operation completed,
        /// or <see cref="ManualResetValueTaskSourceCoreShared.s_sentinel"/> if the operation completed before a callback was supplied,
        /// or null if a callback hasn't yet been provided and the operation hasn't yet completed.
        /// </summary>
        private Action<object?>? _continuation;
        /// <summary>State to pass to <see cref="_continuation"/>.</summary>
        private object? _continuationState;
        // SharpOS cut: _capturedContext (ExecutionContext / SynchronizationContext / TaskScheduler) — nothing to capture.
        /// <summary>The exception with which the operation failed, or null if it hasn't yet completed or completed successfully.</summary>
        private ExceptionDispatchInfo? _error;
        /// <summary>The result with which the operation succeeded, or the default value if it hasn't yet completed or failed.</summary>
        private TResult? _result;
        /// <summary>The current version of this value, used to help prevent misuse.</summary>
        private short _version;
        /// <summary>Whether the current operation has completed.</summary>
        private bool _completed;
        /// <summary>Whether to force continuations to run asynchronously.</summary>
        private bool _runContinuationsAsynchronously;

        /// <summary>Gets or sets whether to force continuations to run asynchronously.</summary>
        /// <remarks>Continuations may run asynchronously if this is false, but they'll never run synchronously if this is true.</remarks>
        public bool RunContinuationsAsynchronously
        {
            get => _runContinuationsAsynchronously;
            set => _runContinuationsAsynchronously = value;
        }

        /// <summary>Resets to prepare for the next operation.</summary>
        public void Reset()
        {
            // Reset/update state for the next use/await of this instance.
            _version++;
            _continuation = null;
            _continuationState = null;
            _error = null;
            _result = default;
            _completed = false;
        }

        /// <summary>Completes with a successful result.</summary>
        /// <param name="result">The result.</param>
        public void SetResult(TResult result)
        {
            _result = result;
            SignalCompletion();
        }

        /// <summary>Completes with an error.</summary>
        /// <param name="error">The exception.</param>
        public void SetException(Exception error)
        {
            _error = ExceptionDispatchInfo.Capture(error);
            SignalCompletion();
        }

        /// <summary>Gets the operation version.</summary>
        public short Version => _version;

        /// <summary>Gets the status of the operation.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        public ValueTaskSourceStatus GetStatus(short token)
        {
            ValidateToken(token);
            return
                Volatile.Read(ref _continuation) is null || !_completed ? ValueTaskSourceStatus.Pending :
                _error is null ? ValueTaskSourceStatus.Succeeded :
                _error.SourceException is OperationCanceledException ? ValueTaskSourceStatus.Canceled :
                ValueTaskSourceStatus.Faulted;
        }

        /// <summary>Gets the result of the operation.</summary>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        [StackTraceHidden]
        public TResult GetResult(short token)
        {
            if (token != _version || !_completed || _error is not null)
            {
                ThrowForFailedGetResult();
            }

            return _result!;
        }

        /// <summary>Throws an exception in response to a failed <see cref="GetResult"/>.</summary>
        [StackTraceHidden]
        private void ThrowForFailedGetResult()
        {
            _error?.Throw();
            throw new InvalidOperationException(); // not using ThrowHelper.ThrowInvalidOperationException so that the JIT sees ThrowForFailedGetResult as always throwing
        }

        /// <summary>Schedules the continuation action for this operation.</summary>
        /// <param name="continuation">The continuation to invoke when the operation has completed.</param>
        /// <param name="state">The state object to pass to <paramref name="continuation"/> when it's invoked.</param>
        /// <param name="token">Opaque value that was provided to the <see cref="ValueTask"/>'s constructor.</param>
        /// <param name="flags">The flags describing the behavior of the continuation.</param>
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            if (continuation is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.continuation);
            }
            ValidateToken(token);

            // SharpOS cut: FlowExecutionContext / UseSchedulingContext capture — see the file header.

            // We need to set the continuation state before we swap in the delegate, so that
            // if there's a race between this and SetResult/Exception and SetResult/Exception
            // sees the _continuation as non-null, it'll be able to invoke it with the state
            // stored here.  However, this also means that if this is used incorrectly (e.g.
            // awaited twice concurrently), _continuationState might get erroneously overwritten.
            // To minimize the chances of that, we check preemptively whether _continuation
            // is already set to something other than the completion sentinel.
            object? storedContinuation = _continuation;
            if (storedContinuation is null)
            {
                _continuationState = state;
                storedContinuation = Interlocked.CompareExchange(ref _continuation, continuation, null);
                if (storedContinuation is null)
                {
                    // Operation hadn't already completed, so we're done. The continuation will be
                    // invoked when SetResult/Exception is called at some later point.
                    return;
                }
            }

            // Operation already completed, so we need to queue the supplied callback.
            // At this point the storedContinuation should be the sentinal; if it's not, the instance was misused.
            Debug.Assert(storedContinuation is not null, $"{nameof(storedContinuation)} is null");
            if (!ReferenceEquals(storedContinuation, ManualResetValueTaskSourceCoreShared.s_sentinel))
            {
                ThrowHelper.ThrowInvalidOperationException();
            }

            // SharpOS cut: the captured-context switch; always the `case null` branch.
            ManualResetValueTaskSourceCoreShared.QueueUserWorkItem(continuation, state);
        }

        /// <summary>Ensures that the specified token matches the current version.</summary>
        /// <param name="token">The token supplied by <see cref="ValueTask"/>.</param>
        private void ValidateToken(short token)
        {
            if (token != _version)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }
        }

        /// <summary>Signals that the operation has completed.  Invoked after the result or error has been set.</summary>
        private void SignalCompletion()
        {
            if (_completed)
            {
                ThrowHelper.ThrowInvalidOperationException();
            }
            _completed = true;

            Action<object?>? continuation =
                Volatile.Read(ref _continuation) ??
                Interlocked.CompareExchange(ref _continuation, ManualResetValueTaskSourceCoreShared.s_sentinel, null);

            if (continuation is not null)
            {
                Debug.Assert(continuation is not null, $"{nameof(continuation)} is null");

                // SharpOS cut: captured-context branches; only `context is null` remains.
                if (_runContinuationsAsynchronously)
                {
                    ManualResetValueTaskSourceCoreShared.QueueUserWorkItem(continuation, _continuationState);
                }
                else
                {
                    continuation(_continuationState);
                }
            }
        }
    }

    internal static class ManualResetValueTaskSourceCoreShared // separated out of generic to avoid unnecessary duplication
    {
        internal static readonly Action<object?> s_sentinel = CompletionSentinel;

        private static void CompletionSentinel(object? _) // named method to aid debugging
        {
            Debug.Fail("The sentinel delegate should never be invoked.");
            ThrowHelper.ThrowInvalidOperationException();
        }

        // SharpOS cut: ScheduleCapturedContext / InvokeContinuationWithContext.
        // Stand-in for ThreadPool.UnsafeQueueUserWorkItem(continuation, state, preferLocal: true).
        internal static void QueueUserWorkItem(Action<object?> continuation, object? state) =>
            Task.QueueContinuation(() => continuation(state));
    }
}
