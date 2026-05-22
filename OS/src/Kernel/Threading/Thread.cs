namespace OS.Kernel.Threading
{
    internal enum ThreadState : byte
    {
        New = 0,        // allocated, not yet enqueued
        Runnable = 1,   // in the runnable queue, waiting for CPU
        Running = 2,    // currently executing
        Waiting = 3,    // blocked on TimerQueue or Event/Semaphore wait list
        Exited = 4,     // terminated, will not run again
    }

    // Phase E9.c step 102 -- wait kind tag. Identifies which primitive
    // the thread is parked on; consumers (Event/Semaphore/Win32Mutex/
    // AddressWait/TimerQueue) set this on Wait() entry and Scheduler
    // clears it on Wake.
    internal enum WaitKind : byte
    {
        None      = 0,    // not blocked on any wait list
        Event     = 1,
        Semaphore = 2,
        Mutex     = 3,
        Address   = 4,    // WaitOnAddress
        Timer     = 5,    // Sleep / timed wait
    }

    // Phase E9.c step 102 -- wait state per docs/threading-architecture.md
    // §3. Currently inline-by-value on Thread; SMP (E13+) swaps Thread
    // to hold `WaitBlock* CurrentWait` for atomic CAS-swap. Field names
    // and grouping are the spec shape -- pointer-swap is a mechanical
    // migration when SMP scheduler lock lands.
    //
    // A thread is on AT MOST ONE wait list at a time, so the queue
    // links (Next + TimerNext) double up safely:
    //   Next       -- linked list of waiters on an Event/Semaphore/
    //                 Mutex/AddressWait queue. Head lives on the
    //                 primitive (e.g. Event._waitHead).
    //   TimerNext  -- separate list for TimerQueue, sorted by Deadline.
    //                 Thread can be in BOTH (timer + primitive) if a
    //                 future finite-timeout wait lands; today only one.
    //   Address    -- for WaitOnAddress: the user-memory address the
    //                 thread is parked on (identifies bucket entry).
    //   Deadline   -- HPET tick value at which Sleep should expire.
    //                 0 means no deadline (pure wait).
    //   Kind       -- which primitive this wait belongs to. Future
    //                 cancel paths (CancelSynchronousIo, timeout) read
    //                 this to know which queue to unlink from.
    internal unsafe struct WaitBlock
    {
        public Thread? Next;
        public Thread? TimerNext;
        public void* Address;
        public ulong Deadline;
        public WaitKind Kind;
    }

    // Phase E9.b step 102 -- thread "kind" tag per docs/threading-
    // architecture.md §3. Distinguishes scheduling-policy-relevant
    // thread origins. Today all three follow the same Scheduler /
    // CoopSwitch path (cooperative single-CPU); the enum exists so
    // SMP / preemption (Phase E13+) can apply per-kind policy
    // (e.g. CoreCLR threads need GC barrier at suspend, Kernel
    // threads do not).
    internal enum ThreadKind : byte
    {
        Kernel  = 0,    // Scheduler.Spawn directly (kernel C# code)
        AotApp  = 1,    // ELF-app thread (Phase E8 -- deferred)
        CoreClr = 2,    // SpawnHosted (CoreCLR CreateThread -> SharpOSHost_*)
    }

    // Phase E9.b step 102 -- separated CoreCLR binding per §3 spec.
    // Holds the "managed side" state that the kernel scheduler never
    // touches: the opaque CoreCLR Thread*, the CRT-side thread proc,
    // exit reporting, and the Join wakeup event. Non-null iff
    // Thread.Kind == CoreClr.
    //
    // Boundary convention (type-enforced once SMP-time C refactor
    // pointer-indirects WaitBlock): kernel scheduler only reads
    // Thread.{Id,State,Kind,Next,ContextBlock,Stack*,GuardPage,Teb,
    // Entry,Wait*,DeadlineTicks,Owner*}. CoreCLR-touched code only
    // reads Thread.Binding.{...}. Mixing both in one file is OK as
    // long as the call site stays on its side of the boundary.
    internal unsafe class ManagedThreadBinding
    {
        // Opaque CoreCLR Thread* (vm/threads.h). Passed back to the
        // CRT entry function as its lpParameter. Kernel NEVER
        // dereferences -- treats as void*.
        public void* ClrThreadOpaquePtr;

        // C-side thread entry from Thread::CreateNewOSThread
        // (KickOffThread or one of the runtime-internal start procs).
        // Win64 ABI: DWORD WINAPI fn(LPVOID). Null after the
        // trampoline has invoked it; left null on a CreateThread that
        // hit HandleTable exhaustion (trampoline early-exits).
        public delegate* unmanaged<void*, uint> HostedEntry;

        // Exit code returned by HostedEntry -- surfaced via
        // SharpOSHost_GetExitCodeThread (once that PAL surface lands).
        public uint HostedExitCode;

        // Joiners block on this until HasExited becomes true.
        // Manual-reset so multiple Join() calls all unblock at exit.
        public Event? JoinEvent;
        public bool HasExited;
    }

    // Phase E4 cooperative-switch thread. One CPU; no preemption.
    // Field layout is irrelevant for the switch shellcode — that
    // operates only on the ContextBlock (raw byte buffer with a fixed
    // ABI: SavedRsp at offset 0, FXSAVE area at offset 0x10).
    internal unsafe class Thread
    {
        public int Id;
        public ThreadState State;
        public ThreadKind Kind;

        // Singly-linked runnable queue. null when not enqueued.
        public Thread? Next;

        // ContextBlock layout (528 bytes, 16-byte aligned):
        //   +0x00  ulong  SavedRsp  — written by CoopSwitch on switch-out
        //   +0x08  ulong  Teb       — IA32_GS_BASE to load on switch-in
        //                              (Phase E9.b; 0 = skip gs swap)
        //   +0x10  byte[512]  FxsaveArea — fxsave/fxrstor target
        public byte* ContextBlock;

        // Stack ownership. StackBase = low VA (allocation start),
        // StackTop = high VA (StackBase + StackBytes). For the wrapped
        // boot thread, StackBase/Top are null (we don't own the boot
        // stack — UEFI/loader did the allocation).
        public byte* StackBase;
        public byte* StackTop;
        public uint StackBytes;

        // Phase E9.b step 102 -- guard page below StackBase (4 KiB,
        // marked non-present in the active page tables). A stack
        // overflow that walks past StackBase #PFs cleanly with CR2
        // inside [GuardPage, GuardPage+4096); the trap handler
        // recognises the range and halts with an identifiable
        // thread / overflow message instead of silently corrupting
        // adjacent GC heap. Null on the boot thread (UEFI stack;
        // no guard reserved by us).
        public byte* GuardPage;

        // TEB pointer. Phase E4 leaves this null on kernel-only threads
        // (no gs base swap yet); E5+ wires per-thread TEBs.
        public byte* Teb;

        // Entry function for spawned kernel threads. Null for the
        // boot-thread wrapper and for hosted threads (those use
        // Binding.HostedEntry via HostedTrampoline).
        public delegate* unmanaged<void> Entry;

        // Phase E5 / E9.c step 102 -- wait state grouped under WaitBlock
        // per docs/threading-architecture.md §3. Currently inline by
        // value (one struct slot on Thread). At E13 SMP this is the
        // exact field that gets swapped to `WaitBlock* CurrentWait`
        // pointer (stack-allocated per Wait() call frame) so the entire
        // wait state can be atomically CAS-swapped. All current
        // consumers already access via `t.Wait.X` -- the migration is
        // a one-line per call site change.
        public WaitBlock Wait;

        // Phase E7 — Process ownership. Threads owned by a Process are
        // linked via NextInProcess (head at Process.FirstThread).
        // `OwnerProcess == null` for the boot-thread wrapper and for
        // standalone test threads created without a Process.
        public Process? OwnerProcess;
        public Thread? NextInProcess;

        // Phase E9.b step 102 -- CoreCLR-side state, separated from
        // kernel scheduling state per §3. Non-null iff Kind == CoreClr;
        // kernel scheduler never reads through this pointer.
        public ManagedThreadBinding? Binding;
    }
}
