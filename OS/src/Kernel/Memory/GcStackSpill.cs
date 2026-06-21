namespace OS.Kernel.Memory
{
    // Register-spill trampoline for the conservative GC stack scanner.
    //
    // Problem: ILC may keep live managed references in callee-saved
    // registers (RBX, RBP, RDI, RSI, R12..R15) across calls. Our
    // ScanStack reads memory only, so those roots would be invisible.
    //
    // Solution: before running MarkAll, call into a small shellcode
    // stub that pushes all callee-saved regs onto the stack, calls
    // back into managed code, then pops them. While MarkAll runs,
    // those register values are present in the stack region that
    // ScanStack walks, so any root living in a callee-saved reg is
    // picked up by the conservative sweep.
    //
    // Memory layout: the shellcode is installed at offset 64 of the
    // shared ExecStubBuffer (Cr3Accessor uses 0..63).
    internal static unsafe partial class GcStackSpill
    {
        private const uint StubOffset = 64;
        private const uint StubSize = 64;

        private static bool s_initialized;
        private static delegate* unmanaged<delegate* unmanaged<void>, void> s_invoke;

        public static bool IsInitialized => s_initialized;

        public static bool TryInitialize(void* execBuffer, uint execBufferSize)
        {
            if (s_initialized)
                return true;

            if (execBuffer == null || execBufferSize < StubOffset + StubSize)
                return false;

            byte* stub = (byte*)execBuffer + StubOffset;

            // step 118 — compile-time codegen via BootAsm.Generator.
            // Emit() is a static ReadOnlySpan<byte> template baked at
            // OS-build time from the Iced fluent body in GcStackSpill.Iced.cs.
            Emit(stub);

            s_invoke = (delegate* unmanaged<delegate* unmanaged<void>, void>)stub;
            s_initialized = true;
            return true;
        }

        public static void Invoke(delegate* unmanaged<void> callback)
        {
            if (!s_initialized || s_invoke == null)
                return;

            s_invoke(callback);
        }
    }
}
