using OS.Hal;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Threading
{
    // Per-thread environment block (TEB) facade — CoreCLR-compatible layout.
    // Phase E2: factor TEB allocation + gs-base swap out of CoreClrProbe so
    // the kernel can stand up additional TEBs (Phase E4 cooperative switch).
    // CoreCLR's own TEB stays where it is (CoreClrProbe.SetupTebFacade)
    // because it carries TLS-template and _tls_index wiring this layer
    // doesn't model.
    //
    // Layout per docs/threading-architecture.md §6 (also NT_TIB):
    //   +0x08 StackBase                   TOP of stack (HIGH address)
    //   +0x10 StackLimit                  BOTTOM of stack (LOW address)
    //   +0x30 Self                        TEB*
    //   +0x58 ThreadLocalStoragePointer   TLS slot array (CoreCLR slot 0)
    //   +0x60 PEB                         (nullable; rarely read)
    //   +0x68 LastError                   Win32 ABI mirror
    // Stack grows DOWN: Base > Limit. CoreCLR threads.cpp:6108 asserts
    // Base >= Limit AND dereferences addresses within for stack probes / GC
    // root scan / unwind — caller MUST pass a real mapped range.
    internal static unsafe class TebFacade
    {
        public const uint TebSize = 0x1000;   // 4 KiB

        public const uint OffsetStackBase  = 0x08;
        public const uint OffsetStackLimit = 0x10;
        public const uint OffsetSelf       = 0x30;
        public const uint OffsetTlsPointer = 0x58;
        public const uint OffsetPeb        = 0x60;
        public const uint OffsetLastError  = 0x68;

        // Allocate a zeroed TEB on the kernel heap, populate the NT_TIB
        // header (StackBase / StackLimit / Self). Returns null on OOM.
        // Caller initialises TLS pointer / PEB / LastError if needed.
        public static byte* Allocate(ulong stackBase, ulong stackLimit)
        {
            if (stackBase <= stackLimit)
                return null;  // Base must be > Limit (stack grows down)

            byte* teb = (byte*)GcHeap.AllocateRaw(TebSize);
            if (teb == null) return null;

            *(ulong*)(teb + OffsetStackBase)  = stackBase;
            *(ulong*)(teb + OffsetStackLimit) = stackLimit;
            *(ulong*)(teb + OffsetSelf)       = (ulong)teb;
            return teb;
        }

        // Make `teb` the active TEB by writing IA32_GS_BASE. After this,
        // gs:[N] resolves to (teb + N). Returns false if the underlying
        // wrmsr shellcode buffer isn't available.
        public static bool SetActive(void* teb)
        {
            return X64Asm.WriteGsBaseMsr((ulong)teb);
        }

        // Capture the currently active TEB (whatever IA32_GS_BASE points
        // at — could be the CoreCLR primary TEB, a probe TEB, or 0 if
        // unset). Used by Phase E4 context switch to save/restore.
        public static bool TryGetActive(out byte* teb)
        {
            teb = null;
            if (!X64Asm.ReadGsBaseMsr(out ulong msr)) return false;
            teb = (byte*)msr;
            return true;
        }
    }
}
