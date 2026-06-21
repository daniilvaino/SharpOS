using OS.Boot;

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
        private const byte JmpRel32Opcode = 0xE9;
        private const int JmpRel32Size = 5;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall(
            void* execBuffer,
            uint execBufferSize,
            delegate* unmanaged<nint, nint, nint> resolver,
            delegate* unmanaged<void> failHandler)
        {
            if (s_installed) return true;
            if (execBuffer == null) return false;
            if (resolver == null || failHandler == null) return false;

            if (!InterfaceDispatchBridge.TryInitialize(execBuffer, execBufferSize, resolver, failHandler))
                return false;

            byte* shellcode = (byte*)InterfaceDispatchBridge.ShellcodeStart;
            if (shellcode == null) return false;

            byte* target = (byte*)InterfaceDispatchStub.GetMethodAddress();
            if (target == null) return false;

            // rel32 = shellcode - (target + 5); must fit in int32. Range
            // check here so we fail clean rather than emit a wrap-around
            // displacement inside Emit().
            const long Int32Min = -2147483648L;
            const long Int32Max = 2147483647L;
            long displacement = (long)shellcode - ((long)target + JmpRel32Size);
            if (displacement < Int32Min || displacement > Int32Max)
                return false;

            // step 118 Wave 1 — compile-time codegen (BootAsm.Generator).
            // Emit() writes the 5-byte template `E9 00 00 00 00` then
            // patches the 4-byte disp via the RelHole patch line:
            //   *(int*)(dst+1) = (int)((long)shellcode - ((long)dst + 1 + 4));
            // First real-world use of M6.1 RelHole mechanism (JMP rel32
            // displacement-style hole, distinct from MovHole imm64 absolute
            // address holes). No compare-gate — disp32 is computed at
            // install time and varies per boot, so legacy parity would be
            // meaningless without recomputing both sides.
            int compileLen = Emit(target, shellcode);

            // Readback check: if firmware mapped .text read-only, the writes
            // silently landed in nowhere (or faulted upstream).
            if (target[0] != JmpRel32Opcode)
                return false;

            s_installed = true;
            return true;
        }
    }
}
