using OS.Boot.EH;
using OS.Kernel.Diagnostics;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Threading
{
    // Phase E9.b -- per-thread TEB + TLS block allocator for CoreCLR-hosted
    // threads. CoreCLR's native C++ uses `thread_local` (compiled to
    // gs:[0x58][_tls_index]) for variables like t_ThreadType, which
    // distinguishes "regular managed thread" from "debugger helper",
    // "GC thread", etc. Pre-E9.b all our hosted threads shared a single
    // global TEB (allocated once by CoreClrProbe.SetupTebFacade), so any
    // thread that wrote t_ThreadType polluted ALL threads -> wrong
    // identity -> hot _ASSERTE chain (debugger.cpp:15558 et al).
    //
    // This class makes the discovery one-time (PE TLS directory walked
    // once) and the allocation per-thread. Layout matches what CoreCLR
    // expects: NT_TIB header + 64-slot TLS pointer array + per-thread
    // tls_block initialised from the PE TLS template.
    //
    // Cross-references:
    //   CoreClrProbe.SetupTebFacade -- legacy primary TEB setup, now
    //     delegates to CoreClrTeb.Allocate for the main thread.
    //   Scheduler.SpawnHosted -- calls CoreClrTeb.Allocate per new thread.
    //   X64Asm.CoopSwitch -- loads IA32_GS_BASE from ContextBlock+0x08
    //     before resuming next thread.
    internal static unsafe class CoreClrTeb
    {
        public const uint TebSize = 0x1000;       // 4 KiB
        public const uint TlsSlotsSize = 64 * 8;  // 64 slots * 8 bytes

        public const uint OffsetStackBase  = 0x08;
        public const uint OffsetStackLimit = 0x10;
        public const uint OffsetSelf       = 0x30;
        public const uint OffsetTlsPointer = 0x58;

        private static bool s_templateReady;
        private static byte* s_templateSrc;    // pointer into kernel image (PE TLS raw data)
        private static uint s_tlsRawSize;
        private static uint s_tlsBlockSize;
        private static ulong s_tlsIndexAbsoluteAddr;   // location of _tls_index variable in the image

        public static bool IsTemplateReady => s_templateReady;

        // One-time discovery of CoreCLR's TLS template via PE TLS
        // directory. Safe to call multiple times (idempotent). Returns
        // false on discovery failure (CoffRuntimeFunctionTable not yet
        // initialised, missing TLS directory).
        public static bool EnsureTemplate()
        {
            if (s_templateReady) return true;

            if (!CoffRuntimeFunctionTable.IsInitialized)
                return false;

            byte* image = CoffRuntimeFunctionTable.ImageBase;
            uint peOffset = *(uint*)(image + 0x3C);
            byte* pe = image + peOffset;
            byte* opt = pe + 24;
            ulong linkImageBase = *(ulong*)(opt + 24);
            uint tlsDirRva = *(uint*)(opt + 112 + 9 * 8);
            if (tlsDirRva == 0) return false;

            ulong* tlsDir = (ulong*)(image + tlsDirRva);
            ulong tlsStartVa = tlsDir[0];
            ulong tlsEndVa = tlsDir[1];
            ulong tlsIndexVa = tlsDir[2];
            uint tlsZeroFill = *(uint*)((byte*)tlsDir + 32);

            ulong tlsTemplateRva = tlsStartVa - linkImageBase;
            ulong tlsIndexRva = tlsIndexVa - linkImageBase;
            uint tlsRawSize = (uint)(tlsEndVa - tlsStartVa);

            s_templateSrc = image + tlsTemplateRva;
            s_tlsRawSize = tlsRawSize;
            s_tlsBlockSize = tlsRawSize + tlsZeroFill;
            s_tlsIndexAbsoluteAddr = (ulong)(image + tlsIndexRva);

            // _tls_index = 0 — first (and only) module slot.
            *(uint*)s_tlsIndexAbsoluteAddr = 0;

            s_templateReady = true;
            return true;
        }

        // Allocate a fresh TEB + slots + tls_block for one thread.
        // `stackBase` = TOP of stack (HIGH addr), `stackLimit` = BOTTOM
        // (LOW addr). Returns the TEB pointer (suitable for
        // IA32_GS_BASE), or null on allocation failure.
        public static byte* Allocate(ulong stackBase, ulong stackLimit)
        {
            if (!EnsureTemplate()) return null;

            byte* teb = (byte*)GcHeap.AllocateRaw(TebSize);
            byte* slots = (byte*)GcHeap.AllocateRaw(TlsSlotsSize);
            byte* tlsBlock = (byte*)GcHeap.AllocateRaw(s_tlsBlockSize);
            if (teb == null || slots == null || tlsBlock == null) return null;

            // Copy TLS template (initialized portion). GcHeap returns
            // zeroed memory; zero-fill portion is already 0.
            for (uint i = 0; i < s_tlsRawSize; i++) tlsBlock[i] = s_templateSrc[i];

            // NT_TIB header.
            *(ulong*)(teb + OffsetStackBase)  = stackBase;
            *(ulong*)(teb + OffsetStackLimit) = stackLimit;
            *(ulong*)(teb + OffsetSelf)       = (ulong)teb;
            *(ulong*)(teb + OffsetTlsPointer) = (ulong)slots;

            // slots[0] -> per-thread tls_block.
            *(ulong*)(slots + 0) = (ulong)tlsBlock;

            return teb;
        }
    }
}
