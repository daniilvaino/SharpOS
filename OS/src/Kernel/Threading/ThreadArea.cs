using OS.Hal;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Threading
{
    // What a thread keeps of its own, on its context page past SavedRsp, the
    // TEB pointer and the FXSAVE area (up to 0x210):
    //
    //   +0x600  its copy of the kernel image's .tls (CoreCLR's thread_locals),
    //           slot 0 — the image's _tls_index
    //   +0x800  a TEB: NT_TIB stack bounds, Self, ThreadLocalStoragePointer
    //   +0xC00  its TLS slot array (64 slots)
    //   +0xE00  the thread's words: [0] the kernel's thread-static storage,
    //           [1] the app's (each an object[][] in its own tier's heap)
    //
    // Every thread gets one, so gs always points at the running thread's TEB:
    // CoopSwitch loads GS_BASE from ContextBlock+0x08 on every switch. Before
    // step209 only CoreCLR threads had a TEB, and every other thread ran on
    // whichever GS the last one left: CoreCLR code reached from such a thread
    // (the SEH walk asking for the Frame chain) read another thread's
    // thread_locals. With a .tls copy of its own a thread is, to CoreCLR, a
    // native thread it has not met.
    //
    // A CoreCLR thread swaps in a full TEB (CoreClrTeb) whose slot array
    // carries the same pointer to the words, so the storage does not move
    // when a thread starts running hosted code.
    //
    // Thread statics find the words through gs: ReadWords is
    // mov rax, gs:[0x58]; mov rax, [rax + 62*8]. The words go with the page
    // when the thread is reaped; until then each collector roots its own.
    internal static unsafe class ThreadArea
    {
        public const uint TlsBlockOffset = 0x600;
        public const uint TebOffset = 0x800;
        public const uint SlotsOffset = 0xC00;
        public const uint WordsOffset = 0xE00;

        /// <summary>The TLS slot that points at the thread's words (X64Asm.ThreadWords reads it).</summary>
        public const int WordsSlot = 62;

        public const int KernelWord = 0;
        public const int AppWord = 1;

        /// <summary>Lays out the TEB, slots and words on a fresh context page and makes the TEB the thread's.</summary>
        public static void Init(byte* ctx, ulong stackTop, ulong stackLimit)
        {
            OS.Kernel.Util.Memory.Zero(ctx + TlsBlockOffset, 0x1000 - TlsBlockOffset);

            byte* teb = ctx + TebOffset;
            *(ulong*)(teb + CoreClrTeb.OffsetStackBase) = stackTop;
            *(ulong*)(teb + CoreClrTeb.OffsetStackLimit) = stackLimit;
            *(ulong*)(teb + CoreClrTeb.OffsetSelf) = (ulong)teb;
            *(ulong*)(teb + CoreClrTeb.OffsetTlsPointer) = (ulong)(ctx + SlotsOffset);
            ((ulong*)(ctx + SlotsOffset))[WordsSlot] = (ulong)(ctx + WordsOffset);

            // Slot 0 stays empty until the image's TLS directory can be read
            // (the boot thread, early probes); HasClrTls tells.
            if (CoreClrTeb.CopyTemplate(ctx + TlsBlockOffset, TebOffset - TlsBlockOffset))
                ((ulong*)(ctx + SlotsOffset))[0] = (ulong)(ctx + TlsBlockOffset);

            *(ulong*)(ctx + 0x08) = (ulong)teb;
        }

        /// <summary>
        /// Whether CoreCLR's thread_locals can be read on this thread: its
        /// active TEB has a block in slot 0 (ThreadArea's copy or a CoreCLR TEB).
        /// </summary>
        public static bool HasClrTls(Thread t)
        {
            if (t.ContextBlock == null) return false;
            byte* teb = *(byte**)(t.ContextBlock + 0x08);
            if (teb == null) return false;
            ulong* slots = *(ulong**)(teb + CoreClrTeb.OffsetTlsPointer);
            return slots != null && slots[0] != 0;
        }

        /// <summary>The thread's words, or null once its context page is gone.</summary>
        public static nint* Words(Thread t)
            => t.ContextBlock == null ? null : (nint*)(t.ContextBlock + WordsOffset);

        /// <summary>
        /// Gives every thread its words from here on: the boot thread's TEB
        /// goes into GS, thread statics learn where to look, the kernel's
        /// collector learns to root them.
        /// </summary>
        public static bool Activate(Thread boot)
        {
            if (!X64Asm.WriteGsBaseMsr(*(ulong*)(boot.ContextBlock + 0x08)))
                return false;
            void* read = X64Asm.ThreadWordsStub();
            if (read == null) return false;

            GcRoots.ExtraRoots = &MarkKernelWords;
            Internal.Runtime.ThreadStatics.Install((delegate*<nint*>)read, KernelWord);
            return true;
        }

        // The kernel's collector: every thread's kernel word.
        private static void MarkKernelWords()
        {
            for (Thread? t = Scheduler.AllThreads; t != null; t = t.AllNext)
            {
                nint* words = Words(t);
                if (words != null && words[KernelWord] != 0)
                    GcMark.MarkFromRoot(words[KernelWord]);
            }
        }

        /// <summary>An app's collector: the app words of its own process's threads.</summary>
        public static void MarkAppWords(delegate* unmanaged<nuint, void> markRoot)
        {
            OS.Kernel.Process.AppProcess? app = Scheduler.Current?.App;
            if (app == null) return;
            for (Thread? t = Scheduler.AllThreads; t != null; t = t.AllNext)
            {
                if (t.App != app) continue;
                nint* words = Words(t);
                if (words != null && words[AppWord] != 0)
                    markRoot((nuint)words[AppWord]);
            }
        }
    }
}
