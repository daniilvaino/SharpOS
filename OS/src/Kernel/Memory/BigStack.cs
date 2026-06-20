using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    // step 72 / Frontier-C — run CoreCLR on a large stack.
    //
    // SharpOS is a unikernel: the whole system runs on the fixed
    // ~128 KiB UEFI boot stack (proven via [SOSstk]:
    // 0x1FE78000..0x1FE98000). Unlike the GC heap (demand-mapped, grows
    // on #PF), the stack region is fixed and the pager does NOT auto-grow
    // it; a stack #PF pushes the fault frame on the overrun SP → #DF →
    // triple fault → QEMU `-no-reboot` exits silently. Stock .NET
    // reserves ~1 MB/thread; reflection-mode System.Text.Json (nested
    // DoRunClassInit → JIT-on-first-call → typeload → cctor recursion)
    // overruns 128 KiB → the silent death after the gcinfodecoder fix
    // let it recurse that deep.
    //
    // Minimal fix: a Win64 shellcode trampoline that switches RSP to a
    // large pre-mapped buffer, calls a callback, restores RSP and
    // returns. The whole CoreCLR run executes on the big stack; the rest
    // of the kernel is unaffected (scoped switch). Same byte-array
    // shellcode mechanism as GcStackSpill / JumpStub (invariant 1).
    //
    // step105: lives in a DEDICATED 64-byte EfiLoaderCode pool
    // (BootInfo.BigStackStubBuffer). Previously sharing ExecStubBuffer
    // at offset 128 silently overwrote InterfaceDispatchBridge's first
    // 32 bytes, hijacking every interface call into `mov rsp, rcx`.
    internal static unsafe class BigStack
    {
        private const uint StubSize = 32;

        private static bool s_initialized;
        // (newStackTop, callback) -> void. Win64: rcx=newStackTop, rdx=cb.
        private static delegate* unmanaged<void*, delegate* unmanaged<void>, void> s_run;

        // Active managed-stack extent while RunOn is in flight. The caller
        // supplies a dedicated mapped buffer (BootSequence currently uses
        // PhysicalMemory.AllocPages + VirtualMemory.MapFixed), but the
        // surrounding UEFI/physical memory-map region can be much larger
        // than the buffer. These are the authoritative bounds so CoreCLR's
        // m_CacheStackLimit is the real buffer bottom (clean, detectable SO
        // at the limit instead of silent corruption).
        private static ulong s_activeLo;   // buffer bottom (low)
        private static ulong s_activeHi;   // buffer top (high)

        public static bool IsInitialized => s_initialized;

        // Consulted by SharpOSHost_GetStackBounds. Returns true + the
        // true active buffer bounds iff a RunOn is in flight and `sp`
        // falls inside the buffer.
        public static bool TryGetActiveBounds(ulong sp, out ulong lo, out ulong hi)
        {
            lo = s_activeLo;
            hi = s_activeHi;
            return s_activeLo != 0 && s_activeHi != 0 &&
                   sp >= s_activeLo && sp < s_activeHi;
        }

        public static bool TryInitialize(void* execBuffer, uint execBufferSize)
        {
            if (s_initialized)
                return true;
            if (execBuffer == null || execBufferSize < StubSize)
                return false;

            byte* stub = (byte*)execBuffer;
            WriteShellcode(stub);

            s_run = (delegate* unmanaged<void*, delegate* unmanaged<void>, void>)stub;
            s_initialized = true;
            return true;
        }

        // Run `callback` with RSP switched into [bufBase, bufBase+bufSize).
        // The 16-aligned high end is the new stack top (grows down to
        // bufBase). Publishes the true bounds for SharpOSHost_GetStackBounds
        // for the duration, so CoreCLR's stack limit is the real buffer
        // bottom. Returns after callback returns, on the original stack.
        public static bool RunOn(void* bufBase, uint bufSize, delegate* unmanaged<void> callback)
        {
            if (!s_initialized || s_run == null || bufBase == null ||
                bufSize < 0x10000 || callback == null)
                return false;

            ulong lo = (ulong)bufBase;
            ulong hi = (lo + bufSize) & ~0xFUL;        // 16-aligned top
            if (hi <= lo)
                return false;

            s_activeLo = lo;
            s_activeHi = hi;
            s_run((void*)hi, callback);
            s_activeLo = 0;
            s_activeHi = 0;
            return true;
        }

        // Win64 ABI, entry RSP ≡ 8 (mod 16) (after the C# `call s_run`):
        //   push rbp           ; rsp: 8 -> 0 (mod 16)
        //   mov  rbp, rsp      ; rbp = saved-rbp slot
        //   mov  rsp, rcx      ; switch (rcx=newStackTop, 16-aligned → rsp%16==0)
        //   sub  rsp, 0x20     ; Win64 shadow; rsp%16==0 kept
        //   call rdx           ; rsp%16==0 before call → callee entry %16==8 ✓
        //   mov  rsp, rbp      ; restore to saved-rbp slot
        //   pop  rbp           ; restore rbp; rsp → entry rsp
        //   ret
        // 14 bytes. newStackTop must be 16-aligned (caller masks ~0xF).
        //
        // step 115 follow-up #3: emitted via Iced. Smallest of the three
        // shellcode migrations (8 instructions, 14 bytes) — compare-gate
        // dropped after #1/#2 confirmed byte parity on representative
        // patterns. If Iced silently produces 0 bytes the length print
        // will show it; any real divergence breaks Phase3 boot loudly.
        private static void WriteShellcode(byte* p)
        {
            var a = new Iced.Intel.Assembler(64);
            a.push(rbp);
            a.mov(rbp, rsp);
            a.mov(rsp, rcx);
            a.sub(rsp, 0x20);
            a.call(rdx);
            a.mov(rsp, rbp);
            a.pop(rbp);
            a.ret();

            var w = new BigStackBufWriter(p, (int)StubSize);
            a.Assemble(w, 0);
            Console.Write("[bigstack] stub len=0x");
            Console.WriteHex((ulong)w.Count);
            Console.WriteLine("");
        }

        private sealed class BigStackBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public BigStackBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }
    }
}
