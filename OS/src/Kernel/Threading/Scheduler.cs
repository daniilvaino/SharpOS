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

            // step104: if BootStackSwitch ran, the boot thread now lives
            // on our owned BootStackPool — register its bounds here so
            // HwFaultBridge can report "RSP within bounds, used=..."
            // instead of "no owned stack". Pre-switch boots (failure
            // mode) leave the fields null as before.
            byte* sBase = null;
            byte* sTop  = null;
            uint  sLen  = 0;
            if (OS.Boot.BootStackSwitch.IsDone)
            {
                sBase = OS.Boot.BootStackSwitch.OwnedStackBase;
                sTop  = OS.Boot.BootStackSwitch.OwnedStackTop;
                sLen  = OS.Boot.BootStackSwitch.OwnedStackBytes;
            }

            Thread t = new Thread
            {
                Id = s_nextId++,
                State = ThreadState.Running,
                ContextBlock = ctx,
                StackBase = sBase,
                StackTop = sTop,
                StackBytes = sLen,
                Teb = null,
                Entry = null,
            };
            s_current = t;
            return true;
        }

        // Create a new thread that, on first dispatch, executes `entry`.
        // The entry function must be [UnmanagedCallersOnly] (Win64 ABI)
        // so the synthetic stack frame's `ret` lands cleanly at it.
        // Caller passes 0 for stackBytes to get DefaultStackBytes (64 KiB).
        // `owner` is optional — non-null links the thread into the
        // Process via Process.FirstThread (Phase E7).
        // `startRunnable` = false (Phase E9) leaves the thread in `New`
        // state and out of the runnable queue; caller resumes via
        // Scheduler.MakeRunnable when ready. Used by SpawnHosted when
        // CoreCLR passes CREATE_SUSPENDED.
        public static Thread? Spawn(delegate* unmanaged<void> entry, uint stackBytes, Process? owner = null, bool startRunnable = true)
        {
            if (entry == null) return null;
            if (stackBytes == 0) stackBytes = DefaultStackBytes;

            byte* ctx = AllocateContextBlock();
            if (ctx == null) return null;

            byte* guard;
            byte* stack = AllocateStack(stackBytes, out guard);
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
                State = startRunnable ? ThreadState.Runnable : ThreadState.New,
                Kind = ThreadKind.Kernel,
                ContextBlock = ctx,
                StackBase = stack,
                StackTop = stackTop,
                StackBytes = stackBytes,
                GuardPage = guard,
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

            if (startRunnable)
                EnqueueRunnable(t);
            return t;
        }

        // Phase E9 -- transition a suspended (`New`) thread to `Runnable`
        // and enqueue. Idempotent against already-runnable / running /
        // exited threads. Used by SharpOSHost_ResumeThread to honor
        // CoreCLR's CREATE_SUSPENDED + ResumeThread sequence.
        public static bool MakeRunnable(Thread t)
        {
            if (t == null) return false;
            if (t.State != ThreadState.New) return false;
            t.State = ThreadState.Runnable;
            EnqueueRunnable(t);
            return true;
        }

        // Phase E9 -- spawn a thread whose entry has Win32 LPTHREAD_START_ROUTINE
        // signature `uint(void*)` instead of our native `void()`. Used by
        // SharpOSHost_CreateThread to bridge CoreCLR's PAL thread model
        // to our Scheduler. The entry's argument and return value are
        // stashed on the Thread; HostedTrampoline below reads them on
        // first dispatch.
        //
        // Phase E9.b: also allocate a per-thread TEB + tls_block via
        // CoreClrTeb so CoreCLR's native `thread_local` (gs:[0x58][_tls_index])
        // gets its own copy. Without this every hosted thread shared one
        // TEB and `t_ThreadType` polluted across threads (debugger helper
        // misidentification, assertion storms). The TEB pointer is also
        // written into ContextBlock+0x08 so X64Asm.CoopSwitch can swap
        // IA32_GS_BASE atomically with the rest of the context.
        //
        // Single-CPU cooperative: between Spawn's EnqueueRunnable and
        // the caller setting HostedEntry/HostedParam/Teb, no other thread
        // can run -- so the trampoline never reads null fields. When SMP /
        // preemption land, this needs a barrier or hand-off via Spawn
        // taking the args directly.
        public static Thread? SpawnHosted(delegate* unmanaged<void*, uint> hostedEntry,
                                          void* hostedParam,
                                          uint stackBytes,
                                          Process? owner = null,
                                          bool suspended = false)
        {
            if (hostedEntry == null) return null;
            Thread? t = Spawn(&HostedTrampoline, stackBytes, owner, startRunnable: !suspended);
            if (t == null) return null;

            // Promote to CoreCLR kind and attach binding -- the kernel
            // trampoline reads HostedEntry/ClrThreadOpaquePtr from this
            // box, not from inline Thread fields (boundary per
            // docs/threading-architecture.md §3).
            t.Kind = ThreadKind.CoreClr;
            t.Binding = new ManagedThreadBinding {
                HostedEntry = hostedEntry,
                ClrThreadOpaquePtr = hostedParam,
            };

            // Allocate per-thread TEB. Stack range from the kernel.Thread
            // (StackTop is HIGH addr, StackBase is LOW addr -- matches
            // NT_TIB StackBase/StackLimit semantics).
            ulong stackTop  = (ulong)t.StackTop;
            ulong stackBase = (ulong)t.StackBase;
            byte* teb = CoreClrTeb.Allocate(stackTop, stackBase);
            if (teb != null)
            {
                t.Teb = teb;
                *(ulong*)(t.ContextBlock + 0x08) = (ulong)teb;
            }
            // teb == null -> ContextBlock+0x08 stays 0 (CoopSwitch skips
            // gs swap). Acceptable for non-CoreCLR kernel threads where
            // we don't need CoreCLR thread_local isolation.

            return t;
        }

        // Trampoline that bridges Scheduler's `void()` entry convention
        // to the hosted `uint(void*)` convention. Reads Scheduler.Current's
        // HostedEntry + HostedParam, calls them, captures exit code, signals
        // any JoinEvent, then Scheduler.Exit's.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void HostedTrampoline()
        {
            OS.Hal.Console.WriteLine("[Tramp] entry reached");

            Thread? curr = s_current;
            ManagedThreadBinding? bind = curr == null ? null : curr.Binding;
            if (curr == null || bind == null || bind.HostedEntry == null)
            {
                OS.Hal.Console.WriteLine("[Tramp] curr/Binding/HostedEntry null -- Exit");
                Scheduler.Exit();
                return;
            }

            OS.Hal.Console.Write("[Tramp] calling entry=0x");
            OS.Hal.Console.WriteHex((ulong)bind.HostedEntry);
            OS.Hal.Console.Write(" param=0x");
            OS.Hal.Console.WriteHex((ulong)bind.ClrThreadOpaquePtr);
            OS.Hal.Console.WriteLine("");

            uint exitCode = bind.HostedEntry(bind.ClrThreadOpaquePtr);

            OS.Hal.Console.Write("[Tramp] entry returned exitCode=");
            OS.Hal.Console.WriteUInt(exitCode);
            OS.Hal.Console.WriteLine("");

            bind.HostedExitCode = exitCode;
            bind.HasExited = true;
            if (bind.JoinEvent != null)
                bind.JoinEvent.Set();

            OS.Hal.Console.WriteLine("[Tramp] Exit");
            Scheduler.Exit();
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
            // Hot path: every Yield() hits this. Skip the HPET MMIO if
            // nothing is parked on a deadline — saves ~1us per yield on
            // QEMU (HPET reads are slow there), and PS bootstrap fires
            // 10⁵+ yields so the elision matters.
            if (!TimerQueue.HasPending) return;
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

        private static byte* AllocateStack(uint stackBytes, out byte* guardPage)
        {
            // Page-aligned physical allocation; identity-mapped → VA == PA.
            // Step 102: +1 page below the stack as a non-present guard --
            // any access past StackBase #PFs cleanly with CR2 in the
            // guard range and the trap handler reports stack overflow
            // (HwFaultBridge.cs) instead of letting it walk into the
            // adjacent heap object. The boot thread's UEFI stack has
            // no guard (we don't own that allocation).
            uint pageCount = (stackBytes + 4095) / 4096;
            uint totalPages = pageCount + 1;
            ulong phys = PhysicalMemory.AllocPages(totalPages);
            if (phys == 0) { guardPage = null; return null; }

            guardPage = (byte*)phys;             // lowest page = guard
            byte* stack = (byte*)phys + 4096;    // usable stack starts above

            // Mark guard non-present. Page is identity-mapped, so the
            // VA matches the physical address. Unmap clears the present
            // bit at the leaf PTE level; the page is still owned by
            // PhysicalMemory until thread exit (we don't free yet).
            OS.Kernel.Paging.Pager.Unmap((ulong)guardPage);

            return stack;
        }
    }
}
