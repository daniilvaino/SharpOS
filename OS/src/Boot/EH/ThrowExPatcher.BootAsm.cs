// step 118 Wave 2 — compile-time codegen migration of ThrowExPatcher.
// Identical layout to RethrowPatcher except ExInfo.Kind = 1 (Throw)
// vs 5 (Throw | Rethrow). 2 MovHoles (head, ingress).
//
// See RethrowPatcher.BootAsm.cs for the offset cheat-sheet.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Boot.EH
{
    internal static unsafe partial class ThrowExPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst, void** head, void* ingress);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            // capture throw-site rsp/rip
            a.lea(rax, __qword_ptr[rsp + 8]);
            a.mov(rdx, __qword_ptr[rsp]);

            // spill 8 nonvols
            a.push(r15); a.push(r14); a.push(r13); a.push(r12);
            a.push(rbx); a.push(rsi); a.push(rdi); a.push(rbp);

            // sub rsp, 0x388 (FrameSize)
            a.sub(rsp, 0x388);

            // PAL.Rsp = rax, PAL.IP = rdx
            a.mov(__qword_ptr[rsp + 0x28], rax);
            a.mov(__qword_ptr[rsp + 0x20], rdx);

            // GPR snapshots
            a.mov(__qword_ptr[rsp + 0x30], rbp);
            a.mov(__qword_ptr[rsp + 0x38], rdi);
            a.mov(__qword_ptr[rsp + 0x40], rsi);
            a.mov(__qword_ptr[rsp + 0x50], rbx);
            a.mov(__qword_ptr[rsp + 0x58], r12);
            a.mov(__qword_ptr[rsp + 0x60], r13);
            a.mov(__qword_ptr[rsp + 0x68], r14);
            a.mov(__qword_ptr[rsp + 0x70], r15);

            // rdx = &ExInfo, rax = &PAL
            a.lea(rdx, __qword_ptr[rsp + 0x120]);
            a.lea(rax, __qword_ptr[rsp + 0x20]);
            // ExInfo.m_pExContext = rax
            a.mov(__qword_ptr[rdx + 0x08], rax);

            // r8 = &s_head; r9 = *r8
            h.MovHole(a, r8, "head");
            a.mov(r9, __qword_ptr[r8]);
            // ExInfo.m_pPrevExInfo = r9
            a.mov(__qword_ptr[rdx], r9);

            // ExInfo.m_exception = null
            a.xor(rax, rax);
            a.mov(__qword_ptr[rdx + 0x10], rax);

            // ExInfo.m_kind = 1 (Throw)  ← only diff vs RethrowPatcher
            a.mov(__byte_ptr[rdx + 0x18], 1);

            // ExInfo.m_passNumber = 1
            a.mov(__byte_ptr[rdx + 0x19], 1);

            // ExInfo.m_idxCurClause = 0xFFFFFFFF
            a.mov(__dword_ptr[rdx + 0x1C], -1);

            // s_head = &ExInfo
            a.mov(__qword_ptr[r8], rdx);

            // call ingress
            h.MovHole(a, r10, "ingress");
            a.call(r10);

            // int3 — should not return
            a.int3();
        }
    }
}
