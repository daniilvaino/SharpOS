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

        // Innermost app run in progress. Runs nest (an app launching an app),
        // and each context lives on the frame of the Run that owns it.
        private static JumpContext* s_active;
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
            out int exitCode)
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
            if (Pager.IsPagerRootActive())
            {
                OS.Kernel.Process.ProcessStartupBlock* startup = (OS.Kernel.Process.ProcessStartupBlock*)startupBlockVirtualAddress;
                context.ImageBase = startup->ImageBase;
                context.ImageEnd = startup->ImageEnd;
                context.AppStackBase = startup->StackBase;
            }
            context.OwnerThreadId = OS.Kernel.Threading.Scheduler.Current?.Id ?? 0;
            context.PreemptionDepth = OS.Kernel.Threading.Preemption.Depth;
            context.ExInfoHead = OS.Boot.EH.ExInfoHead.s_head;
            context.Previous = s_active;
            s_active = &context;

            exitCode = s_jump(&context);

            s_active = context.Previous;
            return true;
        }

        /// <summary>True when the address is code of the app now running.</summary>
        public static bool IsAppCode(ulong rip)
        {
            JumpContext* context = s_active;
            return context != null && rip >= context->ImageBase && rip < context->ImageEnd;
        }

        /// <summary>The innermost app run in progress; older ones through <see cref="JumpContext.Previous"/>.</summary>
        internal static JumpContext* Innermost => s_active;

        // Kinds of hardware-fault exception an app's factory makes.
        public const int HwExceptionNullReference = 0;
        public const int HwExceptionAccessViolation = 1;
        public const int HwExceptionDivideByZero = 2;

        /// <summary>The innermost app registers its factory (service SetHwExceptionFactory).</summary>
        public static void SetHwExceptionFactory(nint factory)
        {
            if (s_active != null)
                s_active->HwExceptionFactory = factory;
        }

        /// <summary>
        /// An exception of the app's own type for a fault at <paramref name="rip"/>,
        /// when that address is in the image of the app now running.
        /// </summary>
        /// <remarks>
        /// Only for faults in the app's code. A fault in kernel code — a service
        /// the app called included — keeps the kernel's type, which is what the
        /// kernel's own catch clauses match. Any thread qualifies, not just the
        /// one that launched the app: the address alone says whose code it was,
        /// because only the innermost app's image is mapped while it runs.
        /// </remarks>
        public static bool TryCreateAppHwException(int kind, ulong rip, out object exception)
        {
            exception = null;
            JumpContext* context = s_active;
            if (context == null || context->HwExceptionFactory == 0)
                return false;
            if (rip < context->ImageBase || rip >= context->ImageEnd)
                return false;

            nint created = ((delegate* unmanaged<int, nint>)context->HwExceptionFactory)(kind);
            if (created == 0)
                return false;
            exception = System.Runtime.CompilerServices.Unsafe.As<nint, object>(ref created);
            return true;
        }

        /// <summary>Exit code of an app ended by an unhandled exception.</summary>
        /// <remarks>134 is what an app's own <c>Fatal</c> reports (SIGABRT, as .NET on Linux).</remarks>
        public const int UnhandledExitCode = 134;

        /// <summary>
        /// Ends the app this thread is running and resumes the kernel in the
        /// <see cref="Run"/> that started it, as if the app had returned
        /// <see cref="UnhandledExitCode"/>. Does not return when it succeeds.
        /// </summary>
        /// <remarks>
        /// Called from the unhandled-exception path. The exception was reported
        /// already; what is left is to put back what the dead app was holding
        /// and leave its stack. False when this thread is not running an app:
        /// a kernel thread, or an app's worker thread, whose death the kernel
        /// cannot yet turn into the end of its process. The caller then panics
        /// as before.
        ///
        /// Restored here, not in the stub: the exception-info chain (it points
        /// into the app's stack, which is being abandoned) and the preemption
        /// depth (the app may have died inside a service that suppressed it).
        /// Locks a service held at that moment are not released; nothing
        /// records them.
        /// </remarks>
        public static bool TryAbortCurrentApp()
        {
            JumpContext* context = s_active;
            if (context == null || s_abort == null)
                return false;

            // Thrown inside an interrupt handler: a kernel fault, not the app's,
            // and the interrupt may not be acknowledged yet — ending the app
            // here would leave the timer silent (seen 2026-10-02: the sampler's
            // thread dump faulted and took the launcher down with 134).
            if ((OS.Kernel.Threading.Scheduler.Current?.InterruptDepth ?? 0) != 0)
                return false;

            int thread = OS.Kernel.Threading.Scheduler.Current?.Id ?? 0;
            if (thread != context->OwnerThreadId)
            {
                Console.WriteLine("[app] unhandled exception on an app worker thread: only the main thread can end the app yet");
                return false;
            }

            OS.Boot.EH.ExInfoHead.s_head = context->ExInfoHead;
            OS.Kernel.Threading.Preemption.RestoreDepth(context->PreemptionDepth);

            Console.Write("[app] unhandled exception: app ended, exit code ");
            Console.WriteUInt(UnhandledExitCode);
            Console.WriteLine("");

            s_abort(context, UnhandledExitCode);
            return false;   // not reached
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
