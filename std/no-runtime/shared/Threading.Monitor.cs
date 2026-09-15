// Monitor — what `lock (x) { ... }` compiles into.
//
// The compiler does not ask for a type here, it asks for two methods by exact
// signature, and refuses the `lock` statement outright when they are missing.
// That is why this was worth writing rather than working around: without it,
// any source using `lock` is unbuildable, however little contention it has.
//
// The hard part is not the locking, it is that the lock belongs to an object
// that has nowhere to keep it. A real runtime hides a word in the object header
// for exactly this. Ours has no spare header bits, so the association lives in
// a side table keyed by reference identity — the same shape as the kernel's
// HostLocks, for the same reason.
//
// The table is fixed and small. That is a deliberate bound, not an oversight:
// `lock` in this system guards a handful of long-lived structures, and a table
// that grew without limit would be a slow leak keyed by objects that are then
// kept alive forever. Overflowing it is a fault worth hearing about, not one to
// paper over — so it throws.

namespace System.Threading
{
    public static class Monitor
    {
        private const int Capacity = 64;

        // Parallel arrays rather than an array of a struct: the entries are
        // written from more than one thread, and keeping the owner word in its
        // own array means a compare-and-swap touches exactly that word.
        // Declared without an initialiser and filled on first use: a static
        // field WITH an initialiser gives the type a class constructor, and the
        // check ILC emits for one does not work in this environment. Same shape
        // as the kernel's HostLocks, which is where this pattern is proven.
        private static object?[] s_owners = null!;
        private static int[] s_holders = null!;
        private static int[] s_recursion = null!;

        // Threads blocked on the slot's owner word, so Exit knows whether a
        // wake is worth the call.
        private static int[] s_waiters = null!;

        // Zero means "nobody", so thread ids start at one.
        private const int Unowned = 0;

        public static void Enter(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = SlotFor(obj);
            int self = CurrentThreadId();

            while (true)
            {
                if (Interlocked.CompareExchange(ref s_holders[slot], self, Unowned) == Unowned)
                {
                    s_recursion[slot] = 1;
                    return;
                }

                // Already ours: `lock` is reentrant, and a nested lock on the
                // same object must not deadlock against itself.
                if (s_holders[slot] == self)
                {
                    s_recursion[slot]++;
                    return;
                }

                // Held elsewhere. Block on the owner word until it changes:
                // spinning on one core means the holder cannot run. This used
                // to poll with Sleep(1), a whole timer tick per contended lock.
                //
                // Counted before the owner is read: an Exit that releases
                // after this either sees the count and wakes, or released
                // before the read, and then the word already differs and the
                // wait returns at once.
                Interlocked.Increment(ref s_waiters[slot]);
                int holder = Interlocked.Read(ref s_holders[slot]);
                if (holder != Unowned && holder != self)
                    ThreadBackend.WaitWhile(ref s_holders[slot], holder, ThreadBackend.Infinite);
                Interlocked.Decrement(ref s_waiters[slot]);
            }
        }

        public static void Enter(object obj, ref bool lockTaken)
        {
            Enter(obj);
            lockTaken = true;
        }

        public static void Exit(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = SlotFor(obj);
            int self = CurrentThreadId();

            if (s_holders[slot] != self)
                throw new SynchronizationLockException(
                    "Monitor.Exit called from a thread that does not hold the lock.");

            if (--s_recursion[slot] > 0) return;

            // Release last, and through the same door it was taken: the waiter
            // is looking at exactly this word.
            Interlocked.Exchange(ref s_holders[slot], Unowned);
            if (Interlocked.Read(ref s_waiters[slot]) != 0)
                ThreadBackend.WakeAll(ref s_holders[slot]);
        }

        public static bool TryEnter(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = SlotFor(obj);
            int self = CurrentThreadId();

            if (Interlocked.CompareExchange(ref s_holders[slot], self, Unowned) == Unowned)
            {
                s_recursion[slot] = 1;
                return true;
            }

            if (s_holders[slot] == self)
            {
                s_recursion[slot]++;
                return true;
            }

            return false;
        }

        public static bool TryEnter(object obj, int millisecondsTimeout)
        {
            // A millisecond at a time, but each wait ends as soon as the owner
            // word changes rather than at the next tick.
            int slot = SlotFor(obj);
            for (int waited = 0; waited < millisecondsTimeout; waited++)
            {
                if (TryEnter(obj)) return true;
                int holder = Interlocked.Read(ref s_holders[slot]);
                Interlocked.Increment(ref s_waiters[slot]);
                if (holder != Unowned)
                    ThreadBackend.WaitWhile(ref s_holders[slot], holder, 1);
                Interlocked.Decrement(ref s_waiters[slot]);
            }
            return TryEnter(obj);
        }

        // Pulse / Wait are the condition-variable half of Monitor. Nothing in
        // this system uses them yet, and guessing at an implementation would be
        // worse than saying so: a Wait that returned immediately would turn
        // every waiting loop into a spin, silently.
        public static void Pulse(object obj) => throw new NotSupportedException(
            "Monitor.Pulse is not implemented — see std/no-runtime/shared/Threading.Monitor.cs.");

        public static void PulseAll(object obj) => throw new NotSupportedException(
            "Monitor.PulseAll is not implemented — see std/no-runtime/shared/Threading.Monitor.cs.");

        public static bool Wait(object obj) => throw new NotSupportedException(
            "Monitor.Wait is not implemented — see std/no-runtime/shared/Threading.Monitor.cs.");

        /// <summary>
        /// Finds this object's slot, claiming a free one if it has none.
        /// </summary>
        private static int SlotFor(object obj)
        {
            if (s_owners == null)
            {
                s_owners = new object?[Capacity];
                s_holders = new int[Capacity];
                s_recursion = new int[Capacity];
                s_waiters = new int[Capacity];
            }

            int start = (int)((uint)obj.GetHashCode() % Capacity);

            for (int probe = 0; probe < Capacity; probe++)
            {
                int slot = (start + probe) % Capacity;

                if (ReferenceEquals(s_owners[slot], obj)) return slot;

                if (s_owners[slot] == null)
                {
                    // Claim it. Two threads locking a brand new object at once
                    // can both land here, so re-check after writing: the loser
                    // simply keeps probing and finds the winner's slot.
                    s_owners[slot] = obj;
                    if (ReferenceEquals(s_owners[slot], obj)) return slot;
                }
            }

            throw new InvalidOperationException(
                "Monitor: no free lock slots. The table is a fixed 64 entries — " +
                "see std/no-runtime/shared/Threading.Monitor.cs.");
        }

        private static int CurrentThreadId() => ManagedThreadIds.Current;
    }

    public class SynchronizationLockException : Exception
    {
        public SynchronizationLockException() : base("Object synchronization method was called from an unsynchronized block of code.") { }
        public SynchronizationLockException(string message) : base(message) { }
    }
}
