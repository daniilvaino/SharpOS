using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpOS.Std.Exchange;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    // The write barrier of RhpByRefAssignRef for stores into a region, shared
    // by every image (pipe spec, writing into a region).
    //
    // RhpByRefAssignRef has its own convention — rdi = destination slot, rsi =
    // source slot, both advanced by 8, only rax and rcx may change — and the
    // stub bodies in the images are too small for a check. So each image's
    // stub jumps here (`mov rax, entry; jmp rax`, the dispatch bridge's shape),
    // and this one copy of the shellcode lives in its own loader-code buffer:
    //
    //   destination outside the arena  -> copy, advance, return: two loads,
    //                                     a subtraction and a compare more
    //                                     than the plain copy;
    //   inside, value null             -> the same;
    //   inside, otherwise              -> save the volatile registers, ask
    //                                     CheckStore, restore; allowed: copy;
    //                                     refused: touch StoreFaultAddress; the
    //                                     fault handler raises the exception
    //                                     from the caller's frame, of the
    //                                     caller's image when that is an app.
    //
    // The managed stores (RhpAssignRef, RhpCheckedAssignRef, RhpStelemRef,
    // Array.Copy) check in std; this is the one that cannot.
    internal static unsafe class RegionBarrier
    {
        private static void* s_entry;

        public static void* Entry => s_entry;

        private sealed class BufferWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            public int Count;
            public bool Overflow;
            public BufferWriter(byte* p, int capacity) { _p = p; _cap = capacity; }
            public override void WriteByte(byte value)
            {
                if (Count < _cap) _p[Count++] = value;
                else Overflow = true;
            }
        }

        public static bool TryInstall(void* buffer, uint size)
        {
            if (s_entry != null) return true;
            if (buffer == null || size < 256) return false;

            delegate* unmanaged<ulong, ulong, int> check = &CheckStore;

            var a = new Iced.Intel.Assembler(64);
            var slow = a.CreateLabel();
            var store = a.CreateLabel();

            a.mov(rax, (ulong)ExchangeArena.BoundsAddress);
            a.mov(rcx, rdi);
            a.sub(rcx, __qword_ptr[rax]);
            a.cmp(rcx, __qword_ptr[rax + 8]);
            a.jb(slow);

            a.Label(ref store);
            a.mov(rcx, __qword_ptr[rsi]);
            a.mov(__qword_ptr[rdi], rcx);
            a.add(rdi, 8);
            a.add(rsi, 8);
            a.ret();

            a.Label(ref slow);
            a.mov(rcx, __qword_ptr[rsi]);
            a.test(rcx, rcx);
            a.jz(store);
            a.push(rdx);
            a.push(r8);
            a.push(r9);
            a.push(r10);
            a.push(r11);
            a.sub(rsp, 0x80);
            a.movdqu(__xmmword_ptr[rsp + 0x20], xmm0);
            a.movdqu(__xmmword_ptr[rsp + 0x30], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 0x40], xmm2);
            a.movdqu(__xmmword_ptr[rsp + 0x50], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 0x60], xmm4);
            a.movdqu(__xmmword_ptr[rsp + 0x70], xmm5);
            a.mov(rdx, rcx);
            a.mov(rcx, rdi);
            a.mov(rax, (ulong)check);
            a.call(rax);
            a.movdqu(xmm0, __xmmword_ptr[rsp + 0x20]);
            a.movdqu(xmm1, __xmmword_ptr[rsp + 0x30]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 0x40]);
            a.movdqu(xmm3, __xmmword_ptr[rsp + 0x50]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 0x60]);
            a.movdqu(xmm5, __xmmword_ptr[rsp + 0x70]);
            a.add(rsp, 0x80);
            a.pop(r11);
            a.pop(r10);
            a.pop(r9);
            a.pop(r8);
            a.pop(rdx);
            a.test(eax, eax);
            a.jnz(store);
            // Refused: fault on purpose with the stack as the caller left it,
            // so the unwind takes this buffer for a frameless leaf (it is a
            // registered stub range) and the caller's return address on top.
            a.mov(rax, ExchangeArena.StoreFaultAddress);
            a.mov(__byte_ptr[rax], 0);

            var w = new BufferWriter((byte*)buffer, (int)size);
            a.Assemble(w, (ulong)buffer);
            if (w.Overflow) return false;
            s_entry = buffer;
            return true;
        }

        /// <summary>Points an image's RhpByRefAssignRef stub at the shared barrier (12 bytes).</summary>
        public static bool PatchStub(byte* stub)
        {
            if (s_entry == null || stub == null) return false;
            ulong entry = (ulong)s_entry;
            stub[0] = 0x48;                                 // mov rax, imm64
            stub[1] = 0xB8;
            for (int i = 0; i < 8; i++) stub[2 + i] = (byte)(entry >> (8 * i));
            stub[10] = 0xFF;                                // jmp rax
            stub[11] = 0xE0;
            return true;
        }

        [UnmanagedCallersOnly]
        private static int CheckStore(ulong destination, ulong value)
            => ExchangeArena.StoreAllowed(destination, value) ? 1 : 0;

        /// <summary>Whether a faulting address is in this shellcode.</summary>
        public static bool Contains(ulong rip) => s_entry != null && rip - (ulong)s_entry < 512;
    }
}
