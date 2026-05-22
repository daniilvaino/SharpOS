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

    // Phase E4 cooperative-switch thread. One CPU; no preemption.
    // Field layout is irrelevant for the switch shellcode — that
    // operates only on the ContextBlock (raw byte buffer with a fixed
    // ABI: SavedRsp at offset 0, FXSAVE area at offset 0x10).
    internal unsafe class Thread
    {
        public int Id;
        public ThreadState State;

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

        // TEB pointer. Phase E4 leaves this null on kernel-only threads
        // (no gs base swap yet); E5+ wires per-thread TEBs.
        public byte* Teb;

        // Entry function for spawned threads. Null for the boot-thread
        // wrapper.
        public delegate* unmanaged<void> Entry;

        // Phase E5 — wait/timer linkage. A thread is on AT MOST ONE
        // wait list at a time (either TimerQueue or one Event/Semaphore
        // wait queue), so two single-linked next-pointers are enough:
        //
        //   TimerNext      — next entry in TimerQueue (sorted by Deadline).
        //   WaitNext       — next entry on an Event/Semaphore wait list.
        //   DeadlineTicks  — HPET tick value at which Sleep should expire.
        //                    0 means no deadline (pure wait).
        public Thread? TimerNext;
        public Thread? WaitNext;
        public ulong DeadlineTicks;

        // Phase E7 — Process ownership. Threads owned by a Process are
        // linked via NextInProcess (head at Process.FirstThread).
        // `OwnerProcess == null` for the boot-thread wrapper and for
        // standalone test threads created without a Process.
        public Process? OwnerProcess;
        public Thread? NextInProcess;

        // Phase E9 — hosted thread state. When a thread is spawned via
        // SharpOSHost_CreateThread (Win32-style PAL bridge), the entry
        // signature is `uint(void*)` rather than our `void()` Scheduler
        // ABI. A managed trampoline reads these fields and invokes the
        // real entry. Null on threads spawned directly via Scheduler.Spawn.
        public delegate* unmanaged<void*, uint> HostedEntry;
        public void* HostedParam;
        public uint HostedExitCode;

        // Phase E9 — Win32 thread handle bookkeeping. The Thread object's
        // identity in HandleTable plus a Join-waiters event so
        // WaitForSingleObject can block until ExitThread.
        public Event? JoinEvent;
        public bool HasExited;

        // Phase E9.c — WaitOnAddress state. When this thread is blocked
        // in `SharpOSHost_WaitOnAddress`, `WaitAddress` holds the user-
        // memory address it's parked on (used by WakeByAddress* to
        // match the bucket). Null when not in a WaitOnAddress wait.
        // The thread can be on AT MOST ONE wait list at a time
        // (TimerQueue OR Event/Semaphore/Mutex OR WaitOnAddress).
        public void* WaitAddress;
    }
}
