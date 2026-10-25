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
// The table is fixed and small, and bounds the locks in use at one time, not
// the objects ever locked: a slot goes back once nobody holds the object or
// waits for it. Until step211 a slot stayed bound to its first object for
// good, so 64 distinct objects ever locked used the table up, and each was
// kept alive from it — interactive KQL, which locks a fresh code service per
// completion, failed on about the fourteenth Tab. Overflowing it now means 64
// locks at once, a fault worth hearing about, so it still throws.

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

        // Threads with a claim on the slot: the holder, and each thread on its
        // way in or waiting. The slot goes back to the table when this reaches
        // zero; until then the object stays bound to it, or a waiter would
        // take a slot that already belongs to someone else.
        private static int[] s_users = null!;

        // A freed slot. Lookups probe past it, so an object placed after it in
        // the probe chain is still found; a claim may reuse it.
        private static object s_removed = null!;

        // Guards s_owners and s_users. Held only for a probe of the table,
        // never while blocked on a lock.
        private static int s_tableLock;
        private static int s_tableWaiters;

        // Zero means "nobody", so thread ids start at one.
        private const int Unowned = 0;

        public static void Enter(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = Claim(obj);
            int self = CurrentThreadId();

            while (true)
            {
                if (Interlocked.CompareExchange(ref s_holders[slot], self, Unowned) == Unowned)
                {
                    s_recursion[slot] = 1;
                    return;
                }

                // Already ours: `lock` is reentrant, and a nested lock on the
                // same object must not deadlock against itself. The holder's
                // claim already keeps the slot, so this one goes back.
                if (s_holders[slot] == self)
                {
                    s_recursion[slot]++;
                    Release(slot);
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

            int slot = Find(obj);
            int self = CurrentThreadId();

            if (slot < 0 || s_holders[slot] != self)
                throw new SynchronizationLockException(
                    "Monitor.Exit called from a thread that does not hold the lock.");

            if (--s_recursion[slot] > 0) return;

            // Release last, and through the same door it was taken: the waiter
            // is looking at exactly this word.
            Interlocked.Exchange(ref s_holders[slot], Unowned);
            if (Interlocked.Read(ref s_waiters[slot]) != 0)
                ThreadBackend.WakeAll(ref s_holders[slot]);
            Release(slot);
        }

        public static bool TryEnter(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = Claim(obj);
            if (TryTake(slot)) return true;
            Release(slot);
            return false;
        }

        public static bool TryEnter(object obj, int millisecondsTimeout)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            // A millisecond at a time, but each wait ends as soon as the owner
            // word changes rather than at the next tick. One claim covers the
            // whole wait, so the slot cannot change hands under it.
            int slot = Claim(obj);
            for (int waited = 0; waited < millisecondsTimeout; waited++)
            {
                if (TryTake(slot)) return true;
                int holder = Interlocked.Read(ref s_holders[slot]);
                Interlocked.Increment(ref s_waiters[slot]);
                if (holder != Unowned)
                    ThreadBackend.WaitWhile(ref s_holders[slot], holder, 1);
                Interlocked.Decrement(ref s_waiters[slot]);
            }
            if (TryTake(slot)) return true;
            Release(slot);
            return false;
        }

        /// <summary>Whether the current thread holds the lock on <paramref name="obj"/>.</summary>
        /// <remarks>
        /// Looks the slot up without claiming one: asking must not spend a
        /// table entry on an object nobody has locked.
        /// </remarks>
        public static bool IsEntered(object obj)
        {
            if (obj == null) throw new ArgumentNullException(nameof(obj));

            int slot = Find(obj);
            return slot >= 0 && s_holders[slot] == CurrentThreadId();
        }

        // Pulse / Wait are the condition-variable half of Monitor. Nothing in
        // this system uses them yet, and guessing at an implementation would be
        // worse than saying so: a Wait that returned immediately would turn
        // every waiting loop into a spin, silently.
        public static void Pulse(object obj) => throw new NotSupportedException(
            "Monitor.Pulse is not implemented — see std-no-runtime/Threading.Monitor.cs.");

        public static void PulseAll(object obj) => throw new NotSupportedException(
            "Monitor.PulseAll is not implemented — see std-no-runtime/Threading.Monitor.cs.");

        public static bool Wait(object obj) => throw new NotSupportedException(
            "Monitor.Wait is not implemented — see std-no-runtime/Threading.Monitor.cs.");

        /// <summary>
        /// Builds the side tables. Idempotent, and meant to be called once at
        /// startup — before a second thread exists — by whoever installs the
        /// threading backend.
        /// </summary>
        /// <remarks>
        /// This used to be inline in SlotFor, and it published s_owners FIRST.
        /// Every other method tests s_owners and then indexes the other three,
        /// so one timer tick landing between those assignments left the next
        /// thread reading a null array: `lock` faulted on a null dereference at
        /// offset 8, which is an array's length field. The lock probe runs two
        /// hammering threads with preemption on and hit it exactly there.
        ///
        /// Publishing the flag last closes that. It does not close two threads
        /// both building tables at once — the reference CompareExchange in this
        /// std is a managed read-compare-write, not an atomic one — which is
        /// why the real fix is calling this before there is anyone to race.
        /// </remarks>
        public static void EnsureTables()
        {
            if (s_owners != null) return;

            int[] holders = new int[Capacity];
            int[] recursion = new int[Capacity];
            int[] waiters = new int[Capacity];
            int[] users = new int[Capacity];
            object removed = new object();
            object?[] owners = new object?[Capacity];

            s_holders = holders;
            s_recursion = recursion;
            s_waiters = waiters;
            s_users = users;
            s_removed = removed;
            s_owners = owners;          // last: it is the one everyone tests
        }

        // The holder word, taken or re-entered by this thread on a slot it
        // already has a claim on. A re-entry hands the extra claim back.
        private static bool TryTake(int slot)
        {
            int self = CurrentThreadId();

            if (Interlocked.CompareExchange(ref s_holders[slot], self, Unowned) == Unowned)
            {
                s_recursion[slot] = 1;
                return true;
            }

            if (s_holders[slot] == self)
            {
                s_recursion[slot]++;
                Release(slot);
                return true;
            }

            return false;
        }

        /// <summary>
        /// This object's slot with one more claim on it, binding a free slot
        /// to the object when it has none.
        /// </summary>
        private static int Claim(object obj)
        {
            EnsureTables();
            LockTable();
            try
            {
                int start = (int)((uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj) % Capacity);
                int free = -1;

                for (int probe = 0; probe < Capacity; probe++)
                {
                    int slot = (start + probe) % Capacity;
                    object? owner = s_owners[slot];

                    if (ReferenceEquals(owner, obj))
                    {
                        s_users[slot]++;
                        return slot;
                    }

                    if (ReferenceEquals(owner, s_removed))
                    {
                        if (free < 0) free = slot;
                        continue;
                    }

                    if (owner == null)
                    {
                        // The end of this object's chain: it has no slot.
                        if (free < 0) free = slot;
                        break;
                    }
                }

                if (free < 0)
                    throw new InvalidOperationException(
                        "Monitor: no free lock slots. The table holds 64 locks at once — " +
                        "see std-no-runtime/Threading.Monitor.cs.");

                s_owners[free] = obj;
                s_users[free] = 1;
                return free;
            }
            finally
            {
                UnlockTable();
            }
        }

        /// <summary>The object's slot, or -1 when it has none; claims nothing.</summary>
        private static int Find(object obj)
        {
            EnsureTables();
            LockTable();
            try
            {
                int start = (int)((uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj) % Capacity);

                for (int probe = 0; probe < Capacity; probe++)
                {
                    int slot = (start + probe) % Capacity;
                    object? owner = s_owners[slot];
                    if (ReferenceEquals(owner, obj)) return slot;
                    if (owner == null) return -1;
                }

                return -1;
            }
            finally
            {
                UnlockTable();
            }
        }

        // Gives back one claim; the last one frees the slot. Marked removed
        // rather than emptied, so the probe chains through it stay whole.
        private static void Release(int slot)
        {
            LockTable();
            if (--s_users[slot] == 0)
                s_owners[slot] = s_removed;
            UnlockTable();
        }

        private static void LockTable()
        {
            while (Interlocked.CompareExchange(ref s_tableLock, 1, 0) != 0)
            {
                Interlocked.Increment(ref s_tableWaiters);
                if (Interlocked.Read(ref s_tableLock) != 0)
                    ThreadBackend.WaitWhile(ref s_tableLock, 1, 1);
                Interlocked.Decrement(ref s_tableWaiters);
            }
        }

        private static void UnlockTable()
        {
            Interlocked.Exchange(ref s_tableLock, 0);
            if (Interlocked.Read(ref s_tableWaiters) != 0)
                ThreadBackend.WakeAll(ref s_tableLock);
        }

        private static int CurrentThreadId() => ManagedThreadIds.Current;
    }

    public class SynchronizationLockException : Exception
    {
        public SynchronizationLockException() : base("Object synchronization method was called from an unsynchronized block of code.") { }
        public SynchronizationLockException(string message) : base(message) { }
    }
}
