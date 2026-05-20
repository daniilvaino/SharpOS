using OS.Hal;
using OS.Hal.Timer;
using OS.Kernel;

namespace OS.Kernel.Threading
{
    // Phase E4 cooperative scheduler. Round-robin runnable queue, single
    // CPU, no preemption — threads explicitly call Yield/Exit to relinquish.
    //
    // The boot thread is wrapped on Init so it has a ContextBlock that
    // CoopSwitch can save into; its stack stays unowned (UEFI/loader did
    // the allocation, we don't free).
    //
    // Spawned threads get a fresh stack via PhysicalMemory.AllocPages
    // (page-aligned, identity-mapped VA == PA) and a synthetic initial
    // stack frame so the first CoopSwitch's `ret` lands at the entry
    // function. Initial FP state is snapshotted from the current thread.
    internal static unsafe class Scheduler
    {
        private const uint ContextBlockBytes = 528;     // 16 (RSP + reserved) + 512 fxsave
        private const uint FxsaveAreaOffset  = 0x10;
        private const uint DefaultStackBytes = 16 * 4096;  // 64 KiB

        private static Thread? s_current;
        private static Thread? s_runnableHead;
        private static int s_nextId = 1;
        private static uint s_yieldCount;
        private static uint s_switchCount;

        public static Thread? Current => s_current;
        public static uint YieldCount  => s_yieldCount;
        public static uint SwitchCount => s_switchCount;

        // Wrap the current execution context as a Thread so its state can
        // be saved on the first switch out. Idempotent: subsequent calls
        // return true without re-wrapping. Returns false on ContextBlock
        // allocation failure.
        public static bool Init()
        {
            if (s_current != null) return true;

            byte* ctx = AllocateContextBlock();
            if (ctx == null) return false;

            // Snapshot current FP state so the first switch INTO this
            // thread (post-yield) restores something valid.
            X64Asm.Fxsave(ctx + FxsaveAreaOffset);

            Thread t = new Thread
            {
                Id = s_nextId++,
                State = ThreadState.Running,
                ContextBlock = ctx,
                StackBase = null,
                StackTop = null,
                StackBytes = 0,
                Teb = null,
                Entry = null,
            };
            s_current = t;
            return true;
        }

        // Create a new Runnable thread that, on first dispatch, executes
        // `entry`. The entry function must be [UnmanagedCallersOnly]
        // (Win64 ABI) so the synthetic stack frame's `ret` lands cleanly
        // at it. Caller should pass DefaultStackBytes (0) for the
        // standard 64 KiB. `owner` is optional — non-null links the
        // thread into the Process via Process.FirstThread (Phase E7).
        public static Thread? Spawn(delegate* unmanaged<void> entry, uint stackBytes, Process? owner = null)
        {
            if (entry == null) return null;
            if (stackBytes == 0) stackBytes = DefaultStackBytes;

            byte* ctx = AllocateContextBlock();
            if (ctx == null) return null;

            byte* stack = AllocateStack(stackBytes);
            if (stack == null) return null;

            byte* stackTop = stack + stackBytes;

            // Build synthetic initial frame:
            //   [SavedRsp + 0  .. +56] = 8 callee-saved GPRs (zero)
            //   [SavedRsp + 64]        = entry  (popped by `ret`)
            // The 8 GPRs are popped (r15 first → rbx last), then `ret`
            // pops the RIP slot. After ret RSP = SavedRsp + 72. For Win64
            // ABI at entry expectations (RSP%16 == 8 just after a CALL),
            // SavedRsp must be 16-byte aligned (since 72%16 = 8).
            ulong rspInt = (ulong)(stackTop - 72);
            rspInt &= ~0xFUL;   // align DOWN to 16-byte boundary
            byte* initRsp = (byte*)rspInt;

            ulong* slot = (ulong*)initRsp;
            for (int i = 0; i < 8; i++) slot[i] = 0;
            slot[8] = (ulong)entry;

            // ContextBlock: SavedRsp at offset 0; FXSAVE template snapshot.
            *(ulong*)ctx = (ulong)initRsp;
            *(ulong*)(ctx + 8) = 0;
            X64Asm.Fxsave(ctx + FxsaveAreaOffset);

            Thread t = new Thread
            {
                Id = s_nextId++,
                State = ThreadState.Runnable,
                ContextBlock = ctx,
                StackBase = stack,
                StackTop = stackTop,
                StackBytes = stackBytes,
                Teb = null,
                Entry = entry,
                OwnerProcess = owner,
            };

            // Link into the process thread list (head insert -- O(1)).
            // Cooperative single-CPU; no lock needed today.
            if (owner != null)
            {
                t.NextInProcess = owner.FirstThread;
                owner.FirstThread = t;
                owner.ThreadCount++;
            }

            EnqueueRunnable(t);
            return t;
        }

        // Cooperative yield. Phase E5: drain expired TimerQueue entries
        // first (wakes any threads whose deadline has elapsed), then
        // dispatch the runnable head. Self-switch is detected and elided.
        // If the current thread is Waiting and no other thread is ready,
        // spin-poll the timer queue until something becomes runnable —
        // this is the "no IRQ-driven wake yet" path (acceptable in
        // single-CPU cooperative; replace with HLT + IRQ in Phase E6+).
        public static void Yield()
        {
            s_yieldCount++;

            // Drain expired sleepers first so they participate in the
            // selection below.
            DrainExpiredTimers();

            Thread? curr = s_current;
            if (curr == null) return;

            Thread? next = DequeueRunnable();

            // If nobody else is runnable AND we're still Running, just
            // keep going (no-op yield). If we're Waiting (called from
            // Sleep / Event.Wait) we must NOT continue here — block
            // until something wakes us.
            if (next == null)
            {
                if (curr.State != ThreadState.Waiting)
                    return;
                while (next == null)
                {
                    DrainExpiredTimers();
                    next = DequeueRunnable();
                    // No CPU-pause hint yet; tight spin. Once IRQ-driven
                    // wake lands this becomes HLT-in-IST and the spin
                    // collapses to interrupt latency.
                }
            }

            // Re-enqueue current only if it's still Running. Threads
            // that just transitioned to Waiting / Exited / etc. stay
            // out of the ready queue.
            if (curr.State == ThreadState.Running)
            {
                curr.State = ThreadState.Runnable;
                EnqueueRunnable(curr);
            }

            // Self-switch elision: if `next` happens to be us (we were
            // the only Runnable and got re-enqueued above), skip the
            // shellcode round-trip.
            if (next == curr)
            {
                next.State = ThreadState.Running;
                return;
            }

            next.State = ThreadState.Running;
            s_current = next;
            s_switchCount++;
            X64Asm.CoopSwitch(curr.ContextBlock, next.ContextBlock);
            // CoopSwitch returns here when SOMEBODY switches back to curr.
        }

        // Phase E5 — block the current thread on the TimerQueue until
        // `milliseconds` of HPET time have elapsed. State transitions:
        //   Running -> Waiting (caller's "I want to sleep" intent)
        //   Yield   -> spins / dispatches until our deadline drains us back
        //   Running (after Yield returns)
        // Zero ms degrades to a plain Yield. Caller must be a real thread
        // (s_current != null) — Sleep before Scheduler.Init is a no-op.
        public static void Sleep(uint milliseconds)
        {
            Thread? curr = s_current;
            if (curr == null) return;
            if (milliseconds == 0) { Yield(); return; }

            ulong freq = Hpet.FrequencyHz;
            if (freq == 0) return;   // HPET not initialised — degrade silently
            ulong ticksPerMs = freq / 1000;
            if (ticksPerMs == 0) ticksPerMs = 1;

            ulong now = Hpet.ReadCounter();
            ulong deadline = now + (ulong)milliseconds * ticksPerMs;

            curr.State = ThreadState.Waiting;
            TimerQueue.Schedule(curr, deadline);
            Yield();
            // When we return, deadline has expired and someone (Yield's
            // drain or another scheduler tick) put us back on Runnable.
        }

        // Transition `t` from Waiting back to Runnable. Used by
        // TimerQueue.DrainExpired and by Event/Semaphore.Set.
        // Idempotent against not-currently-waiting state — guards against
        // double-wake (timer fires concurrently with explicit Set).
        public static void WakeFromWait(Thread t)
        {
            if (t == null) return;
            if (t.State != ThreadState.Waiting) return;
            t.State = ThreadState.Runnable;
            EnqueueRunnable(t);
        }

        private static void DrainExpiredTimers()
        {
            if (Hpet.FrequencyHz == 0) return;
            ulong now = Hpet.ReadCounter();
            TimerQueue.DrainExpired(now);
        }

        // Terminate the current thread. Marks it Exited; does NOT re-
        // enqueue it. Dispatches the next runnable thread (panics if
        // none — we have no idle thread yet). Never returns.
        public static void Exit()
        {
            Thread? curr = s_current;
            if (curr == null) { Panic.Fail("Scheduler.Exit: no current"); return; }
            curr.State = ThreadState.Exited;

            Thread? next = DequeueRunnable();
            if (next == null)
            {
                Panic.Fail("Scheduler.Exit: no other runnable");
                return;
            }

            next.State = ThreadState.Running;
            s_current = next;
            s_switchCount++;
            X64Asm.CoopSwitch(curr.ContextBlock, next.ContextBlock);
            // Unreachable — curr is Exited, no one re-enters its frame.
        }

        // ─── internal helpers ────────────────────────────────────────────

        private static void EnqueueRunnable(Thread t)
        {
            t.Next = null;
            if (s_runnableHead == null) { s_runnableHead = t; return; }
            Thread c = s_runnableHead;
            while (c.Next != null) c = c.Next;
            c.Next = t;
        }

        private static Thread? DequeueRunnable()
        {
            Thread? t = s_runnableHead;
            if (t == null) return null;
            s_runnableHead = t.Next;
            t.Next = null;
            return t;
        }

        private static byte* AllocateContextBlock()
        {
            // KernelHeap.Alloc would return 8-byte aligned payloads
            // (24-byte HeapBlock header lands at +24 from a 16-aligned
            // region base, so payloads end up at +8 mod 16). FXSAVE
            // requires its operand 16-byte aligned — we'd #GP. Take a
            // whole page instead: 4 KiB, guaranteed 16-aligned, plenty
            // of headroom for the 528-byte ContextBlock. Wasteful per-
            // thread; revisit when thread count matters.
            ulong phys = PhysicalMemory.AllocPage();
            if (phys == 0) return null;
            byte* p = (byte*)phys;
            // Zero just the header (SavedRsp + reserved). FXSAVE area
            // is overwritten by Fxsave() right after.
            for (int i = 0; i < 16; i++) p[i] = 0;
            return p;
        }

        private static byte* AllocateStack(uint stackBytes)
        {
            // Page-aligned physical allocation; identity-mapped → VA == PA.
            uint pageCount = (stackBytes + 4095) / 4096;
            ulong phys = PhysicalMemory.AllocPages(pageCount);
            if (phys == 0) return null;
            return (byte*)phys;
        }
    }
}
