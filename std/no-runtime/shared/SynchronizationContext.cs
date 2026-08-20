// SynchronizationContext — "run this back where I came from".
//
// A UI library uses it for one thing: a background thread that needs to touch
// the interface posts the work instead of doing it, and the context delivers it
// on the thread that owns the screen. Terminal.Gui captures the current context
// when the application starts and posts through it from its input loop.
//
// The base class here does the honest default the real BCL does: Post and Send
// simply run the callback, Post on a thread of its own and Send on the caller's.
// That is not a UI context and does not pretend to be — nothing is marshalled
// anywhere. A real one belongs with whoever owns the main loop, and it will
// derive from this and override the two methods, which is exactly the shape the
// library expects to find.
//
// Ported from dotnet/runtime v8.0 (MIT), cut to the members in use: the
// operation-started/completed callbacks, the copy protocol and the wait
// machinery are not here.

using System.Threading.Tasks;

namespace System.Threading
{
    public delegate void SendOrPostCallback(object? state);

    public class SynchronizationContext
    {
        // Process-wide rather than per thread: thread-statics do not work in
        // this environment (see Threading.ManagedThreadId.cs). That matters
        // only for code installing a different context on different threads,
        // which nothing here does — stated rather than hidden.
        private static SynchronizationContext? s_current;

        public SynchronizationContext() { }

        public static SynchronizationContext? Current => s_current;

        public static void SetSynchronizationContext(SynchronizationContext? syncContext)
            => s_current = syncContext;

        /// <summary>Runs the callback on the caller's thread, and waits.</summary>
        public virtual void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>Hands the callback off and returns without waiting.</summary>
        public virtual void Post(SendOrPostCallback d, object? state)
            => Task.Run(() => d(state));

        public virtual SynchronizationContext CreateCopy() => new SynchronizationContext();
    }
}
