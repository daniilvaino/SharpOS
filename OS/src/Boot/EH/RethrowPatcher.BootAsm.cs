// step 118 Wave 2 — compile-time codegen migration of RethrowPatcher.
// 2 MovHoles (head, ingress). Layout identical to ThrowExPatcher except
// ExInfo.Kind = 0x05 (Throw | Rethrow) — Dispatcher reads this flag to
// use prev ExInfo's ExContext + skip past the catch that just ran.
//
// Hardcoded offsets (cf. ExInfo.cs / PalLimitedContext.cs):
//   PAL slots @ rsp + OffsetCtx + Pal.Offset<X>:
//     0x20 + 0x00 = 0x20  IP
//     0x20 + 0x08 = 0x28  Rsp
//     0x20 + 0x10 = 0x30  Rbp
//     0x20 + 0x18 = 0x38  Rdi
//     0x20 + 0x20 = 0x40  Rsi
//     0x20 + 0x30 = 0x50  Rbx
//     0x20 + 0x38 = 0x58  R12
//     0x20 + 0x40 = 0x60  R13
//     0x20 + 0x48 = 0x68  R14
//     0x20 + 0x50 = 0x70  R15
//   ExInfo @ rsp + 0x120; fields:
//     +0x00  PrevExInfo
//     +0x08  ExContext
//     +0x10  Exception
//     +0x18  Kind (byte)
//     +0x19  PassNumber (byte)
//     +0x1C  IdxCurClause (dword)

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class RethrowPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst, void** head, void* ingress);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            // capture rethrow-site rsp/rip
            a.lea(rax, __qword_ptr[rsp + 8]);
            a.mov(rdx, __qword_ptr[rsp]);

            // spill 8 nonvols
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);

            // sub rsp, 0x388 (FrameSize)
            a.sub(rsp, 0x388);

            // PAL.Rsp = rax (offset 0x28)
            a.mov(__qword_ptr[rsp + 0x28], rax);
            // PAL.IP  = rdx (offset 0x20)
            a.mov(__qword_ptr[rsp + 0x20], rdx);

            // GPR snapshots
            a.mov(__qword_ptr[rsp + 0x30], rbp);   // Rbp
            a.mov(__qword_ptr[rsp + 0x38], rdi);   // Rdi
            a.mov(__qword_ptr[rsp + 0x40], rsi);   // Rsi
            a.mov(__qword_ptr[rsp + 0x50], rbx);   // Rbx
            a.mov(__qword_ptr[rsp + 0x58], r12);   // R12
            a.mov(__qword_ptr[rsp + 0x60], r13);   // R13
            a.mov(__qword_ptr[rsp + 0x68], r14);   // R14
            a.mov(__qword_ptr[rsp + 0x70], r15);   // R15

            // rdx = &ExInfo (at rsp + 0x120)
            a.lea(rdx, __qword_ptr[rsp + 0x120]);
            // rax = &PAL (at rsp + 0x20)
            a.lea(rax, __qword_ptr[rsp + 0x20]);
            // ExInfo.m_pExContext = rax (offset 0x08)
            a.mov(__qword_ptr[rdx + 0x08], rax);

            // r8 = &s_head (MovHole)
            h.MovHole(a, r8, "head");
            // r9 = *r8 (current head — prev ExInfo to inherit from)
            a.mov(r9, __qword_ptr[r8]);
            // ExInfo.m_pPrevExInfo = r9 (offset 0x00)
            a.mov(__qword_ptr[rdx], r9);

            // ExInfo.m_exception = null  (xor rax,rax; mov [rdx+0x10], rax)
            a.xor(rax, rax);
            a.mov(__qword_ptr[rdx + 0x10], rax);

            // ExInfo.m_kind = (Throw | Rethrow) = 0x05
            a.mov(__byte_ptr[rdx + 0x18], 5);

            // ExInfo.m_passNumber = 1
            a.mov(__byte_ptr[rdx + 0x19], 1);

            // ExInfo.m_idxCurClause = 0xFFFFFFFF (sentinel; Dispatcher
            // overrides for rethrow with prev->IdxCurClause)
            a.mov(__dword_ptr[rdx + 0x1C], -1);

            // s_head = &ExInfo  (mov [r8], rdx)
            a.mov(__qword_ptr[r8], rdx);

            // call ingress (MovHole). RCX still holds exception, RDX = &ExInfo.
            h.MovHole(a, r10, "ingress");
            a.call(r10);

            // int3 — should not return
            a.int3();
        }
    }
}
