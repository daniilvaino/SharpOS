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
    internal static unsafe class InterfaceDispatchPatcher
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

            // rel32 = shellcode - (target + 5); must fit in int32.
            const long Int32Min = -2147483648L;
            const long Int32Max = 2147483647L;
            long displacement = (long)shellcode - ((long)target + JmpRel32Size);
            if (displacement < Int32Min || displacement > Int32Max)
                return false;

            int rel32 = (int)displacement;

            // Write atomically enough for a single-core boot: opcode first would
            // leave a half-patched instruction visible; we're not concurrent
            // here, so plain sequential stores suffice.
            target[0] = JmpRel32Opcode;
            target[1] = (byte)(rel32);
            target[2] = (byte)(rel32 >> 8);
            target[3] = (byte)(rel32 >> 16);
            target[4] = (byte)(rel32 >> 24);

            // Readback check: if firmware mapped .text read-only, the writes
            // silently landed in nowhere (or faulted upstream). Verify one
            // byte matches.
            if (target[0] != JmpRel32Opcode)
                return false;

            s_installed = true;
            return true;
        }
    }
}
