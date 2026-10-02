// Class-constructor runner — implements the helpers ILC emits before
// every read of a static reference field with an initializer.
//
// Without this support, ILC in --resilient mode emits fallback stubs
// that return non-canonical sentinel pointers (high bits 0xF000...);
// dereferencing those gives the #PF we hit in step 34 SystemBanner.
//
// Implementation ported verbatim from Test.CoreLib (which itself is
// the minimum-viable subset of System.Private.CoreLib's full version).
//
// Thread-safe since pipe_plan.md item 9: one thread runs a cctor, others
// wait for it, the running thread re-enters freely.

using System.Threading;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    // Fixed runtime-known layout. ILC emits one StaticClassConstructionContext
    // per type with a cctor; the structure lives in the binary's data section,
    // is zero-initialized, and is passed by reference to the runner methods.
    //
    // Layout MUST match: cctorMethodAddress (8) + initialized (4). ILC reads
    // the address and writes the flag; we mediate everything between.
    [StructLayout(LayoutKind.Sequential)]
    public struct StaticClassConstructionContext
    {
        public IntPtr cctorMethodAddress;
        public volatile int initialized;
    }

    internal static class ClassConstructorRunner
    {
        // net8/major-9 diagnostic counters (plain ints, no cctor). CheckCalls
        // = how many times a cctor-check helper fired; CctorRuns = how many
        // times a cctor body was actually invoked. A probe reads these
        // around a known lazy-cctor access to tell "ILC never emitted the
        // check (preinit'd)" from "check fired but cctor was skipped".
        internal static int CheckCalls;
        internal static int CctorRuns;
        // First-check raw context capture (major-9 layout probe).
        internal static long FirstCtxQ0;   // raw bytes [0..7]
        internal static long FirstCtxQ1;   // raw bytes [8..15]
        internal static int FirstInitAt8;  // what we read as `initialized` (@+8)

        // Two entry points ILC may emit, one per static-base flavor.
        // Both run the cctor (if needed) then pass through the static base
        // pointer. The runtime uses the return value as the actual address
        // of the static field storage.

        private static unsafe object CheckStaticClassConstructionReturnGCStaticBase(
            ref StaticClassConstructionContext context, object gcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return gcStaticBase;
        }

        private static unsafe IntPtr CheckStaticClassConstructionReturnNonGCStaticBase(
            ref StaticClassConstructionContext context, IntPtr nonGcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return nonGcStaticBase;
        }

        // Race-aware initializer. State machine on context.initialized:
        //   0 → 2 (winner — runs cctor) → 1 (done)
        //   0 → blocked (loser — spin until winner reaches 1)
        //   1 → fast return (already done)
        //
        // CAS guarantees only one thread runs the cctor body. Memory barrier
        // after the cctor ensures any writes inside it become visible before
        // the initialized flag flips to 1.
        // Thread-safe run of a type's cctor (pipe_plan.md item 9: the kernel
        // now preempts, apps have threads).
        //
        // The pending cctor's address is the state: non-zero means "not run".
        // The thread that runs it first swaps in InProgress, an odd value no
        // code address can be. Another thread that sees InProgress waits,
        // yielding, until the address becomes zero; the running thread itself
        // seeing it (a static touched from inside its own cctor, directly or
        // through a cctor it triggers) goes straight through and uses the
        // partially-initialised storage, as before. Which thread is running
        // which cctor is kept in a small table, because the context has no
        // room for an owner.
        //
        // The single-threaded version nulled the address BEFORE running the
        // cctor, so a second thread arriving meanwhile skipped it and read
        // statics the first was still filling in.
        //
        // Ported in spirit from Test.CoreLib / System.Private.CoreLib's
        // ClassConstructorRunner (which spins on a per-context lock and tracks
        // the running thread for the recursion case); cut: deadlock detection
        // between two threads each inside the other's cctor, and
        // TypeInitializationException — a cctor that throws counts as run.
        private const long InProgress = 1;
        private const int MaxRunning = 32;

        private unsafe struct RunningTable
        {
            public fixed long Context[MaxRunning];
            public fixed int Owner[MaxRunning];
        }

        private static RunningTable s_running;
        private static int s_runningLock;

        /// <summary>
        /// Whether the calling code may wait for another thread. The kernel
        /// answers no inside an interrupt handler: the thread holding what it
        /// would wait for is the one it interrupted, or one that cannot run
        /// until the handler returns. Null: always.
        /// </summary>
        public static unsafe delegate*<bool> s_canWait;

        private static unsafe void CheckStaticClassConstruction(
            ref StaticClassConstructionContext context)
        {
            CheckCalls++;
            if (CheckCalls == 1)
            {
                fixed (StaticClassConstructionContext* c = &context)
                {
                    byte* b = (byte*)c;
                    FirstCtxQ0 = *(long*)b;
                    FirstCtxQ1 = *(long*)(b + 8);
                }
                FirstInitAt8 = context.initialized;
            }
            // major 9 (ILC 8): there is NO separate `initialized` int at +8
            // (that slot holds the GC static base pointer). The cctor is
            // pending iff cctorMethodAddress != 0; the runner nulls it once
            // run. Confirmed against dotnet/runtime release/8.0
            // ClassConstructorRunner (`if (pfnCctor == 0) return;`).
            ref long state = ref Unsafe.As<IntPtr, long>(ref context.cctorMethodAddress);

            // Where waiting is impossible, the single-threaded rule: a cctor in
            // progress is gone through, one not yet started is run here.
            if (s_canWait != null && !s_canWait())
            {
                long pending = state;
                if (pending == 0 || pending == InProgress) return;
                if (Interlocked.CompareExchange(ref state, InProgress, pending) != pending) return;
                CctorRuns++;
                ((delegate*<void>)(IntPtr)pending)();
                Interlocked.MemoryBarrier();
                Interlocked.Exchange(ref state, 0);
                return;
            }

            long contextKey = (long)Unsafe.AsPointer(ref context);
            int me = Environment.CurrentManagedThreadId;

            while (true)
            {
                long pfn = Interlocked.CompareExchange(ref state, 0, 0);
                if (pfn == 0)
                    return;                                 // already run

                if (pfn == InProgress)
                {
                    if (RunningOwner(contextKey) == me)
                        return;                             // our own cctor, re-entered
                    Thread.Yield();
                    continue;
                }

                if (Interlocked.CompareExchange(ref state, InProgress, pfn) != pfn)
                    continue;                               // lost the race; look again

                CctorRuns++;
                int slot = Track(contextKey, me);
                try
                {
                    ((delegate*<void>)(IntPtr)pfn)();
                }
                finally
                {
                    Interlocked.MemoryBarrier();
                    Untrack(slot);
                    Interlocked.Exchange(ref state, 0);
                }
                return;
            }
        }

        private static unsafe int RunningOwner(long contextKey)
        {
            int owner = 0;
            EnterTable();
            fixed (RunningTable* t = &s_running)
                for (int i = 0; i < MaxRunning; i++)
                    if (t->Context[i] == contextKey) { owner = t->Owner[i]; break; }
            LeaveTable();
            return owner;
        }

        // A full table leaves the cctor untracked: its own re-entry then waits
        // on itself. Thirty-two cctors running at once, nested or concurrent,
        // is far past anything seen.
        private static unsafe int Track(long contextKey, int owner)
        {
            int slot = -1;
            EnterTable();
            fixed (RunningTable* t = &s_running)
                for (int i = 0; i < MaxRunning; i++)
                    if (t->Context[i] == 0) { t->Context[i] = contextKey; t->Owner[i] = owner; slot = i; break; }
            LeaveTable();
            return slot;
        }

        private static unsafe void Untrack(int slot)
        {
            if (slot < 0) return;
            EnterTable();
            fixed (RunningTable* t = &s_running)
            {
                t->Context[slot] = 0;
                t->Owner[slot] = 0;
            }
            LeaveTable();
        }

        private static void EnterTable()
        {
            while (Interlocked.CompareExchange(ref s_runningLock, 1, 0) != 0)
                Thread.Yield();
        }

        private static void LeaveTable() => Interlocked.Exchange(ref s_runningLock, 0);
    }
}
