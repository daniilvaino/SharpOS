// Async streams: the interfaces `await foreach` and `async IAsyncEnumerable<T>`
// are written against, and the ConfigureAwait/WithCancellation wrappers.
//
// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/IAsyncDisposable.cs
//   src/libraries/System.Private.CoreLib/src/System/Collections/Generic/IAsyncEnumerable.cs
//   src/libraries/System.Private.CoreLib/src/System/Collections/Generic/IAsyncEnumerator.cs
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/ConfiguredAsyncDisposable.cs
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/ConfiguredCancelableAsyncEnumerable.cs
//   src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/TaskAsyncEnumerableExtensions.cs
// Verbatim; doc comments trimmed. No cuts (the `allows ref struct`
// anti-constraints are NET9+/NET10+ only upstream and absent from 8.0).
//
// The iterator itself — the class the compiler generates for an
// `async IAsyncEnumerable<T>` method — rests on AsyncIteratorMethodBuilder
// (Runtime/CompilerServices/AsyncStreams.cs) and ManualResetValueTaskSourceCore
// (Threading.Tasks.Sources.cs).

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace System
{
    /// <summary>Provides a mechanism for releasing unmanaged resources asynchronously.</summary>
    public interface IAsyncDisposable
    {
        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or
        /// resetting unmanaged resources asynchronously.
        /// </summary>
        ValueTask DisposeAsync();
    }
}

namespace System.Collections.Generic
{
    /// <summary>Exposes an enumerator that provides asynchronous iteration over values of a specified type.</summary>
    /// <typeparam name="T">The type of values to enumerate.</typeparam>
    public interface IAsyncEnumerable<out T>
    {
        /// <summary>Returns an enumerator that iterates asynchronously through the collection.</summary>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> that may be used to cancel the asynchronous iteration.</param>
        /// <returns>An enumerator that can be used to iterate asynchronously through the collection.</returns>
        IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default);
    }

    /// <summary>Supports a simple asynchronous iteration over a generic collection.</summary>
    /// <typeparam name="T">The type of objects to enumerate.</typeparam>
    public interface IAsyncEnumerator<out T> : IAsyncDisposable
    {
        /// <summary>Advances the enumerator asynchronously to the next element of the collection.</summary>
        /// <returns>
        /// A <see cref="ValueTask{Boolean}"/> that will complete with a result of <c>true</c> if the enumerator
        /// was successfully advanced to the next element, or <c>false</c> if the enumerator has passed the end
        /// of the collection.
        /// </returns>
        ValueTask<bool> MoveNextAsync();

        /// <summary>Gets the element in the collection at the current position of the enumerator.</summary>
        T Current { get; }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Provides a type that can be used to configure how awaits on an <see cref="IAsyncDisposable"/> are performed.</summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ConfiguredAsyncDisposable
    {
        private readonly IAsyncDisposable _source;
        private readonly bool _continueOnCapturedContext;

        internal ConfiguredAsyncDisposable(IAsyncDisposable source, bool continueOnCapturedContext)
        {
            _source = source;
            _continueOnCapturedContext = continueOnCapturedContext;
        }

        /// <summary>Asynchronously releases the unmanaged resources used by the <see cref="ConfiguredAsyncDisposable" />.</summary>
        public ConfiguredValueTaskAwaitable DisposeAsync() =>
            // as with other "configured" awaitable-related type in CompilerServices, we don't null check to defend against
            // misuse like `default(ConfiguredAsyncDisposable).DisposeAsync()`, which will null ref by design.
            _source.DisposeAsync().ConfigureAwait(_continueOnCapturedContext);
    }

    /// <summary>Provides an awaitable async enumerable that enables cancelable iteration and configured awaits.</summary>
    [StructLayout(LayoutKind.Auto)]
    public readonly struct ConfiguredCancelableAsyncEnumerable<T>
    {
        private readonly IAsyncEnumerable<T> _enumerable;
        private readonly CancellationToken _cancellationToken;
        private readonly bool _continueOnCapturedContext;

        internal ConfiguredCancelableAsyncEnumerable(IAsyncEnumerable<T> enumerable, bool continueOnCapturedContext, CancellationToken cancellationToken)
        {
            _enumerable = enumerable;
            _continueOnCapturedContext = continueOnCapturedContext;
            _cancellationToken = cancellationToken;
        }

        /// <summary>Configures how awaits on the tasks returned from an async iteration will be performed.</summary>
        public ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait(bool continueOnCapturedContext) =>
            new ConfiguredCancelableAsyncEnumerable<T>(_enumerable, continueOnCapturedContext, _cancellationToken);

        /// <summary>Sets the <see cref="CancellationToken"/> to be passed to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/> when iterating.</summary>
        public ConfiguredCancelableAsyncEnumerable<T> WithCancellation(CancellationToken cancellationToken) =>
            new ConfiguredCancelableAsyncEnumerable<T>(_enumerable, _continueOnCapturedContext, cancellationToken);

        /// <summary>Returns an enumerator that iterates asynchronously through collections that enables cancelable iteration and configured awaits.</summary>
        public Enumerator GetAsyncEnumerator() =>
            // as with other "configured" awaitable-related type in CompilerServices, we don't null check to defend against
            // misuse like `default(ConfiguredCancelableAsyncEnumerable<T>).GetAsyncEnumerator()`, which will null ref by design.
            new Enumerator(_enumerable.GetAsyncEnumerator(_cancellationToken), _continueOnCapturedContext);

        /// <summary>Provides an awaitable async enumerator that enables cancelable iteration and configured awaits.</summary>
        [StructLayout(LayoutKind.Auto)]
        public readonly struct Enumerator
        {
            private readonly IAsyncEnumerator<T> _enumerator;
            private readonly bool _continueOnCapturedContext;

            internal Enumerator(IAsyncEnumerator<T> enumerator, bool continueOnCapturedContext)
            {
                _enumerator = enumerator;
                _continueOnCapturedContext = continueOnCapturedContext;
            }

            /// <summary>Advances the enumerator asynchronously to the next element of the collection.</summary>
            public ConfiguredValueTaskAwaitable<bool> MoveNextAsync() =>
                _enumerator.MoveNextAsync().ConfigureAwait(_continueOnCapturedContext);

            /// <summary>Gets the element in the collection at the current position of the enumerator.</summary>
            public T Current => _enumerator.Current;

            /// <summary>
            /// Performs application-defined tasks associated with freeing, releasing, or
            /// resetting unmanaged resources asynchronously.
            /// </summary>
            public ConfiguredValueTaskAwaitable DisposeAsync() =>
                _enumerator.DisposeAsync().ConfigureAwait(_continueOnCapturedContext);
        }
    }
}

namespace System.Threading.Tasks
{
    /// <summary>Provides a set of static methods for configuring <see cref="Task"/>-related behaviors on asynchronous enumerables and disposables.</summary>
    public static partial class TaskAsyncEnumerableExtensions
    {
        /// <summary>Configures how awaits on the tasks returned from an async disposable will be performed.</summary>
        public static ConfiguredAsyncDisposable ConfigureAwait(this IAsyncDisposable source, bool continueOnCapturedContext) =>
            new ConfiguredAsyncDisposable(source, continueOnCapturedContext);

        /// <summary>Configures how awaits on the tasks returned from an async iteration will be performed.</summary>
        public static ConfiguredCancelableAsyncEnumerable<T> ConfigureAwait<T>(
            this IAsyncEnumerable<T> source, bool continueOnCapturedContext)
            => new ConfiguredCancelableAsyncEnumerable<T>(source, continueOnCapturedContext, cancellationToken: default);

        /// <summary>Sets the <see cref="CancellationToken"/> to be passed to <see cref="IAsyncEnumerable{T}.GetAsyncEnumerator(CancellationToken)"/> when iterating.</summary>
        public static ConfiguredCancelableAsyncEnumerable<T> WithCancellation<T>(
            this IAsyncEnumerable<T> source, CancellationToken cancellationToken)
            => new ConfiguredCancelableAsyncEnumerable<T>(source, continueOnCapturedContext: true, cancellationToken);
    }
}
