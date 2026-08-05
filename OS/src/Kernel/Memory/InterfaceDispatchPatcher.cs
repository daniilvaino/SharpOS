using OS.Boot;
using OS.Hal;

namespace OS.Kernel.Memory
{
    // Overwrites the first 5 bytes of the managed RhpInitialDynamicInterfaceDispatch
    // wrapper with `jmp rel32 <shellcode>` so that the first call for any
    // interface-dispatch cell lands in the bridge shellcode instead of the
    // noisy fallback body.
    //
    // Runs under firmware CR3 (before Pager.TryActivatePagerRoot()). On
    // QEMU/OVMF the kernel image is mapped RWX by default, so the direct
    // write succeeds. On real hardware with W^X we'd need alias-mapping via
    // the pager root + CR3 switch — tracked in nativeaot-nostdlib-limits.md.
    internal static unsafe partial class InterfaceDispatchPatcher
    {
        // jmp qword ptr [rip+0] + an 8-byte absolute target right behind it.
        // 14 bytes, no register touched, and — unlike the jmp rel32 this
        // replaced — no limit on how far the shellcode sits from the image.
        private const byte JmpIndirectOpcode0 = 0xFF;
        private const byte JmpIndirectOpcode1 = 0x25;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall(
            void* execBuffer,
            uint execBufferSize,
            delegate* unmanaged<nint, nint, nint> resolver,
            delegate* unmanaged<void> failHandler)
        {
            if (s_installed) return true;
            if (execBuffer == null) return Refuse("execBuffer null", 0, 0, 0);
            if (resolver == null || failHandler == null) return Refuse("resolver/failHandler null", 0, 0, 0);

            if (!InterfaceDispatchBridge.TryInitialize(execBuffer, execBufferSize, resolver, failHandler))
                return Refuse("bridge init", (ulong)execBuffer, 0, 0);

            byte* shellcode = (byte*)InterfaceDispatchBridge.ShellcodeStart;
            if (shellcode == null) return Refuse("shellcode null", (ulong)execBuffer, 0, 0);

            byte* target = (byte*)InterfaceDispatchStub.GetMethodAddress();
            if (target == null) return Refuse("stub address null", (ulong)execBuffer, (ulong)shellcode, 0);

            int compileLen = Emit(target, shellcode);

            // Readback check: if firmware mapped .text read-only, the writes
            // silently landed in nowhere (or faulted upstream).
            if (target[0] != JmpIndirectOpcode0 || target[1] != JmpIndirectOpcode1)
            {
                // The write did not stick: .text is read-only for us.
                return Refuse("readback mismatch (.text not writable)",
                              (ulong)target, (ulong)shellcode, 0);
            }

            s_installed = true;
            return true;
        }

        // Every refusal names itself and prints the three numbers that decide
        // it. Without them "stub not patched / patch failed" is the first
        // symptom, and it surfaces far away — at the first interface dispatch.
        private static bool Refuse(string why, ulong target, ulong shellcode, long displacement)
        {
            Console.Write("[ifacepatch] refused: ");
            Console.Write(why);
            Console.Write(" target=0x"); Console.WriteHexRaw(target, 16);
            Console.Write(" shellcode=0x"); Console.WriteHexRaw(shellcode, 16);
            Console.Write(" disp=0x"); Console.WriteHexRaw((ulong)displacement, 16);
            Console.WriteLine("");
            return false;
        }
    }
}
