// System.Threading.Thread — the handle, not the scheduler.
//
// What callers actually ask of it here is narrow: which thread am I, what is
// its id, put me to sleep for a while, and what culture is it using. Terminal.Gui
// wants all four; nothing in our tree wants Thread.Start, because starting work
// goes through Task.Run, which is where the backend seam already lives.
//
// So this deliberately does NOT expose Start. A Thread you can construct but not
// start would be a trap, and one that could start would be a second way to make
// a thread with its own bugs — the scheduler is reached through one door.
//
// Kept in step with ManagedThreadIds: the id here is the same id Monitor keys
// ownership on, so "am I the main thread?" and "do I hold this lock?" cannot
// disagree.

using System.Globalization;

namespace System.Threading
{
    public sealed class Thread
    {
        private readonly int _id;

        private Thread(int id) { _id = id; }

        /// <summary>
        /// A handle for the running thread. A fresh object each time rather
        /// than a cached one per thread: caching would need per-thread storage,
        /// which this environment does not have (see Threading.ManagedThreadId.cs),
        /// and every caller uses it for its id and then drops it.
        /// </summary>
        public static Thread CurrentThread => new Thread(ManagedThreadIds.Current);

        public int ManagedThreadId => _id;

        /// <summary>
        /// Always true: we hand out a handle for the running thread and have no
        /// way to name a dead one.
        /// </summary>
        public bool IsAlive => true;

        public bool IsBackground { get; set; }

        public string? Name { get; set; }

        // One culture in the system, so these are answers rather than settings.
        // Assignment is accepted and dropped, which is the honest behaviour for
        // a system that has nothing to switch to — see std Globalization.cs.
        public CultureInfo CurrentCulture
        {
            get => CultureInfo.InvariantCulture;
            set { }
        }

        public CultureInfo CurrentUICulture
        {
            get => CultureInfo.InvariantCulture;
            set { }
        }

        public static void Sleep(int millisecondsTimeout)
        {
            if (millisecondsTimeout <= 0) return;
            ThreadBackend.Sleep((uint)millisecondsTimeout);
        }

        public static void Yield() => ThreadBackend.Sleep(0);
    }
}
