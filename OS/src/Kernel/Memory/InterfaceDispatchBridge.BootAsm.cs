// step 119 Wave 5 — compile-time codegen migration of InterfaceDispatchBridge.
// 2 DataSlotHoles: resolverSlot + failSlot — 8-byte data slots at the end
// of the stub, runtime fills with absolute addresses of the managed
// resolver and fail-handler.
//
// Forward Iced labels:
//   slow              — target of two jnz / jne in fast path
//   nullfail          — the failure tail; fall-through after fail_after_spill
//                       (no separate jmp needed). NOT the target of the entry
//                       `jz` any more: a null `this` goes to `slow` so the
//                       resolver can report it with real arguments.
//   failAfterSpill    — target of `jz` mid slow-path (resolver returned 0)
//   resolverSlot      — RIP-relative load target in slow path
//   failSlot          — RIP-relative load target in nullfail path
//
// Stack: rsp is 8 mod 16 on entry (post-call). `sub rsp, 0xA8` adjusts to
// 0 mod 16 for the inner call. `add rsp, 0xA8` restores before tail-jmp.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class InterfaceDispatchBridge
    {
        [CompileTimeAsm]
        private static partial int Emit(byte* dst, void* resolverSlot, void* failSlot);

        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            var slow = a.CreateLabel();
            var nullfail = a.CreateLabel();
            var failAfterSpill = a.CreateLabel();
            var resolverData = a.CreateLabel();
            var failData = a.CreateLabel();

            // -- fast path --
            a.test(rcx, rcx);

            // A null `this` goes down the SLOW path, not straight to the
            // failure tail.
            //
            // It used to jump to the tail, which meant the case a person most
            // needs explained arrived where no argument can be trusted — three
            // rounds of reading registers there produced three different
            // stories, all wrong. The slow path spills and calls the resolver
            // like any other dispatch, and the resolver reports a null `this`
            // by name with arguments passed the ordinary way.
            //
            // The cost is a spill and a call on a path that is about to panic.
            a.jz(slow);

            a.mov(rax, __qword_ptr[rcx]);
            a.mov(r11, __qword_ptr[r10 + 8]);
            a.test(r11, 3);
            a.jnz(slow);

            a.cmp(rax, __qword_ptr[r11 + 32]);
            a.jne(slow);
            a.jmp(__qword_ptr[r11 + 40]);

            // -- slow path --
            a.Label(ref slow);
            a.sub(rsp, 0xA8);

            a.mov(__qword_ptr[rsp + 0x20], rcx);
            a.mov(__qword_ptr[rsp + 0x28], rdx);
            a.mov(__qword_ptr[rsp + 0x30], r8);
            a.mov(__qword_ptr[rsp + 0x38], r9);
            a.mov(__qword_ptr[rsp + 0x40], r10);

            a.movdqu(__xmmword_ptr[rsp + 0x50], xmm0);
            a.movdqu(__xmmword_ptr[rsp + 0x60], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 0x70], xmm2);
            a.movdqu(__xmmword_ptr[rsp + 0x80], xmm3);

            a.mov(rdx, r10);                       // arg2 = cell
            a.mov(rax, __qword_ptr[resolverData]); // RIP-relative load of resolver*
            a.call(rax);

            a.test(rax, rax);
            a.jz(failAfterSpill);

            a.movdqu(xmm0, __xmmword_ptr[rsp + 0x50]);
            a.movdqu(xmm1, __xmmword_ptr[rsp + 0x60]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 0x70]);
            a.movdqu(xmm3, __xmmword_ptr[rsp + 0x80]);

            a.mov(rcx, __qword_ptr[rsp + 0x20]);
            a.mov(rdx, __qword_ptr[rsp + 0x28]);
            a.mov(r8,  __qword_ptr[rsp + 0x30]);
            a.mov(r9,  __qword_ptr[rsp + 0x38]);

            a.add(rsp, 0xA8);
            a.jmp(rax);

            // -- fail_after_spill --
            //
            // Reached when the resolver returned nothing. It has already
            // reported why — every giving-up path in it panics with its own
            // message — so this tail only has to get to the handler.
            a.Label(ref failAfterSpill);

            // The cell again: r10 is volatile in the Win64 convention, so the
            // resolver call above was free to clobber it. The spill slot still
            // holds the real one, and the handler names the interface from it.
            a.mov(r10, __qword_ptr[rsp + 0x40]);
            a.add(rsp, 0xA8);

            // -- nullfail --
            a.Label(ref nullfail);

            // arg1 = the dispatch cell, so the failure can say which interface
            // and slot it was dispatching rather than only that one failed.
            a.mov(rcx, r10);

            a.mov(rax, __qword_ptr[failData]);     // RIP-relative load of fail*
            a.jmp(rax);

            // -- data slots --
            h.DataSlotHole(a, ref resolverData, "resolverSlot");
            h.DataSlotHole(a, ref failData,     "failSlot");
        }
    }
}
