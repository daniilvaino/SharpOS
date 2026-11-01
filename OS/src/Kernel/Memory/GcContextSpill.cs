using OS.PAL.SharpOSHost;

namespace OS.Kernel.Memory
{
    // Capture-CONTEXT shellcode for the precise GC walk (step 110 Part 7).
    //
    // Companion to GcStackSpill but instead of just pushing callee-saved
    // onto the stack, this writes the caller's GP register set into a
    // caller-provided Context struct, computes the caller's RSP/RIP, then
    // invokes a managed callback with Context* as argument. The callback
    // can use SehUnwind.VirtualUnwind to step the Context up the stack
    // frame by frame, doing precise GC root enumeration at each frame's
    // safepoint via CoffGcInfoDecoder + CoffGcInfoResolver.
    //
    // Calling convention (Win64):
    //   void Spill(Context* ctx, delegate* unmanaged<Context*, void> callback)
    //     rcx = Context*
    //     rdx = callback
    //
    // At entry:
    //   - rbx, rbp, rdi, rsi, r12..r15 = caller's callee-saved values
    //     (CALL doesn't touch them; we capture exactly what caller had)
    //   - rax, r8, r9, r10, r11 = caller-saved, may be clobbered by the
    //     caller's prologue / argument setup. Stored as 0 — GcInfo at a
    //     call-site PC never refers to caller-saved regs (refs cannot
    //     survive a call in a register that calls clobber, by definition).
    //   - rcx = Context*, rdx = callback: our args, NOT caller's rcx/rdx.
    //     Caller's rcx/rdx are caller-saved → stored as 0 above.
    //   - RSP = (caller's RSP at call site) - 8. The 8 bytes at [rsp] are
    //     the return address into caller. We use these to set ctx.Rip and
    //     ctx.Rsp accurately.
    //
    // Memory layout: installed at ExecStubBuffer offset 512 (we share the
    // exec buffer with Cr3Accessor/GcStackSpill/InterfaceDispatchBridge,
    // which together occupy 0..512 — see ExecStubBuffer layout comment in
    // UefiBootInfoBuilder). ExecStubBuffer total size bumped to 1024 in
    // step 110 Part 7 to make room.
    internal static unsafe partial class GcContextSpill
    {
        private const uint StubOffset = 512;
        private const uint StubSize   = 512;

        private static bool s_initialized;
        private static delegate* unmanaged<Context*, delegate* unmanaged<Context*, void>, void> s_invoke;

        public static bool IsInitialized => s_initialized;

        public static bool TryInitialize(void* execBuffer, uint execBufferSize)
        {
            if (s_initialized) return true;
            if (execBuffer == null || execBufferSize < StubOffset + StubSize) return false;

            byte* stub = (byte*)execBuffer + StubOffset;
            // step 118 Wave 4 — compile-time codegen (BootAsm.Generator).
            // 0 holes — args via Win64 ABI (rcx = ctx*, rdx = callback*).
            Emit(stub);
            s_invoke = (delegate* unmanaged<Context*, delegate* unmanaged<Context*, void>, void>)stub;
            s_initialized = true;
            return true;
        }

        public static void Invoke(Context* ctx, delegate* unmanaged<Context*, void> callback)
        {
            if (!s_initialized || s_invoke == null) return;
            s_invoke(ctx, callback);
        }
    }
}
