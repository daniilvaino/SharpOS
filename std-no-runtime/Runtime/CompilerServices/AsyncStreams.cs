// The builders and attributes behind `async ValueTask`, `async IAsyncEnumerable`
// and `await Task.Yield()`.
//
// Ported from dotnet/runtime (MIT):
//   release/8.0 (v8.0.27), src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/:
//     AsyncIteratorMethodBuilder.cs, AsyncIteratorStateMachineAttribute.cs,
//     AsyncMethodBuilderAttribute.cs, StateMachineAttribute.cs,
//     EnumeratorCancellationAttribute.cs, YieldAwaitable.cs
//   release/3.0, same directory: AsyncValueTaskMethodBuilder.cs,
//     AsyncValueTaskMethodBuilderT.cs — the shape before pooled state-machine
//     boxes: a ValueTask builder that wraps the Task builder and hands out a
//     plain result when the method finished without suspending. The 8.0 body
//     drives AsyncTaskMethodBuilder<T>'s private box machinery (m_task as an
//     IAsyncStateMachineBox), which std's builders do not have.
//
// Cuts:
//   - AsyncMethodBuilderCore.Start's ExecutionContext/SynchronizationContext
//     save-and-restore around MoveNext: neither exists to restore here; Start
//     and MoveNext advance the machine directly, as std's Task builder does.
//   - AsyncIteratorMethodBuilder: m_task (Task<VoidTaskResult>, a box) →
//     std's AsyncTaskMethodBuilder; ObjectIdForDebugger and box clearing gone
//     with it. The builder holds one shared reference, like every std builder.
//   - YieldAwaitable: no SynchronizationContext/TaskScheduler to post back to,
//     no ETW correlation, no IStateMachineBoxAwareAwaiter. The continuation
//     goes to std's task pool, or runs inline when the tier has no threads.
//   - AsyncStateMachineAttribute is not added: it would be stamped on every
//     async method in every image, and nothing reads it.

using System.Threading.Tasks;

namespace System.Runtime.CompilerServices
{
    /// <summary>Represents a builder for asynchronous methods that return a <see cref="ValueTask"/>.</summary>
    public struct AsyncValueTaskMethodBuilder
    {
        /// <summary>The <see cref="AsyncTaskMethodBuilder"/> to which most operations are delegated.</summary>
        private AsyncTaskMethodBuilder _methodBuilder; // mutable struct; do not make it readonly
        /// <summary>true if completed synchronously and successfully; otherwise, false.</summary>
        private bool _haveResult;
        /// <summary>true if the builder should be used for setting/getting the result; otherwise, false.</summary>
        private bool _useBuilder;

        /// <summary>Creates an instance of the <see cref="AsyncValueTaskMethodBuilder"/> struct.</summary>
        public static AsyncValueTaskMethodBuilder Create() => default;

        /// <summary>Begins running the builder with the associated state machine.</summary>
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
            _methodBuilder.Start(ref stateMachine);

        /// <summary>Associates the builder with the specified state machine.</summary>
        public void SetStateMachine(IAsyncStateMachine stateMachine) => _methodBuilder.SetStateMachine(stateMachine);

        /// <summary>Marks the task as successfully completed.</summary>
        public void SetResult()
        {
            if (_useBuilder)
            {
                _methodBuilder.SetResult();
            }
            else
            {
                _haveResult = true;
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        public void SetException(Exception exception) => _methodBuilder.SetException(exception);

        /// <summary>Gets the task for this builder.</summary>
        public ValueTask Task
        {
            get
            {
                if (_haveResult)
                {
                    return default;
                }
                else
                {
                    _useBuilder = true;
                    return new ValueTask(_methodBuilder.Task);
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            _useBuilder = true;
            _methodBuilder.AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            _useBuilder = true;
            _methodBuilder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
        }
    }

    /// <summary>Represents a builder for asynchronous methods that returns a <see cref="ValueTask{TResult}"/>.</summary>
    /// <typeparam name="TResult">The type of the result.</typeparam>
    public struct AsyncValueTaskMethodBuilder<TResult>
    {
        /// <summary>The <see cref="AsyncTaskMethodBuilder{TResult}"/> to which most operations are delegated.</summary>
        private AsyncTaskMethodBuilder<TResult> _methodBuilder; // mutable struct; do not make it readonly
        /// <summary>The result for this builder, if it's completed before any awaits occur.</summary>
        private TResult _result;
        /// <summary>true if <see cref="_result"/> contains the synchronous result for the async method; otherwise, false.</summary>
        private bool _haveResult;
        /// <summary>true if the builder should be used for setting/getting the result; otherwise, false.</summary>
        private bool _useBuilder;

        /// <summary>Creates an instance of the <see cref="AsyncValueTaskMethodBuilder{TResult}"/> struct.</summary>
        public static AsyncValueTaskMethodBuilder<TResult> Create() => default;

        /// <summary>Begins running the builder with the associated state machine.</summary>
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
            _methodBuilder.Start(ref stateMachine);

        /// <summary>Associates the builder with the specified state machine.</summary>
        public void SetStateMachine(IAsyncStateMachine stateMachine) => _methodBuilder.SetStateMachine(stateMachine);

        /// <summary>Marks the task as successfully completed.</summary>
        public void SetResult(TResult result)
        {
            if (_useBuilder)
            {
                _methodBuilder.SetResult(result);
            }
            else
            {
                _result = result;
                _haveResult = true;
            }
        }

        /// <summary>Marks the task as failed and binds the specified exception to the task.</summary>
        public void SetException(Exception exception) => _methodBuilder.SetException(exception);

        /// <summary>Gets the task for this builder.</summary>
        public ValueTask<TResult> Task
        {
            get
            {
                if (_haveResult)
                {
                    return new ValueTask<TResult>(_result);
                }
                else
                {
                    _useBuilder = true;
                    return new ValueTask<TResult>(_methodBuilder.Task);
                }
            }
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            _useBuilder = true;
            _methodBuilder.AwaitOnCompleted(ref awaiter, ref stateMachine);
        }

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            _useBuilder = true;
            _methodBuilder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);
        }
    }

    /// <summary>Represents a builder for asynchronous iterators.</summary>
    public struct AsyncIteratorMethodBuilder
    {
        // SharpOS cut: m_task (Task<VoidTaskResult>, the BCL's state-machine box) → std's Task builder.
        private AsyncTaskMethodBuilder _methodBuilder; // mutable struct; do not make it readonly

        /// <summary>Creates an instance of the <see cref="AsyncIteratorMethodBuilder"/> struct.</summary>
        /// <returns>The initialized instance.</returns>
        public static AsyncIteratorMethodBuilder Create() => default;

        /// <summary>Invokes <see cref="IAsyncStateMachine.MoveNext"/> on the state machine while guarding the <see cref="ExecutionContext"/>.</summary>
        /// <typeparam name="TStateMachine">The type of the state machine.</typeparam>
        /// <param name="stateMachine">The state machine instance, passed by reference.</param>
        public void MoveNext<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine =>
            _methodBuilder.Start(ref stateMachine); // SharpOS cut: AsyncMethodBuilderCore.Start's context guard

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine =>
            _methodBuilder.AwaitOnCompleted(ref awaiter, ref stateMachine);

        /// <summary>Schedules the state machine to proceed to the next action when the specified awaiter completes.</summary>
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine =>
            _methodBuilder.AwaitUnsafeOnCompleted(ref awaiter, ref stateMachine);

        /// <summary>Marks iteration as being completed, whether successfully or otherwise.</summary>
        public void Complete()
        {
            // As the BCL: a task only exists if an await suspended; with none
            // there is nothing to complete (the BCL stores a cached completed
            // task instead, for the debugger).
            if (_methodBuilder.HasTask)
            {
                _methodBuilder.SetResult();
            }
        }
    }

    /// <summary>
    /// Indicates the type of the async method builder that should be used by a language compiler to
    /// build the attributed async method or to build the attributed type when used as the return type
    /// of an async method.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface | AttributeTargets.Delegate | AttributeTargets.Enum | AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncMethodBuilderAttribute : Attribute
    {
        /// <summary>Initializes the <see cref="AsyncMethodBuilderAttribute"/>.</summary>
        /// <param name="builderType">The <see cref="Type"/> of the associated builder.</param>
        public AsyncMethodBuilderAttribute(Type builderType) => BuilderType = builderType;

        /// <summary>Gets the <see cref="Type"/> of the associated builder.</summary>
        public Type BuilderType { get; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public class StateMachineAttribute : Attribute
    {
        public StateMachineAttribute(Type stateMachineType)
        {
            StateMachineType = stateMachineType;
        }

        public Type StateMachineType { get; }
    }

    /// <summary>Indicates whether a method is an asynchronous iterator.</summary>
    [AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
    public sealed class AsyncIteratorStateMachineAttribute : StateMachineAttribute
    {
        /// <summary>Initializes a new instance of the <see cref="AsyncIteratorStateMachineAttribute"/> class.</summary>
        /// <param name="stateMachineType">The type object for the underlying state machine type that's used to implement a state machine method.</param>
        public AsyncIteratorStateMachineAttribute(Type stateMachineType)
            : base(stateMachineType)
        {
        }
    }

    /// <summary>Allows users of async-enumerable methods to mark the parameter that should receive the cancellation token value from <see cref="System.Collections.Generic.IAsyncEnumerable{T}.GetAsyncEnumerator(System.Threading.CancellationToken)"/>.</summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class EnumeratorCancellationAttribute : Attribute
    {
        /// <summary>Initializes a new instance of the <see cref="EnumeratorCancellationAttribute"/> class.</summary>
        public EnumeratorCancellationAttribute()
        {
        }
    }

    // NOTE: YieldAwaitable currently has no state; while developers are encouraged to use Task.Yield() to produce one,
    // no validation is performed to ensure that the developer isn't doing "await new YieldAwaitable()".  Such validation
    // would require additional, useless state to be stored, and as this is a type in the CompilerServices namespace, and
    // as the above example isn't harmful, we take the cheaper approach of not validating anything.

    /// <summary>Provides an awaitable context for switching into a target environment.</summary>
    /// <remarks>This type is intended for compiler use only.</remarks>
    public readonly struct YieldAwaitable
    {
        /// <summary>Gets an awaiter for this <see cref="YieldAwaitable"/>.</summary>
        /// <returns>An awaiter for this awaitable.</returns>
        /// <remarks>This method is intended for compiler user rather than use directly in code.</remarks>
        public YieldAwaiter GetAwaiter() { return default; }

        /// <summary>Provides an awaiter that switches into a target environment.</summary>
        /// <remarks>This type is intended for compiler use only.</remarks>
        public readonly struct YieldAwaiter : ICriticalNotifyCompletion // SharpOS cut: IStateMachineBoxAwareAwaiter
        {
            /// <summary>Gets whether a yield is not required.</summary>
            /// <remarks>This property is intended for compiler user rather than use directly in code.</remarks>
            public bool IsCompleted => false;  // yielding is always required for YieldAwaiter, hence false

            /// <summary>Posts the <paramref name="continuation"/> back to the current context.</summary>
            /// <param name="continuation">The action to invoke asynchronously.</param>
            /// <exception cref="System.ArgumentNullException">The <paramref name="continuation"/> argument is null (Nothing in Visual Basic).</exception>
            public void OnCompleted(Action continuation)
            {
                QueueContinuation(continuation, flowContext: true);
            }

            /// <summary>Posts the <paramref name="continuation"/> back to the current context.</summary>
            /// <param name="continuation">The action to invoke asynchronously.</param>
            /// <exception cref="System.ArgumentNullException">The <paramref name="continuation"/> argument is null (Nothing in Visual Basic).</exception>
            public void UnsafeOnCompleted(Action continuation)
            {
                QueueContinuation(continuation, flowContext: false);
            }

            /// <summary>Posts the <paramref name="continuation"/> back to the current context.</summary>
            /// <param name="continuation">The action to invoke asynchronously.</param>
            /// <param name="flowContext">true to flow ExecutionContext; false if flowing is not required.</param>
            /// <exception cref="System.ArgumentNullException">The <paramref name="continuation"/> argument is null (Nothing in Visual Basic).</exception>
            private static void QueueContinuation(Action continuation, bool flowContext)
            {
                ArgumentNullException.ThrowIfNull(continuation, nameof(continuation));

                // SharpOS cut: ETW correlation, SynchronizationContext.Post and
                // custom TaskScheduler — the default-scheduler branch is the
                // only one, with std's task pool for ThreadPool.QueueUserWorkItem.
                System.Threading.Tasks.Task.QueueContinuation(continuation);
            }

            /// <summary>Ends the await operation.</summary>
            public void GetResult() { } // Nop. It exists purely because the compiler pattern demands it.
        }
    }
}
