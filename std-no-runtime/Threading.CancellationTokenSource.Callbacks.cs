// Cancellation callbacks: CancellationToken.Register / UnsafeRegister and what
// Cancel does with them.
//
// The public surface is the BCL's (dotnet/runtime release/8.0, MIT,
// src/libraries/System.Private.CoreLib/src/System/Threading/CancellationToken.cs
// and CancellationTokenSource.cs): the Register overloads with their
// already-cancelled and cannot-be-canceled shortcuts, LIFO execution on
// Cancel, a CallbackNode per registration and a Registrations list per source
// that CancellationTokenRegistration (ported verbatim alongside) talks to.
//
// The machinery is written smaller than upstream's: one lock-protected doubly
// linked list instead of the per-partition lock-free lists and the node free
// list; no ExecutionContext capture (std has none — Register and
// UnsafeRegister behave the same) and no SynchronizationContext posting
// (useSynchronizationContext is accepted and ignored). Exceptions thrown by
// callbacks: all callbacks still run and the first exception is rethrown
// (upstream aggregates them into an AggregateException, which std lacks;
// with throwOnFirstException the first one propagates at once, as upstream).

namespace System.Threading
{
    public sealed partial class CancellationTokenSource
    {
        // Created on the first registration.
        private volatile Registrations? _registrations;

        // Set once every callback has run after Cancel.
        private volatile bool _cancellationCompleted;

        // The registrations made on the parents of a linked source, so its own
        // callbacks run when a parent is cancelled.
        private CancellationTokenRegistration[]? _linkedRegistrations;

        internal bool IsCancellationCompleted => _cancellationCompleted;

        internal CancellationTokenRegistration Register(Action<object?, CancellationToken> callback, object? stateForCallback)
        {
            if (!IsCancellationRequested)
            {
                Registrations? registrations = _registrations;
                if (registrations == null)
                {
                    registrations = new Registrations(this);
                    registrations = Interlocked.CompareExchange(ref _registrations, registrations, null) ?? registrations;
                }

                HookLinkedParents();

                CallbackNode node = new CallbackNode(registrations) { Callback = callback, CallbackState = stateForCallback };
                long id;
                lock (registrations)
                {
                    id = ++registrations.NextAvailableId;
                    node.Id = id;
                    node.Next = registrations.Callbacks;
                    if (node.Next != null) node.Next.Prev = node;
                    registrations.Callbacks = node;
                }

                // As upstream: if cancellation raced in, take the callback back and run it here.
                if (!IsCancellationRequested || !registrations.Unregister(id, node))
                {
                    return new CancellationTokenRegistration(id, node);
                }
            }

            // Already cancelled: run the callback synchronously, as upstream.
            callback(stateForCallback, new CancellationToken(this));
            return default;
        }

        private void HookLinkedParents()
        {
            CancellationToken[]? linked = _linkedTokens;
            if (linked == null || _linkedRegistrations != null) return;
            var hooks = new CancellationTokenRegistration[linked.Length];
            for (int i = 0; i < linked.Length; i++)
                hooks[i] = linked[i].UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), this);
            if (Interlocked.CompareExchange(ref _linkedRegistrations, hooks, null) != null)
            {
                for (int i = 0; i < hooks.Length; i++) hooks[i].Dispose();
            }
        }

        private void UnhookLinkedParents()
        {
            CancellationTokenRegistration[]? hooks = Interlocked.Exchange(ref _linkedRegistrations, null);
            if (hooks == null) return;
            for (int i = 0; i < hooks.Length; i++) hooks[i].Dispose();
        }

        private void ExecuteCallbackHandlers(bool throwOnFirstException)
        {
            Registrations? registrations = Interlocked.Exchange(ref _registrations, null);
            if (registrations == null)
            {
                _cancellationCompleted = true;
                return;
            }

            registrations.ThreadIDExecutingCallbacks = Environment.CurrentManagedThreadId;
            Exception? first = null;
            try
            {
                while (true)
                {
                    CallbackNode? node;
                    lock (registrations)
                    {
                        node = registrations.Callbacks;
                        if (node == null) break;
                        registrations.Callbacks = node.Next;
                        if (node.Next != null) node.Next.Prev = null;
                        node.Next = null;
                        registrations.ExecutingCallbackId = node.Id;
                        node.Id = 0;
                    }

                    try
                    {
                        node.Callback!(node.CallbackState, new CancellationToken(this));
                    }
                    catch (Exception e) when (!throwOnFirstException)
                    {
                        first ??= e;
                    }
                }
            }
            finally
            {
                registrations.ExecutingCallbackId = 0;
                _cancellationCompleted = true;
            }

            if (first != null)
                throw first; // SharpOS: upstream throws new AggregateException(exceptions)
        }

        internal sealed class CallbackNode
        {
            public readonly Registrations Registrations;
            public CallbackNode? Prev;
            public CallbackNode? Next;
            public long Id;
            public Action<object?, CancellationToken>? Callback;
            public object? CallbackState;

            public CallbackNode(Registrations registrations) => Registrations = registrations;
        }

        internal sealed class Registrations
        {
            public readonly CancellationTokenSource Source;
            public CallbackNode? Callbacks;
            public long NextAvailableId;
            public volatile int ThreadIDExecutingCallbacks = -1;
            public long ExecutingCallbackId;

            public Registrations(CancellationTokenSource source) => Source = source;

            /// <summary>Removes the node if it is still registered under this id.</summary>
            public bool Unregister(long id, CallbackNode node)
            {
                lock (this)
                {
                    if (node.Id != id) return false; // already ran, running, or unregistered
                    if (node.Prev != null) node.Prev.Next = node.Next;
                    else if (Callbacks == node) Callbacks = node.Next;
                    else return false;
                    if (node.Next != null) node.Next.Prev = node.Prev;
                    node.Prev = node.Next = null;
                    node.Id = 0;
                    node.Callback = null;
                    node.CallbackState = null;
                    return true;
                }
            }

            /// <summary>Waits while the callback registered under <paramref name="id"/> is running.</summary>
            public void WaitForCallbackToComplete(long id)
            {
                SpinWait sw = default;
                while (Interlocked.Read(ref ExecutingCallbackId) == id)
                {
                    sw.SpinOnce();
                }
            }

            // SharpOS: upstream returns a task completed by the executing thread; here the wait is synchronous.
            public System.Threading.Tasks.ValueTask WaitForCallbackToCompleteAsync(long id)
            {
                WaitForCallbackToComplete(id);
                return default;
            }
        }
    }

    public readonly partial struct CancellationToken
    {
        public CancellationTokenRegistration Register(Action callback) =>
            Register(callback, useSynchronizationContext: false);

        public CancellationTokenRegistration Register(Action callback, bool useSynchronizationContext) =>
            Register(
                (Action<object?>)(static obj => ((Action)obj!)()),
                callback ?? throw new ArgumentNullException(nameof(callback)),
                useSynchronizationContext);

        public CancellationTokenRegistration Register(Action<object?> callback, object? state) =>
            Register(callback, state, useSynchronizationContext: false);

        public CancellationTokenRegistration Register(Action<object?, CancellationToken> callback, object? state)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            CancellationTokenSource? source = _source;
            return source != null ? source.Register(callback, state) : default;
        }

        // SharpOS: useSynchronizationContext is accepted and ignored — callbacks run on the cancelling thread.
        public CancellationTokenRegistration Register(Action<object?> callback, object? state, bool useSynchronizationContext)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            CancellationTokenSource? source = _source;
            return source != null
                ? source.Register(static (s, _) => { var t = (Tuple<Action<object?>, object?>)s!; t.Item1(t.Item2); }, new Tuple<Action<object?>, object?>(callback, state))
                : default;
        }

        // SharpOS: there is no ExecutionContext to flow, so UnsafeRegister and Register do the same.
        public CancellationTokenRegistration UnsafeRegister(Action<object?> callback, object? state) =>
            Register(callback, state, useSynchronizationContext: false);

        public CancellationTokenRegistration UnsafeRegister(Action<object?, CancellationToken> callback, object? state) =>
            Register(callback, state);
    }
}
