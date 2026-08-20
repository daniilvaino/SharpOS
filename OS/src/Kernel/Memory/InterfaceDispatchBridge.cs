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

        // Pre-step-119 manual byte emitter. NOT COMPILED — the live code is
        // Emit() in InterfaceDispatchBridge.BootAsm.cs (compile-time codegen
        // via BootAsm.Generator + Iced). This is reference material only.
        //
        // Read that sentence before editing anything below it. Three rounds of
        // a debugging session went into changing this copy and wondering why
        // the running shellcode never changed; the answer was always twenty
        // lines above the edit.
#if false
        private static int WriteShellcode(byte* buf, nint resolverAddr, nint failAddr)
        {
            int o = 0;

            // -- fast path --

            // test rcx, rcx            48 85 C9
            buf[o++] = 0x48; buf[o++] = 0x85; buf[o++] = 0xC9;

            // jz slow                  0F 84 <rel32>          (patch later)
            //
            // To the SLOW path, not to the failure tail.
            //
            // A null `this` used to jump straight to the failure handler, which
            // meant the one case a person most needs explained arrived with no
            // arguments the handler could trust — and every attempt to read the
            // registers there described a machine state that was not the one we
            // were in. The slow path spills and calls the resolver like any
            // other dispatch, and the resolver already reports a null `this` by
            // name, with arguments passed the ordinary way.
            //
            // The cost is a spill and a call on a path that is about to panic.
            int jzNullPatch;
            buf[o++] = 0x0F; buf[o++] = 0x84;
            jzNullPatch = o; o += 4;

            // mov rax, [rcx]           48 8B 01
            buf[o++] = 0x48; buf[o++] = 0x8B; buf[o++] = 0x01;

            // mov r11, [r10 + 8]       4D 8B 5A 08
            buf[o++] = 0x4D; buf[o++] = 0x8B; buf[o++] = 0x5A; buf[o++] = 0x08;

            // test r11, 3              49 F7 C3 03 00 00 00
            buf[o++] = 0x49; buf[o++] = 0xF7; buf[o++] = 0xC3;
            buf[o++] = 0x03; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // jnz slow                 0F 85 <rel32>          (patch later)
            int jnzSlowPatch1;
            buf[o++] = 0x0F; buf[o++] = 0x85;
            jnzSlowPatch1 = o; o += 4;

            // cmp rax, [r11 + 32]      49 3B 43 20
            buf[o++] = 0x49; buf[o++] = 0x3B; buf[o++] = 0x43; buf[o++] = 0x20;

            // jne slow                 0F 85 <rel32>
            int jnzSlowPatch2;
            buf[o++] = 0x0F; buf[o++] = 0x85;
            jnzSlowPatch2 = o; o += 4;

            // jmp qword ptr [r11 + 40] 41 FF 63 28
            buf[o++] = 0x41; buf[o++] = 0xFF; buf[o++] = 0x63; buf[o++] = 0x28;

            // -- slow path --
            int slowLabel = o;

            // sub rsp, 0xA8            48 81 EC A8 00 00 00
            buf[o++] = 0x48; buf[o++] = 0x81; buf[o++] = 0xEC;
            buf[o++] = 0xA8; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // mov [rsp+0x20], rcx      48 89 4C 24 20
            buf[o++] = 0x48; buf[o++] = 0x89; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x20;
            // mov [rsp+0x28], rdx      48 89 54 24 28
            buf[o++] = 0x48; buf[o++] = 0x89; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x28;
            // mov [rsp+0x30], r8       4C 89 44 24 30
            buf[o++] = 0x4C; buf[o++] = 0x89; buf[o++] = 0x44; buf[o++] = 0x24; buf[o++] = 0x30;
            // mov [rsp+0x38], r9       4C 89 4C 24 38
            buf[o++] = 0x4C; buf[o++] = 0x89; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x38;
            // mov [rsp+0x40], r10      4C 89 54 24 40
            buf[o++] = 0x4C; buf[o++] = 0x89; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x40;

            // movdqu [rsp+0x50], xmm0  F3 0F 7F 44 24 50
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x7F; buf[o++] = 0x44; buf[o++] = 0x24; buf[o++] = 0x50;
            // movdqu [rsp+0x60], xmm1  F3 0F 7F 4C 24 60
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x7F; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x60;
            // movdqu [rsp+0x70], xmm2  F3 0F 7F 54 24 70
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x7F; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x70;
            // movdqu [rsp+0x80], xmm3  F3 0F 7F 9C 24 80 00 00 00  (disp32 because >= 0x80)
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x7F; buf[o++] = 0x9C; buf[o++] = 0x24;
            buf[o++] = 0x80; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // mov rdx, r10             4C 89 D2
            buf[o++] = 0x4C; buf[o++] = 0x89; buf[o++] = 0xD2;

            // mov rax, [rip + resolverPtr]   48 8B 05 <rel32>
            int resolverRipPatch;
            buf[o++] = 0x48; buf[o++] = 0x8B; buf[o++] = 0x05;
            resolverRipPatch = o; o += 4;

            // call rax                 FF D0
            buf[o++] = 0xFF; buf[o++] = 0xD0;

            // test rax, rax            48 85 C0
            buf[o++] = 0x48; buf[o++] = 0x85; buf[o++] = 0xC0;

            // jz fail_after_spill      0F 84 <rel32>
            int jzFailAfterSpillPatch;
            buf[o++] = 0x0F; buf[o++] = 0x84;
            jzFailAfterSpillPatch = o; o += 4;

            // movdqu xmm0, [rsp+0x50]  F3 0F 6F 44 24 50
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x6F; buf[o++] = 0x44; buf[o++] = 0x24; buf[o++] = 0x50;
            // movdqu xmm1, [rsp+0x60]
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x6F; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x60;
            // movdqu xmm2, [rsp+0x70]
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x6F; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x70;
            // movdqu xmm3, [rsp+0x80]  disp32
            buf[o++] = 0xF3; buf[o++] = 0x0F; buf[o++] = 0x6F; buf[o++] = 0x9C; buf[o++] = 0x24;
            buf[o++] = 0x80; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // mov rcx, [rsp+0x20]      48 8B 4C 24 20
            buf[o++] = 0x48; buf[o++] = 0x8B; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x20;
            // mov rdx, [rsp+0x28]
            buf[o++] = 0x48; buf[o++] = 0x8B; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x28;
            // mov r8, [rsp+0x30]       4C 8B 44 24 30
            buf[o++] = 0x4C; buf[o++] = 0x8B; buf[o++] = 0x44; buf[o++] = 0x24; buf[o++] = 0x30;
            // mov r9, [rsp+0x38]
            buf[o++] = 0x4C; buf[o++] = 0x8B; buf[o++] = 0x4C; buf[o++] = 0x24; buf[o++] = 0x38;

            // add rsp, 0xA8            48 81 C4 A8 00 00 00
            buf[o++] = 0x48; buf[o++] = 0x81; buf[o++] = 0xC4; buf[o++] = 0xA8; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // jmp rax                  FF E0
            buf[o++] = 0xFF; buf[o++] = 0xE0;

            // -- fail_after_spill --
            int failAfterSpillLabel = o;



            // mov r10, [rsp+0x40]      4C 8B 54 24 40
            //
            // The cell again. r10 is volatile in the Win64 convention, so the
            // resolver call above is free to have clobbered it — reading it
            // afterwards would hand the failure handler a plausible-looking
            // wrong address, which is worse than none. The spill slot still
            // holds the real one.
            buf[o++] = 0x4C; buf[o++] = 0x8B; buf[o++] = 0x54; buf[o++] = 0x24; buf[o++] = 0x40;

            // add rsp, 0xA8
            buf[o++] = 0x48; buf[o++] = 0x81; buf[o++] = 0xC4; buf[o++] = 0xA8; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // mov r8d, 2               41 B8 02 00 00 00   (route: resolver found nothing)
            buf[o++] = 0x41; buf[o++] = 0xB8; buf[o++] = 0x02; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // jmp +6                   EB 06
            //
            // Over the null-route tag below. Without this the spilled route
            // falls straight through it and arrives claiming to be the other
            // one — the first version did exactly that.
            buf[o++] = 0xEB; buf[o++] = 0x06;

            // -- nullthis --
            //
            // Its own two instructions rather than sharing the tail: the jump
            // from the top lands here, so this is where "the reference was
            // null" can still be said. Falls through into the shared tail.
            int nullfailLabel = o;

            // mov r8d, 1               41 B8 01 00 00 00   (route tag)
            buf[o++] = 0x41; buf[o++] = 0xB8; buf[o++] = 0x01; buf[o++] = 0x00; buf[o++] = 0x00; buf[o++] = 0x00;

            // mov rdx, rsp             48 89 E2
            //
            // The stack pointer, so the handler can print the top few words
            // and let a human pick out the return address.
            //
            // Reading one slot here was the obvious thing and it printed zero:
            // the frame at this point is not what the layout comment assumes on
            // every route in. Dumping a handful is honest about that — one of
            // them is the call site, and tools/symbolize_app.py will say which.
            buf[o++] = 0x48; buf[o++] = 0x89; buf[o++] = 0xE2;

            // mov rcx, r10             4C 89 D1
            //
            // The cell, handed to the failure handler as its argument: it
            // names the interface and slot being dispatched, which is the
            // difference between "an interface call failed" and a diagnosis.
            // Both routes reach here with it in r10 — the null check never
            // clobbers it, and the spilled path has just restored it.
            buf[o++] = 0x4C; buf[o++] = 0x89; buf[o++] = 0xD1;

            // mov rax, [rip + failPtr]   48 8B 05 <rel32>
            int failRipPatch;
            buf[o++] = 0x48; buf[o++] = 0x8B; buf[o++] = 0x05;
            failRipPatch = o; o += 4;

            // jmp rax                  FF E0
            buf[o++] = 0xFF; buf[o++] = 0xE0;

            // -- data slots (8-byte aligned) --
            while ((o & 7) != 0) buf[o++] = 0x90;   // pad with NOPs to 8-byte alignment

            int resolverSlot = o;
            WriteI64(buf, o, (long)resolverAddr); o += 8;

            int failSlot = o;
            WriteI64(buf, o, (long)failAddr); o += 8;

            // Patch rel32 displacements: target - (nextInstructionStart)
            PatchRel32(buf, jzNullPatch,            slowLabel);
            PatchRel32(buf, jnzSlowPatch1,          slowLabel);
            PatchRel32(buf, jnzSlowPatch2,          slowLabel);
            PatchRel32(buf, resolverRipPatch,       resolverSlot);
            PatchRel32(buf, jzFailAfterSpillPatch,  failAfterSpillLabel);
            PatchRel32(buf, failRipPatch,           failSlot);

            return o;
        }

        private static void PatchRel32(byte* buf, int dispOffset, int targetOffset)
        {
            int nextInstr = dispOffset + 4;
            int rel = targetOffset - nextInstr;
            buf[dispOffset + 0] = (byte)(rel);
            buf[dispOffset + 1] = (byte)(rel >> 8);
            buf[dispOffset + 2] = (byte)(rel >> 16);
            buf[dispOffset + 3] = (byte)(rel >> 24);
        }

        private static void WriteI64(byte* buf, int offset, long value)
        {
            for (int i = 0; i < 8; i++)
                buf[offset + i] = (byte)(value >> (i * 8));
        }
#endif
    }
}
