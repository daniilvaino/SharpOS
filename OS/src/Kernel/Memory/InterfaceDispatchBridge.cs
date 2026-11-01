using System.Runtime.InteropServices;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // -----------------------------------------------------------------------
    // x64 bridge trampoline for interface dispatch.
    //
    // Contract with ILC call-site:
    //     rcx = `this`
    //     r10 = InterfaceDispatchCell*
    //     [rsp] = return address
    //     rdx/r8/r9/xmm0..3 = interface-method arguments (preserved through)
    //
    // Layout of the shellcode we build (bytes verified against Intel SDM):
    //
    //   ; fast path: single-slot cache check
    //     test   rcx, rcx                      ; null `this`?
    //     jz     nullfail
    //     mov    rax, [rcx]                    ; MT of instance
    //     mov    r11, [r10 + 8]                ; cell.m_pCache
    //     test   r11, 3                        ; tag bits → still DispatchCellInfo?
    //     jnz    slow                          ; not a real cache yet
    //     cmp    rax, [r11 + 32]               ; cache.entries[0].InstanceType
    //     jne    slow                          ; wrong type → resolve again
    //     jmp    qword ptr [r11 + 40]          ; entries[0].TargetCode
    //
    //   ; slow path: spill args, call managed resolver, restore, tail-jmp
    //   slow:
    //     sub    rsp, 0A8h
    //     mov    [rsp+20h], rcx
    //     mov    [rsp+28h], rdx
    //     mov    [rsp+30h], r8
    //     mov    [rsp+38h], r9
    //     mov    [rsp+40h], r10
    //     movdqu [rsp+50h], xmm0
    //     movdqu [rsp+60h], xmm1
    //     movdqu [rsp+70h], xmm2
    //     movdqu [rsp+80h], xmm3
    //     mov    rdx, r10                      ; arg2 = cell (arg1 rcx already = this)
    //     mov    rax, [rip + resolverPtr]
    //     call   rax                            ; rax ← target method pointer
    //     test   rax, rax
    //     jz     fail_after_spill
    //     movdqu xmm0, [rsp+50h]
    //     movdqu xmm1, [rsp+60h]
    //     movdqu xmm2, [rsp+70h]
    //     movdqu xmm3, [rsp+80h]
    //     mov    rcx, [rsp+20h]
    //     mov    rdx, [rsp+28h]
    //     mov    r8,  [rsp+30h]
    //     mov    r9,  [rsp+38h]
    //     add    rsp, 0A8h
    //     jmp    rax                           ; tail-jump; caller sees original frame
    //
    //   fail_after_spill:
    //     mov    r10, [rsp+40h]                ; cell again (r10 is volatile)
    //     add    rsp, 0A8h
    //   nullfail:
    //     mov    rcx, r10                      ; arg1 = cell, so the failure
    //     mov    rax, [rip + failPtr]          ;   can name interface and slot
    //     jmp    rax
    //
    //   resolverPtr: .qword <addr of InterfaceDispatchResolver.Resolve>
    //   failPtr:     .qword <addr of Panic/fail handler>
    //
    // rsp on entry is 8 mod 16 (just after a `call`). `sub rsp, 0xA8` leaves it
    // 0 mod 16, satisfying Win64 alignment for the inner `call`.
    // -----------------------------------------------------------------------

    internal static unsafe partial class InterfaceDispatchBridge
    {
        // Offsets inside the exec-stub buffer (layout documented in
        // UefiBootInfoBuilder.cs): 128..511 is the 384-byte window we own.
        private const uint StubOffset = 128;
        private const uint MaxStubSize = 384;

        private static bool s_initialized;
        private static void* s_shellcodeStart;

        public static bool IsInitialized => s_initialized;
        public static void* ShellcodeStart => s_shellcodeStart;

        public static bool TryInitialize(
            void* execBuffer,
            uint execBufferSize,
            delegate* unmanaged<nint, nint, nint> resolver,
            // Takes the dispatch cell: the failure names the interface and
            // slot it was dispatching, instead of only the mechanism.
            delegate* unmanaged<nint, nint, nint, void> failHandler)
        {
            if (s_initialized) return true;
            if (execBuffer == null || execBufferSize < StubOffset + MaxStubSize) return false;
            if (resolver == null || failHandler == null) return false;

            byte* p0 = (byte*)execBuffer + StubOffset;
            // step 119 Wave 5 — compile-time codegen via BootAsm.Generator.
            // resolverSlot/failSlot are 8-byte data slots (DataSlotHole) at
            // end of stub; runtime patches them with the absolute addresses
            // of the managed resolver and fail-handler.
            int written = Emit(p0, (void*)resolver, (void*)failHandler);
            if (written < 0 || (uint)written > MaxStubSize) return false;

            s_shellcodeStart = p0;
            s_initialized = true;
            return true;
        }
    }
}
