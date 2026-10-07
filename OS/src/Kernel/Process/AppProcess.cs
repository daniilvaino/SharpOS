using OS.Hal;
using OS.Kernel.Threading;

namespace OS.Kernel.Process
{
    internal enum AppProcessState : byte
    {
        Running,
        Exiting,
        Exited,
    }

    /// <summary>
    /// A running (or ended, not yet released) native app: what used to be
    /// "the current app" in a dozen statics, one record per process (step194).
    /// </summary>
    /// <remarks>
    /// Every process has an address range of its own in the one address space
    /// (<see cref="AppProcesses.ImageBaseForSlot"/>): its image is relocated
    /// there, its main stack sits at its own address, and a pointer into any
    /// process is valid from any thread, interrupt and kernel code — nothing
    /// is remapped when the CPU moves between processes.
    ///
    /// The process's threads carry its <see cref="Id"/> as their app
    /// generation: that is the holder of its pipe ends and exchange blocks.
    /// The main thread is a kernel thread of its own (not the launcher's), so
    /// a launch returns at once and the launcher may end first.
    /// </remarks>
    internal sealed unsafe class AppProcess
    {
        public uint Id;
        public int Slot;
        public string Name;
        public AppProcessState State;

        public LoadedImage Image;
        public ProcessImage Built;
        public ulong ImageBase;
        public ulong ImageEnd;

        public Thread MainThread;
        public ulong PagerCr3;

        // The app's factory of its own exception types (SetHwExceptionFactory).
        public nint HwExceptionFactory;

        // Exit service: the code the app asked to end with.
        public bool ExitRequested;
        public int RequestedExitCode;

        // The ABI the table was built for (GetAbiVersion).
        public uint AbiVersion;

        // Kill or a fatal exception in one of its threads: the code it ends with.
        public bool KillRequested;
        public int KillCode;

        // Ended other than normally: an unhandled exception or Kill. Its pipe
        // ends then say "broken", not "end of stream".
        public bool Failed;

        // HPET moments of its life (step196, where a start-and-exit goes):
        // runnable, first instruction of its code, its runtime set up (before
        // and after the build banner), its code done, its ending done, its
        // waiters woken.
        public ulong TRunnable, TEntry, TMark0, TMark1, TCodeDone, TEnded, TWoken;

        // The heap's pages (step196): bytes given, and every range given as a
        // (start, bytes) pair — what the process's end gives back.
        public ulong HeapCommitted;
        public System.Collections.Generic.List<ulong> HeapRanges;

        // Stack windows in use: bit 0 is the main thread's (step196).
        public ulong StackWindowsUsed = 1;

        /// <summary>A free stack window for a new thread, or -1 when all are taken.</summary>
        public int TakeStackWindow()
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                for (int i = 1; i < AppProcesses.StackWindows; i++)
                    if ((StackWindowsUsed & (1UL << i)) == 0)
                    {
                        StackWindowsUsed |= 1UL << i;
                        return i;
                    }
                return -1;
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        public void FreeStackWindow(int window)
        {
            OS.Kernel.Threading.Preemption.Suppress();
            StackWindowsUsed &= ~(1UL << window);
            OS.Kernel.Threading.Preemption.Allow();
        }

        // The app's namer for its exceptions (service SetExceptionNamer):
        // nint (nint exception) -> a string of the app's. Zero: none.
        public nint ExceptionNamer;

        public int ExitCode;

        // 0 while it runs, 1 once it has ended and its resources are back:
        // WaitForExit waits on this word.
        public uint ExitedWord;

        // Threads of this process not yet off the machine, the main one
        // included; ending waits for it to reach 1.
        public int LiveThreads;

        // Who started it, and whether that one still holds the record.
        public uint LauncherId;
        public bool LauncherHolds;
    }

    /// <summary>The process table: address ranges, records, lookups by thread and by address.</summary>
    internal static unsafe class AppProcesses
    {
        /// <summary>Processes running at once. Start past it is refused.</summary>
        public const int MaxRunning = 16;

        // Image ranges: 1 GiB each from 32 TiB up — beyond any RAM the
        // kernel maps one to one, below the VM window (80 TiB) and the service
        // thunks (112 TiB). They were at 4 GiB until the first run on the
        // laptop: with 7 GiB of RAM the firmware had put the kernel itself at
        // 0x1_4000_0000, and the shell's first child was loaded over it.
        // An image with its 64 MiB heap pool takes about 70 MiB of a range.
        public const ulong ImageRegionBase = 0x0000200000000000UL;
        public const ulong ImageRegionStride = 0x0000000040000000UL;

        public static ulong ImageBaseForSlot(int slot) => ImageRegionBase + (ulong)slot * ImageRegionStride;

        // A slot, from its base (step196):
        //   [0, 64 MiB)        the image
        //   [64, 704 MiB)      the heap's addresses, pages given as it grows
        //   [768 MiB, 1 GiB)   thread stacks, one 4 MiB window each: the stack
        //                      at the window's top, nothing mapped below it.
        // The gap below a stack is the guard: compiled code does not probe a
        // big frame page by page (__chkstk is a no-op here), so a single guard
        // page would be jumped over; 3 MiB of nothing is not.
        public const ulong HeapRegionOffset = 64UL << 20;
        public const ulong HeapRegionBytes = 640UL << 20;

        /// <summary>Pages an app's heap may hold at once: the old pool's 64 MiB.</summary>
        public const ulong HeapBudgetBytes = 64UL << 20;
        public const ulong StackWindowBytes = 4UL << 20;
        public const int StackWindows = 64;
        public const ulong MainStackBytes = 1UL << 20;
        public const ulong WorkerStackBytes = 256UL << 10;

        /// <summary>The top of a stack window of a slot; window 0 is the main thread's.</summary>
        public static ulong StackTopForSlot(int slot, int window = 0)
            => ImageBaseForSlot(slot) + ImageRegionStride - (ulong)window * StackWindowBytes;

        // Made by the first start, not by a class constructor: FindByAddress
        // is asked from interrupt handlers, where nothing may be allocated.
        private static AppProcess[] s_slots;

        // Every record not yet released: running, or ended and still held by
        // its launcher.
        private static System.Collections.Generic.List<AppProcess> s_records;

        private static uint s_nextId;

        public static int Running
        {
            get
            {
                int n = 0;
                for (int i = 0; i < MaxRunning && s_slots != null; i++) if (s_slots[i] != null) n++;
                return n;
            }
        }

        public static int Records => s_records?.Count ?? 0;

        /// <summary>Pages the heaps of the running processes hold.</summary>
        public static ulong RunningHeapPages()
        {
            ulong bytes = 0;
            for (int i = 0; i < MaxRunning && s_slots != null; i++)
                if (s_slots[i] != null) bytes += s_slots[i].HeapCommitted;
            return bytes / 4096;
        }

        /// <summary>A free slot and an id, or -1 when <see cref="MaxRunning"/> are running.</summary>
        public static int TryReserve(AppProcess process)
        {
            s_slots ??= new AppProcess[MaxRunning];
            s_records ??= new System.Collections.Generic.List<AppProcess>();
            Preemption.Suppress();
            try
            {
                for (int i = 0; i < MaxRunning; i++)
                {
                    if (s_slots[i] != null) continue;
                    s_slots[i] = process;
                    process.Slot = i;
                    process.Id = ++s_nextId;
                    s_records.Add(process);
                    return i;
                }
                return -1;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>Gives the address range back (the process has ended or never started).</summary>
        public static void FreeSlot(AppProcess process)
        {
            Preemption.Suppress();
            if (s_slots != null && process.Slot >= 0 && process.Slot < MaxRunning && s_slots[process.Slot] == process)
                s_slots[process.Slot] = null;
            Preemption.Allow();
        }

        /// <summary>Drops the record: nobody will ask about it again.</summary>
        public static void Forget(AppProcess process)
        {
            Preemption.Suppress();
            s_records?.Remove(process);
            Preemption.Allow();
        }

        public static AppProcess Find(uint id)
        {
            Preemption.Suppress();
            try
            {
                if (s_records != null)
                    for (int i = 0; i < s_records.Count; i++)
                        if (s_records[i].Id == id) return s_records[i];
                return null;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>The process whose image holds the address, or null. Safe from an interrupt.</summary>
        public static AppProcess FindByAddress(ulong address)
        {
            AppProcess[] slots = s_slots;
            if (slots == null || address < ImageRegionBase) return null;
            ulong slot = (address - ImageRegionBase) / ImageRegionStride;
            if (slot >= MaxRunning) return null;
            AppProcess p = slots[slot];
            return p != null && address >= p.ImageBase && address < p.ImageEnd ? p : null;
        }

        /// <summary>The process the current thread belongs to, or null for the kernel's own threads.</summary>
        public static AppProcess Current => Scheduler.Current?.App;

        /// <summary>
        /// The launcher has ended: the records of its ended children go, and
        /// those still running free their own when they end.
        /// </summary>
        public static void OnLauncherEnded(uint launcherId)
        {
            Preemption.Suppress();
            try
            {
                if (s_records == null) return;
                for (int i = s_records.Count - 1; i >= 0; i--)
                {
                    AppProcess p = s_records[i];
                    if (p.LauncherId != launcherId || !p.LauncherHolds) continue;
                    p.LauncherHolds = false;
                    if (p.State == AppProcessState.Exited) s_records.RemoveAt(i);
                }
            }
            finally { Preemption.Allow(); }
        }
    }
}
