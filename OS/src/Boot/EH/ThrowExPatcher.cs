using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 5.1 — patches ThrowExStub.RhpThrowEx with shellcode
    // that builds PAL_LIMITED_CONTEXT + ExInfo on the stack and calls
    // managed RhpTest_ThrowIngress(exception, &exInfo). XMM6-XMM15 are
    // NOT spilled (sage 2: optional in 5.1, mandatory in 5.5b/step 7
    // when funclets restore them).
    //
    // Frame layout after `sub rsp, 0x388` (FRAME_SIZE):
    //
    //   [rsp + 0x000 .. 0x01F]   shadow space for callee
    //   [rsp + 0x020 .. 0x11F]   PAL_LIMITED_CONTEXT (size 0x100)
    //   [rsp + 0x120 .. 0x37F]   ExInfo              (size 0x260)
    //   [rsp + 0x380 .. 0x387]   8 bytes alignment pad
    //
    // Stack on entry (after CALL pushed return address, before our pushes):
    //   [rsp+0]  = throw-site return address
    //   [rsp+8]  = throw-site RSP value
    //
    // After 8 nonvol pushes (rbp/rdi/rsi/rbx/r12/r13/r14/r15) + sub rsp,0x388
    // total displacement from entry = 0x40 + 0x388 = 0x3C8. Entry RSP was
    // 8 mod 16 (Microsoft x64 ABI), so RSP after subtraction is 0 mod 16
    // — OK for the call.
    //
    // Two placeholder absolute addresses are patched in:
    //   - &ExInfoHead.s_head             (10-byte mov r8, imm64)
    //   - &RhpTest_ThrowIngress          (10-byte mov r10, imm64)
    internal static unsafe partial class ThrowExPatcher
    {
        // Stack slot offsets. PAL fields are at OffsetCtx + PalLimitedContext.OffsetX.
        private const int OffsetCtx     = 0x020;
        private const int OffsetExInfo  = 0x120;
        private const int FrameSize     = 0x388;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)ThrowExStub.GetMethodAddress();
            if (target == null) return false;

            byte** headAddr = ExInfoHead.GetHeadAddress();
            void* ingressFn = GetIngressAddress();

            // step 118 Wave 2 — compile-time codegen (BootAsm.Generator).
            // 2 MovHoles: head, ingress. Identical to RethrowPatcher except
            // ExInfo.Kind = 1 (Throw) vs 5 (Throw | Rethrow). No compare-gate
            // — first throw validates via existing EH probes (L1..L17).
            int p = Emit(target, (void**)headAddr, ingressFn);

            if (target[0] != 0x48 || target[p - 1] != 0xCC)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("throw-ex shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(nuint)headAddr, 16);
            Console.Write(" ingress=0x");
            Console.WriteHexRaw((ulong)(nuint)ingressFn, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // Resolve address of OS.Boot.ExceptionEngine.RhpTest_ThrowIngress
        // through the [UnmanagedCallersOnly] export.
        private static void* GetIngressAddress()
        {
            delegate* unmanaged<byte*, ExInfo*, void> fn =
                &OS.Boot.ExceptionEngine.RhpTest_ThrowIngress;
            return (void*)fn;
        }
    }
}
