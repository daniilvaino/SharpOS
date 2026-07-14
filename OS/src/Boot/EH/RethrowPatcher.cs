using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 6 — patches RethrowStub.RhpRethrow с byte-array
    // shellcode (~186 bytes). Layout идентичен ThrowExPatcher (step 48)
    // c единственным отличием: ExInfo.Kind set к (Throw | Rethrow) = 0x05
    // вместо чистого Throw = 0x01. Dispatcher reads this flag to use
    // prev ExInfo's ExContext + start FindFirstPassHandler от
    // prev->IdxCurClause (skipping the catch that just ran).
    //
    // Same frame layout (FRAME_SIZE = 0x388):
    //   [rsp + 0x000 .. 0x01F]   shadow space
    //   [rsp + 0x020 .. 0x11F]   PAL_LIMITED_CONTEXT (captured rethrow site)
    //   [rsp + 0x120 .. 0x37F]   ExInfo (kind = Throw|Rethrow)
    //   [rsp + 0x380 .. 0x387]   alignment pad
    //
    // Rethrow site context capture is technically wasted (Dispatch uses
    // prev's ExContext, not ours), but keeping shellcode parallel к
    // ThrowEx avoids subtle layout drift. ~10 bytes of waste is fine.
    internal static unsafe partial class RethrowPatcher
    {
        // Constants kept here (still referenced by body comments + Emit args).
        private const int OffsetCtx     = 0x020;
        private const int OffsetExInfo  = 0x120;
        private const int FrameSize     = 0x388;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)RethrowStub.GetMethodAddress();
            if (target == null) return false;

            byte** headAddr = ExInfoHead.GetHeadAddress();
            void* ingressFn = GetRethrowIngressAddress();

            // step 118 Wave 2 — compile-time codegen (BootAsm.Generator).
            // 2 MovHoles: head (= &s_head), ingress (= RhpTest_ThrowIngress).
            // No compare-gate — first throw validates via existing EH probes
            // (L1..L17) and CoreCLR-hosted census.
            int p = Emit(target, (void**)headAddr, ingressFn);

            if (target[0] != 0x48 || target[p - 1] != 0xCC)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("rethrow shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(nuint)headAddr, 16);
            Console.Write(" ingress=0x");
            Console.WriteHexRaw((ulong)(nuint)ingressFn, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // Reuse RhpTest_ThrowIngress as ingress seam — same managed entry
        // что routes к Dispatch when EhRealDispatch=true. Dispatcher
        // detects Rethrow kind flag и handles accordingly.
        private static void* GetRethrowIngressAddress()
        {
            delegate*<byte*, ExInfo*, void> fn =
                &OS.Boot.ExceptionEngine.RhpTest_ThrowIngress;
            return (void*)fn;
        }
    }
}
