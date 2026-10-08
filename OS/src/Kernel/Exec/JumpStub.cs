using OS.Hal;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Exec
{
    internal static unsafe partial class JumpStub
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const uint StubPageSize = 4096;

        // Where the abort entry sits in the stub page. The main stub is a few
        // hundred bytes; the gap is checked at emit time.
        private const int AbortEntryOffset = 0x800;

        private static bool s_initialized;
        private static delegate* unmanaged<JumpContext*, int> s_jump;
        private static delegate* unmanaged<JumpContext*, int, void> s_abort;

        private static ulong s_stubVirtualAddress;
        private static ulong s_stubPhysicalAddress;

        // EfiLoaderCode buffer provided by the bootloader.
        // Firmware CR3 maps EfiLoaderCode as executable; EfiConventionalMemory is NX on real hardware.
        private static void* s_execBuffer;
        private static uint s_execBufferSize;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            s_execBuffer = buffer;
            s_execBufferSize = size;
        }

        public static bool EnsureInitialized()
        {
            if (s_initialized)
                return true;

            // Phase E1 note: pre-E1 this method ran on firmware CR3 and the
            // guard `if (IsPagerRootActive()) return false;` was a stale
            // defensive check (IsPagerRootActive was never true before E1).
            // Post-E1 kernel CR3 == pager root by design; the EfiLoaderCode
            // buffer is still mapped executable in the clone (deep-copied
            // from firmware), so TryAllocFromExecBuffer still succeeds. The
            // guard would now block every app launch — removed.
            return TryInitialize();
        }

        public static bool TryGetAddress(out ulong virtualAddress)
        {
            virtualAddress = s_stubVirtualAddress;
            return s_initialized && virtualAddress != 0;
        }

        /// <summary>True when the address is inside the jump stub's page.</summary>
        /// <remarks>
        /// Asked by stack walks. The stub switches CR3 and RSP and has no
        /// unwind data by construction, so a walk that reaches it cannot step
        /// past it — but reaching it is a normal end, not a failure, and the
        /// two were indistinguishable in the counters until this existed.
        ///
        /// The whole page, not the emitted length: the page is zeroed and
        /// holds nothing else.
        /// </remarks>
        public static bool ContainsAddress(ulong address)
            => s_initialized && s_stubVirtualAddress != 0
               && address >= s_stubVirtualAddress
               && address < s_stubVirtualAddress + StubPageSize;

        public static bool Run(
            ulong entryVirtualAddress,
            ulong stackTopVirtualAddress,
            ulong startupBlockVirtualAddress,
            ulong pagerCr3,
            out int exitCode,
            ulong appStackBase = 0,
            ulong imageBase = 0,
            ulong imageEnd = 0)
        {
            exitCode = 0;

            if (!s_initialized)
                return false;

            if ((pagerCr3 & 0x000FFFFFFFFFF000UL) == 0)
                return false;

            if (!Pager.TryQuery(s_stubVirtualAddress, out _, out PageFlags stubFlags))
                return false;

            if ((stubFlags & PageFlags.NoExecute) == PageFlags.NoExecute)
                return false;

            JumpContext context = default;
            context.Entry = entryVirtualAddress;
            context.StackTop = stackTopVirtualAddress;
            context.Startup = startupBlockVirtualAddress;
            context.Cr3 = pagerCr3;
            // The app's image bounds, for telling its faults from the kernel's.
            // Readable by virtual address only under the pager root; without it
            // the bounds stay empty and every fault keeps the kernel's type.
            if (startupBlockVirtualAddress != 0 && Pager.IsPagerRootActive())
            {
                OS.Kernel.Process.ProcessStartupBlock* startup = (OS.Kernel.Process.ProcessStartupBlock*)startupBlockVirtualAddress;
                context.ImageBase = startup->ImageBase;
                context.ImageEnd = startup->ImageEnd;
                context.AppStackBase = startup->StackBase;
            }
            else
            {
                // A worker thread of a process (step196): no startup block,
                // the bounds are given.
                context.ImageBase = imageBase;
                context.ImageEnd = imageEnd;
                context.AppStackBase = appStackBase;
            }
            OS.Kernel.Threading.Thread? self = OS.Kernel.Threading.Scheduler.Current;
            context.OwnerThreadId = self?.Id ?? 0;
            context.PreemptionDepth = OS.Kernel.Threading.Preemption.Depth;
            context.ExInfoHead = OS.Boot.EH.ExInfoHead.s_head;
            // Per thread (step194): each process's main thread is in a run of
            // its own, and they interleave.
            if (self != null)
            {
                context.Previous = self.Jump;
                self.Jump = &context;
            }

            exitCode = s_jump(&context);

            if (self != null) self.Jump = context.Previous;
            return true;
        }

        /// <summary>True when the address is code of a running app — any of them.</summary>
        public static bool IsAppCode(ulong rip) => OS.Kernel.Process.AppProcesses.FindByAddress(rip) != null;

        // Kinds of hardware-fault exception an app's factory makes.
        public const int HwExceptionNullReference = 0;
        public const int HwExceptionAccessViolation = 1;
        public const int HwExceptionDivideByZero = 2;
        public const int HwExceptionRegionReference = 3;

        /// <summary>The calling app registers its factory (service SetHwExceptionFactory).</summary>
        public static void SetHwExceptionFactory(nint factory)
        {
            OS.Kernel.Process.AppProcess? p = OS.Kernel.Process.AppProcesses.Current;
            if (p != null) p.HwExceptionFactory = factory;
        }

        /// <summary>
        /// An exception of the app's own type for a fault at <paramref name="rip"/>,
        /// when that address is in the image of a running app.
        /// </summary>
        /// <remarks>
        /// Only for faults in the app's code. A fault in kernel code — a service
        /// the app called included — keeps the kernel's type, which is what the
        /// kernel's own catch clauses match. The address says whose code it was:
        /// every process has its own range.
        /// </remarks>
        public static bool TryCreateAppHwException(int kind, ulong rip, out object exception)
        {
            exception = null;
            OS.Kernel.Process.AppProcess? p = OS.Kernel.Process.AppProcesses.FindByAddress(rip);
            return p != null && TryCreateException(p, kind, out exception);
        }

        /// <summary>
        /// An exception of the calling app's own type, for a failure the
        /// caller already attributed to the app.
        /// </summary>
        public static bool TryCreateAppException(int kind, out object exception)
        {
            exception = null;
            OS.Kernel.Process.AppProcess? p = OS.Kernel.Process.AppProcesses.Current;
            return p != null && TryCreateException(p, kind, out exception);
        }

        private static bool TryCreateException(OS.Kernel.Process.AppProcess p, int kind, out object exception)
        {
            exception = null;
            if (p.HwExceptionFactory == 0)
                return false;

            nint created = ((delegate* unmanaged<int, nint>)p.HwExceptionFactory)(kind);
            if (created == 0)
                return false;
            exception = System.Runtime.CompilerServices.Unsafe.As<nint, object>(ref created);
            return true;
        }

        /// <summary>Exit code of an app ended by an unhandled exception.</summary>
        /// <remarks>134 is what an app's own <c>Fatal</c> reports (SIGABRT, as .NET on Linux).</remarks>
        public const int UnhandledExitCode = 134;

        /// <summary>
        /// Ends the process of the thread that raised an unhandled exception,
        /// with <see cref="UnhandledExitCode"/>. Does not return when it succeeds.
        /// </summary>
        /// <remarks>
        /// The main thread leaves its run at once and ends the process in the
        /// <see cref="Run"/> that started it. Any other thread of the process
        /// asks for the process to end and leaves the machine; the main thread
        /// follows when it is next in the app's code or out of a wait (step194:
        /// a worker's exception no longer stops the machine). False for a
        /// kernel thread, or inside an interrupt handler: the caller panics.
        ///
        /// Restored here, not in the stub: the exception-info chain (it points
        /// into the app's stack, which is being abandoned) and the preemption
        /// depth (the app may have died inside a service that suppressed it).
        /// </remarks>
        public static bool TryAbortCurrentApp()
        {
            OS.Kernel.Threading.Thread? self = OS.Kernel.Threading.Scheduler.Current;
            OS.Kernel.Process.AppProcess? p = self?.App;
            if (p == null || s_abort == null)
                return false;

            // Thrown inside an interrupt handler: a kernel fault, not the app's,
            // and the interrupt may not be acknowledged yet — ending the app
            // here would leave the timer silent (seen 2026-10-02: the sampler's
            // thread dump faulted and took the launcher down with 134).
            if (self.InterruptDepth != 0)
                return false;

            // Being ended already (Kill, an exit, another thread's failure):
            // the exception is what its cut-short wait made of it. It leaves
            // with the code the process ends with.
            if (self.KillRequested)
                LeaveApp(self, p.KillCode);

            // The app's own code for it (step197, asked in the report): 141
            // for a broken standard end, said nothing about.
            int code = self.UnhandledExitCode != 0 ? self.UnhandledExitCode : UnhandledExitCode;
            self.UnhandledExitCode = 0;
            if (code != OS.Boot.EH.UnhandledExceptionReport.QuietExitCode)
            {
                Console.Write("[app] ");
                Console.Write(p.Name);
                Console.Write(": unhandled exception");
                Console.Write(self == p.MainThread ? "" : " on a worker thread");
                Console.Write(": process ended, exit code ");
                Console.WriteUInt((uint)code);
                Console.WriteLine("");
            }

            OS.Kernel.Process.AppServiceBuilder.RequestEnd(p, code, failed: true);
            LeaveApp(self, code);
            return false;   // not reached
        }

        /// <summary>
        /// The current thread of an app leaves the machine: the main thread
        /// through its run's abort entry (the kernel resumes after
        /// <see cref="Run"/>), any other thread by exiting. Does not return.
        /// </summary>
        internal static void LeaveApp(OS.Kernel.Threading.Thread self, int exitCode)
        {
            // Every app thread is inside a run since step196 — workers too —
            // and leaves through its abort, back onto its kernel stack.
            JumpContext* context = self.Jump;
            if (context != null)
            {
                OS.Boot.EH.ExInfoHead.s_head = context->ExInfoHead;
                OS.Kernel.Threading.Preemption.RestoreDepth(context->PreemptionDepth);
                s_abort(context, exitCode);
            }
            OS.Boot.EH.ExInfoHead.s_head = System.IntPtr.Zero;
            OS.Kernel.Threading.Preemption.RestoreDepth(0);
            OS.Kernel.Process.AppServiceBuilder.OnAppThreadGone(self.App);
            OS.Kernel.Threading.Scheduler.Exit();
        }

        /// <summary>
        /// A double fault on an app thread whose stack pointer is below its
        /// app stack, in the window's gap: the stack overflowed (step196). The
        /// process ends with 134; the thread is turned to leave on its kernel
        /// stack, which the overflow did not touch. False for anything else —
        /// the caller panics.
        /// </summary>
        /// <remarks>
        /// Reached on the double fault's own stack (Tss, IST1): the #PF that
        /// found the stack gone could not push its frame there.
        /// </remarks>
        public static bool TryRecoverStackOverflow(OS.Hal.Idt.InterruptFrame* frame)
        {
            OS.Kernel.Threading.Thread? self = OS.Kernel.Threading.Scheduler.Current;
            OS.Kernel.Process.AppProcess? p = self?.App;
            JumpContext* context = self == null ? null : self.Jump;
            if (p == null || context == null || context->KernelRsp == 0 || context->AppStackBase == 0)
                return false;
            ulong rsp = frame->Rsp;
            if (rsp >= context->AppStackBase || context->AppStackBase - rsp > OS.Kernel.Process.AppProcesses.StackWindowBytes)
                return false;

            Console.Write("[app] ");
            Console.Write(p.Name);
            Console.Write(": stack overflow on thread ");
            Console.WriteUInt((uint)self.Id);
            Console.Write(" (rsp=0x");
            Console.WriteHex(rsp);
            Console.Write(", stack from 0x");
            Console.WriteHex(context->AppStackBase);
            Console.Write("): process ended, exit code ");
            Console.WriteUInt(UnhandledExitCode);
            Console.WriteLine("");

            OS.Kernel.Process.AppServiceBuilder.RequestEnd(p, UnhandledExitCode, failed: true);
            delegate* unmanaged<void> leave = &LeaveOverflowed;
            frame->Rip = (ulong)leave;
            frame->Rsp = ((context->KernelRsp - 0x1000) & ~0xFUL) - 8;   // as after a call, below the run's frame
            return true;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void LeaveOverflowed()
        {
            OS.Kernel.Threading.Thread self = OS.Kernel.Threading.Scheduler.Current;
            LeaveApp(self, UnhandledExitCode);
        }

        private static bool TryInitialize()
        {
            if (!TryAllocFromExecBuffer(out ulong stubPhysical, out ulong stubVirtual) &&
                !TryAllocFromPhysicalMemory(out stubPhysical, out stubVirtual))
                return false;

            OS.Kernel.Util.Memory.Zero((void*)stubVirtual, StubPageSize);

            byte* dst = (byte*)stubVirtual;
            int icedLen = EmitStubIced(dst, AbortEntryOffset);
            int abortLen = EmitAbortIced(dst + AbortEntryOffset, (int)StubPageSize - AbortEntryOffset);

            Console.Write("[stub] jumpstub len=0x");
            Console.WriteHex((ulong)icedLen);
            Console.Write(" abort len=0x");
            Console.WriteHex((ulong)abortLen);
            Console.WriteLine("");

            // The writer drops bytes past its capacity: a stub that grew into
            // the abort entry would run off the end of its own code.
            if (icedLen >= AbortEntryOffset || abortLen >= (int)StubPageSize - AbortEntryOffset)
                return false;

            s_stubPhysicalAddress = stubPhysical;
            s_stubVirtualAddress = stubVirtual;
            s_jump = (delegate* unmanaged<JumpContext*, int>)stubVirtual;
            s_abort = (delegate* unmanaged<JumpContext*, int, void>)(stubVirtual + AbortEntryOffset);
            s_initialized = true;
            return true;
        }

        // Preferred: use EfiLoaderCode buffer from bootloader.
        // Firmware CR3 maps it executable — no NX fault when s_jump() is called
        // before the CR3 switch to the pager root.
        private static bool TryAllocFromExecBuffer(out ulong stubPhysical, out ulong stubVirtual)
        {
            stubPhysical = 0;
            stubVirtual = 0;

            if (s_execBuffer == null || s_execBufferSize < StubPageSize)
                return false;

            ulong addr = (ulong)s_execBuffer;
            if ((addr & (PageSize - 1)) != 0)
                return false;   // bootloader must provide page-aligned address

            stubPhysical = addr;
            stubVirtual = addr;

            // The pager clones the firmware CR3, so EfiLoaderCode pages are already mapped.
            if (Pager.TryQuery(stubVirtual, out ulong mappedPhysical, out _))
            {
                // Already in pager (expected). Verify it maps to our physical page.
                if ((mappedPhysical & ~(PageSize - 1)) != stubPhysical)
                    return false;
            }
            else
            {
                // Not cloned for some reason — map it explicitly.
                if (!Pager.Map(stubVirtual, stubPhysical, PageFlags.Writable))
                    return false;
            }

            return true;
        }

        // Fallback: allocate from EfiConventionalMemory.
        // Works on QEMU/OVMF without strict NX policy; fails on real hardware (INSYDE NX).
        private static bool TryAllocFromPhysicalMemory(out ulong stubPhysical, out ulong stubVirtual)
        {
            stubPhysical = 0;
            stubVirtual = 0;

            ulong page = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (page == 0 || (page & (PageSize - 1)) != 0)
                return false;

            stubPhysical = page;
            stubVirtual = page;

            if (Pager.TryQuery(stubVirtual, out ulong mappedPhysical, out _))
            {
                if ((mappedPhysical & ~(PageSize - 1)) != stubPhysical)
                    return false;
            }
            else if (!Pager.Map(stubVirtual, stubPhysical, PageFlags.Writable))
            {
                return false;
            }

            return true;
        }

    }

    /// <summary>
    /// One app run: what the stub needs to enter the app, and everything it
    /// needs to give the kernel back, on the normal return and on an abort.
    /// </summary>
    /// <remarks>
    /// Lives on the kernel stack in <see cref="JumpStub.Run"/>, mapped under
    /// both CR3s. The emitted stub reads the offsets below; the fields after
    /// the saved registers are the kernel's own.
    /// </remarks>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit, Size = 0x150)]
    internal unsafe struct JumpContext
    {
        public const int EntryOffset = 0x00;
        public const int StackTopOffset = 0x08;
        public const int StartupOffset = 0x10;
        public const int Cr3Offset = 0x18;
        public const int KernelRspOffset = 0x20;
        public const int KernelCr3Offset = 0x28;
        public const int RflagsOffset = 0x30;
        public const int RbxOffset = 0x38;
        public const int RbpOffset = 0x40;
        public const int RsiOffset = 0x48;
        public const int RdiOffset = 0x50;
        public const int R12Offset = 0x58;
        public const int R13Offset = 0x60;
        public const int R14Offset = 0x68;
        public const int R15Offset = 0x70;
        public const int XmmOffset = 0x78;      // xmm6..xmm15, 16 bytes each, unaligned: up to 0x118

        [System.Runtime.InteropServices.FieldOffset(EntryOffset)] public ulong Entry;
        [System.Runtime.InteropServices.FieldOffset(StackTopOffset)] public ulong StackTop;
        [System.Runtime.InteropServices.FieldOffset(StartupOffset)] public ulong Startup;
        [System.Runtime.InteropServices.FieldOffset(Cr3Offset)] public ulong Cr3;

        // Written by the stub on entry; read by the collector to walk the
        // kernel frames under the app (KernelGcPreciseWalk.ContinueBelowApps).
        [System.Runtime.InteropServices.FieldOffset(KernelRspOffset)] public ulong KernelRsp;
        [System.Runtime.InteropServices.FieldOffset(RbxOffset)] public ulong Rbx;
        [System.Runtime.InteropServices.FieldOffset(RbpOffset)] public ulong Rbp;
        [System.Runtime.InteropServices.FieldOffset(RsiOffset)] public ulong Rsi;
        [System.Runtime.InteropServices.FieldOffset(RdiOffset)] public ulong Rdi;
        [System.Runtime.InteropServices.FieldOffset(R12Offset)] public ulong R12;
        [System.Runtime.InteropServices.FieldOffset(R13Offset)] public ulong R13;
        [System.Runtime.InteropServices.FieldOffset(R14Offset)] public ulong R14;
        [System.Runtime.InteropServices.FieldOffset(R15Offset)] public ulong R15;

        [System.Runtime.InteropServices.FieldOffset(0x118)] public JumpContext* Previous;
        [System.Runtime.InteropServices.FieldOffset(0x120)] public System.IntPtr ExInfoHead;
        [System.Runtime.InteropServices.FieldOffset(0x128)] public int OwnerThreadId;
        [System.Runtime.InteropServices.FieldOffset(0x12C)] public uint PreemptionDepth;
        [System.Runtime.InteropServices.FieldOffset(0x130)] public ulong ImageBase;
        [System.Runtime.InteropServices.FieldOffset(0x138)] public ulong ImageEnd;
        [System.Runtime.InteropServices.FieldOffset(0x140)] public nint HwExceptionFactory;

        // The app's stack, [AppStackBase, StackTop): where the thread that runs
        // the app is when it is preempted in app code, for the collector's
        // word-by-word scan of an interrupted thread.
        [System.Runtime.InteropServices.FieldOffset(0x148)] public ulong AppStackBase;
    }
}
