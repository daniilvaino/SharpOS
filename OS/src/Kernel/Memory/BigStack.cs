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
        //   55              push rbp            ; rsp: 8 -> 0 (mod 16)
        //   48 89 E5        mov  rbp, rsp       ; rbp = saved-rbp slot
        //   48 89 CC        mov  rsp, rcx       ; switch (rcx=newStackTop, 16-aligned → rsp%16==0)
        //   48 83 EC 20     sub  rsp, 0x20      ; Win64 shadow; rsp%16==0 kept
        //   FF D2           call rdx            ; rsp%16==0 before call → callee entry %16==8 ✓
        //   48 89 EC        mov  rsp, rbp       ; restore to saved-rbp slot
        //   5D              pop  rbp            ; restore rbp; rsp → entry rsp
        //   C3              ret
        // 14 bytes. newStackTop must be 16-aligned (caller masks ~0xF).
        private static void WriteShellcode(byte* p)
        {
            int i = 0;
            p[i++] = 0x55;                                              // push rbp
            p[i++] = 0x48; p[i++] = 0x89; p[i++] = 0xE5;               // mov rbp, rsp
            p[i++] = 0x48; p[i++] = 0x89; p[i++] = 0xCC;               // mov rsp, rcx
            p[i++] = 0x48; p[i++] = 0x83; p[i++] = 0xEC; p[i++] = 0x20; // sub rsp, 0x20
            p[i++] = 0xFF; p[i++] = 0xD2;                               // call rdx
            p[i++] = 0x48; p[i++] = 0x89; p[i++] = 0xEC;               // mov rsp, rbp
            p[i++] = 0x5D;                                              // pop rbp
            p[i++] = 0xC3;                                              // ret
        }
    }
}
