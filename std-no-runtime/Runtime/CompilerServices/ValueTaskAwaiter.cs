// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/ValueTaskAwaiter.cs
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/ConfiguredValueTaskAwaitable.cs
//
// Cuts (marked `SharpOS cut:` in place):
//   - IStateMachineBoxAwareAwaiter and its AwaitUnsafeOnCompleted(box): a
//     fast path of the BCL's AsyncTaskMethodBuilder for its state-machine
//     boxes; std's builders resume through OnCompleted/UnsafeOnCompleted.
//   - Task.ConfigureAwait(bool) in the configured awaiters → Task.GetAwaiter():
//     std's awaiters capture no context, so both configurations behave alike.
//     The flag is still passed on to IValueTaskSource (UseSchedulingContext),
//     whose std implementation ignores it.
//   - Doc comments trimmed to their summaries.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace System.Runtime.CompilerServices
{
    /// <summary>Provides an awaiter for a <see cref="ValueTask"/>.</summary>
    public readonly struct ValueTaskAwaiter : ICriticalNotifyCompletion // SharpOS cut: IStateMachineBoxAwareAwaiter
    {
        /// <summary>Shim used to invoke an <see cref="Action"/> passed as the state argument to a <see cref="Action{Object}"/>.</summary>
        internal static readonly Action<object?> s_invokeActionDelegate = static state =>
        {
            if (state is Action action)
            {
                action();
            }
            else
            {
                ThrowHelper.ThrowUnexpectedStateForKnownCallback(state);
            }
        };

        /// <summary>The value being awaited.</summary>
        private readonly ValueTask _value;

        /// <summary>Initializes the awaiter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ValueTaskAwaiter(in ValueTask value) => _value = value;

        /// <summary>Gets whether the <see cref="ValueTask"/> has completed.</summary>
        public bool IsCompleted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value.IsCompleted;
        }

        /// <summary>Gets the result of the ValueTask.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetResult() => _value.ThrowIfCompletedUnsuccessfully();

        /// <summary>Schedules the continuation action for this ValueTask.</summary>
        public void OnCompleted(Action continuation)
        {
            object? obj = _value._obj;
            Debug.Assert(obj == null || obj is Task || obj is IValueTaskSource);

            if (obj is Task t)
            {
                t.GetAwaiter().OnCompleted(continuation);
            }
            else if (obj != null)
            {
                Unsafe.As<IValueTaskSource>(obj).OnCompleted(s_invokeActionDelegate, continuation, _value._token, ValueTaskSourceOnCompletedFlags.UseSchedulingContext | ValueTaskSourceOnCompletedFlags.FlowExecutionContext);
            }
            else
            {
                Task.CompletedTask.GetAwaiter().OnCompleted(continuation);
            }
        }

        /// <summary>Schedules the continuation action for this ValueTask.</summary>
        public void UnsafeOnCompleted(Action continuation)
        {
            object? obj = _value._obj;
            Debug.Assert(obj == null || obj is Task || obj is IValueTaskSource);

            if (obj is Task t)
            {
                t.GetAwaiter().UnsafeOnCompleted(continuation);
            }
            else if (obj != null)
            {
                Unsafe.As<IValueTaskSource>(obj).OnCompleted(s_invokeActionDelegate, continuation, _value._token, ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
            }
            else
            {
                Task.CompletedTask.GetAwaiter().UnsafeOnCompleted(continuation);
            }
        }
    }

    /// <summary>Provides an awaiter for a <see cref="ValueTask{TResult}"/>.</summary>
    public readonly struct ValueTaskAwaiter<TResult> : ICriticalNotifyCompletion // SharpOS cut: IStateMachineBoxAwareAwaiter
    {
        /// <summary>The value being awaited.</summary>
        private readonly ValueTask<TResult> _value;

        /// <summary>Initializes the awaiter.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ValueTaskAwaiter(in ValueTask<TResult> value) => _value = value;

        /// <summary>Gets whether the <see cref="ValueTask{TResult}"/> has completed.</summary>
        public bool IsCompleted
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _value.IsCompleted;
        }

        /// <summary>Gets the result of the ValueTask.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TResult GetResult() => _value.Result;

        /// <summary>Schedules the continuation action for this ValueTask.</summary>
        public void OnCompleted(Action continuation)
        {
            object? obj = _value._obj;
            Debug.Assert(obj == null || obj is Task<TResult> || obj is IValueTaskSource<TResult>);

            if (obj is Task<TResult> t)
            {
                t.GetAwaiter().OnCompleted(continuation);
            }
            else if (obj != null)
            {
                Unsafe.As<IValueTaskSource<TResult>>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token, ValueTaskSourceOnCompletedFlags.UseSchedulingContext | ValueTaskSourceOnCompletedFlags.FlowExecutionContext);
            }
            else
            {
                Task.CompletedTask.GetAwaiter().OnCompleted(continuation);
            }
        }

        /// <summary>Schedules the continuation action for this ValueTask.</summary>
        public void UnsafeOnCompleted(Action continuation)
        {
            object? obj = _value._obj;
            Debug.Assert(obj == null || obj is Task<TResult> || obj is IValueTaskSource<TResult>);

            if (obj is Task<TResult> t)
            {
                t.GetAwaiter().UnsafeOnCompleted(continuation);
            }
            else if (obj != null)
            {
                Unsafe.As<IValueTaskSource<TResult>>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token, ValueTaskSourceOnCompletedFlags.UseSchedulingContext);
            }
            else
            {
                Task.CompletedTask.GetAwaiter().UnsafeOnCompleted(continuation);
            }
        }
    }

    /// <summary>Provides an awaitable type that enables configured awaits on a <see cref="ValueTask"/>.</summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ConfiguredValueTaskAwaitable
    {
        /// <summary>The wrapped <see cref="Task"/>.</summary>
        private readonly ValueTask _value;

        /// <summary>Initializes the awaitable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConfiguredValueTaskAwaitable(in ValueTask value) => _value = value;

        /// <summary>Returns an awaiter for this <see cref="ConfiguredValueTaskAwaitable"/> instance.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConfiguredValueTaskAwaiter GetAwaiter() => new ConfiguredValueTaskAwaiter(in _value);

        /// <summary>Provides an awaiter for a <see cref="ConfiguredValueTaskAwaitable"/>.</summary>
        [StructLayout(LayoutKind.Auto)]
        public readonly struct ConfiguredValueTaskAwaiter : ICriticalNotifyCompletion // SharpOS cut: IStateMachineBoxAwareAwaiter
        {
            /// <summary>The value being awaited.</summary>
            private readonly ValueTask _value;

            /// <summary>Initializes the awaiter.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ConfiguredValueTaskAwaiter(in ValueTask value) => _value = value;

            /// <summary>Gets whether the <see cref="ConfiguredValueTaskAwaitable"/> has completed.</summary>
            public bool IsCompleted
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _value.IsCompleted;
            }

            /// <summary>Gets the result of the ValueTask.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void GetResult() => _value.ThrowIfCompletedUnsuccessfully();

            /// <summary>Schedules the continuation action for the <see cref="ConfiguredValueTaskAwaitable"/>.</summary>
            public void OnCompleted(Action continuation)
            {
                object? obj = _value._obj;
                Debug.Assert(obj == null || obj is Task || obj is IValueTaskSource);

                if (obj is Task t)
                {
                    t.GetAwaiter().OnCompleted(continuation); // SharpOS cut: t.ConfigureAwait(_value._continueOnCapturedContext)
                }
                else if (obj != null)
                {
                    Unsafe.As<IValueTaskSource>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token,
                        ValueTaskSourceOnCompletedFlags.FlowExecutionContext |
                            (_value._continueOnCapturedContext ? ValueTaskSourceOnCompletedFlags.UseSchedulingContext : ValueTaskSourceOnCompletedFlags.None));
                }
                else
                {
                    Task.CompletedTask.GetAwaiter().OnCompleted(continuation); // SharpOS cut: ConfigureAwait
                }
            }

            /// <summary>Schedules the continuation action for the <see cref="ConfiguredValueTaskAwaitable"/>.</summary>
            public void UnsafeOnCompleted(Action continuation)
            {
                object? obj = _value._obj;
                Debug.Assert(obj == null || obj is Task || obj is IValueTaskSource);

                if (obj is Task t)
                {
                    t.GetAwaiter().UnsafeOnCompleted(continuation); // SharpOS cut: t.ConfigureAwait(_value._continueOnCapturedContext)
                }
                else if (obj != null)
                {
                    Unsafe.As<IValueTaskSource>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token,
                        _value._continueOnCapturedContext ? ValueTaskSourceOnCompletedFlags.UseSchedulingContext : ValueTaskSourceOnCompletedFlags.None);
                }
                else
                {
                    Task.CompletedTask.GetAwaiter().UnsafeOnCompleted(continuation); // SharpOS cut: ConfigureAwait
                }
            }
        }
    }

    /// <summary>Provides an awaitable type that enables configured awaits on a <see cref="ValueTask{TResult}"/>.</summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ConfiguredValueTaskAwaitable<TResult>
    {
        /// <summary>The wrapped <see cref="ValueTask{TResult}"/>.</summary>
        private readonly ValueTask<TResult> _value;

        /// <summary>Initializes the awaitable.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ConfiguredValueTaskAwaitable(in ValueTask<TResult> value) => _value = value;

        /// <summary>Returns an awaiter for this <see cref="ConfiguredValueTaskAwaitable{TResult}"/> instance.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ConfiguredValueTaskAwaiter GetAwaiter() => new ConfiguredValueTaskAwaiter(in _value);

        /// <summary>Provides an awaiter for a <see cref="ConfiguredValueTaskAwaitable{TResult}"/>.</summary>
        [StructLayout(LayoutKind.Auto)]
        public readonly struct ConfiguredValueTaskAwaiter : ICriticalNotifyCompletion // SharpOS cut: IStateMachineBoxAwareAwaiter
        {
            /// <summary>The value being awaited.</summary>
            private readonly ValueTask<TResult> _value;

            /// <summary>Initializes the awaiter.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal ConfiguredValueTaskAwaiter(in ValueTask<TResult> value) => _value = value;

            /// <summary>Gets whether the <see cref="ConfiguredValueTaskAwaitable{TResult}"/> has completed.</summary>
            public bool IsCompleted
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => _value.IsCompleted;
            }

            /// <summary>Gets the result of the ValueTask.</summary>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public TResult GetResult() => _value.Result;

            /// <summary>Schedules the continuation action for the <see cref="ConfiguredValueTaskAwaitable{TResult}"/>.</summary>
            public void OnCompleted(Action continuation)
            {
                object? obj = _value._obj;
                Debug.Assert(obj == null || obj is Task<TResult> || obj is IValueTaskSource<TResult>);

                if (obj is Task<TResult> t)
                {
                    t.GetAwaiter().OnCompleted(continuation); // SharpOS cut: t.ConfigureAwait(_value._continueOnCapturedContext)
                }
                else if (obj != null)
                {
                    Unsafe.As<IValueTaskSource<TResult>>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token,
                        ValueTaskSourceOnCompletedFlags.FlowExecutionContext |
                            (_value._continueOnCapturedContext ? ValueTaskSourceOnCompletedFlags.UseSchedulingContext : ValueTaskSourceOnCompletedFlags.None));
                }
                else
                {
                    Task.CompletedTask.GetAwaiter().OnCompleted(continuation); // SharpOS cut: ConfigureAwait
                }
            }

            /// <summary>Schedules the continuation action for the <see cref="ConfiguredValueTaskAwaitable{TResult}"/>.</summary>
            public void UnsafeOnCompleted(Action continuation)
            {
                object? obj = _value._obj;
                Debug.Assert(obj == null || obj is Task<TResult> || obj is IValueTaskSource<TResult>);

                if (obj is Task<TResult> t)
                {
                    t.GetAwaiter().UnsafeOnCompleted(continuation); // SharpOS cut: t.ConfigureAwait(_value._continueOnCapturedContext)
                }
                else if (obj != null)
                {
                    Unsafe.As<IValueTaskSource<TResult>>(obj).OnCompleted(ValueTaskAwaiter.s_invokeActionDelegate, continuation, _value._token,
                        _value._continueOnCapturedContext ? ValueTaskSourceOnCompletedFlags.UseSchedulingContext : ValueTaskSourceOnCompletedFlags.None);
                }
                else
                {
                    Task.CompletedTask.GetAwaiter().UnsafeOnCompleted(continuation); // SharpOS cut: ConfigureAwait
                }
            }
        }
    }
}
