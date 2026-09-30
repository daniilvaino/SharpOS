// Who am I? — the answer the rest of threading is built on.
//
// Environment.CurrentManagedThreadId used to return the constant 1, which was
// true enough when there was one thread and harmless while the only consumer
// was Roslyn's iterator rewriter comparing it against itself.
//
// It stops being harmless the moment anything asks "is this lock mine?".
// Monitor does, on every entry, because `lock` is reentrant: a thread already
// holding a lock must be let straight through, and a thread that is not must
// wait. With one id shared by everyone, every thread is "already holding it",
// and mutual exclusion silently becomes no exclusion at all — the worst kind of
// defect, since the code reads as correct and the lock appears to work.
//
// The first attempt stored the id in a [ThreadStatic]. That does not work here:
// ILC compiles a thread-static into a call through Internal.Runtime.ThreadStatics,
// which neither tier's runtime has, and the build fails with the type named.
// Failing at link time is the good outcome — a thread-static that quietly
// resolved to one shared slot would have reintroduced the exact bug above.
//
// So the id comes from whoever actually knows: the scheduler owns the threads
// and can name the one running. Same seam as ThreadBackend, for the same reason.

namespace System.Threading
{
    public static unsafe class ManagedThreadIds
    {
        private static delegate*<int> s_currentId;

        public static void Install(delegate*<int> currentId) { s_currentId = currentId; }

        /// <summary>
        /// Whether ids are genuinely per thread. False means the single-thread
        /// answer below is in use, and anything asking "is this mine?" gets yes
        /// from every thread — see Monitor.
        /// </summary>
        public static bool IsPerThread => s_currentId != null;

        /// <summary>
        /// This thread's id. Never zero: zero is Monitor's "unowned" value, and
        /// a real thread holding it would make an owned lock look free.
        /// </summary>
        public static int Current
        {
            get
            {
                if (s_currentId == null) return 1;

                int id = s_currentId();
                return id == 0 ? 1 : id;
            }
        }
    }
}
