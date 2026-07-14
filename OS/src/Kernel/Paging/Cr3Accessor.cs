namespace OS.Kernel.Paging
{
    internal static unsafe partial class Cr3Accessor
    {
        private const uint StubBufferSize = 64;
        private const uint ReadStubOffset = 0;
        private const uint WriteStubOffset = 32;

        private static bool s_initialized;
        private static delegate* unmanaged<ulong> s_readCr3;
        private static delegate* unmanaged<ulong, void> s_writeCr3;
        private static void* s_execBuffer;
        private static uint s_execBufferSize;

        public static void SetExecBuffer(void* buffer, uint size)
        {
            s_execBuffer = buffer;
            s_execBufferSize = size;
        }

        public static bool TryRead(out ulong value)
        {
            value = 0;
            if (!s_initialized && !TryInitialize())
                return false;

            value = s_readCr3();
            return true;
        }

        public static bool TryWrite(ulong value)
        {
            if (!s_initialized && !TryInitialize())
                return false;

            if ((value & 0x000FFFFFFFFFF000UL) == 0)
                return false;

            s_writeCr3(value);
            return true;
        }

        public static bool TryInitialize()
        {
            if (s_initialized)
                return true;

            void* stubBuffer;
            if (s_execBuffer != null && s_execBufferSize >= StubBufferSize)
            {
                stubBuffer = s_execBuffer;
            }
            else
            {
                stubBuffer = global::OS.Kernel.Memory.KernelHeap.Alloc(StubBufferSize);
                if (stubBuffer == null)
                    return false;
            }

            OS.Kernel.Util.Memory.Zero(stubBuffer, StubBufferSize);
            byte* destination = (byte*)stubBuffer;
            if (!TryWriteReadStub(destination + ReadStubOffset))
                return false;

            if (!TryWriteWriteStub(destination + WriteStubOffset))
                return false;

            s_readCr3 = (delegate* unmanaged<ulong>)(destination + ReadStubOffset);
            s_writeCr3 = (delegate* unmanaged<ulong, void>)(destination + WriteStubOffset);
            s_initialized = true;
            return true;
        }

        // step 115 follow-up #6: parallel-emit Iced + legacy + compare gate.
        // This is the EARLIEST shellcode emitter in boot order (Phase3
        // Pager.Init → X64PageTable.Init → here), well before
        // Probe_IcedEncode (Phase4). First call materialises Iced's
        // AssemblerRegisters cctor — if Phase2 cctor materialization
        // didn't cover it, expect a HW fault visible in Phase3 boot log
        // (clean fast-fail; fallback is eager Iced pre-touch at end of
        // Phase2).
        private static bool s_readGateLogged;
        private static bool s_writeGateLogged;

        private static bool TryWriteReadStub(byte* destination)
        {
            if (destination == null) return false;

            byte* scratch = stackalloc byte[16];
            // Runtime Iced restored: the lazy-cctor fix (ClassConstructorRunner
            // on major-9 — run cctor iff cctorMethodAddress!=0, null it after)
            // materialised Iced's encoder-table cctors, so runtime Assemble now
            // encodes correctly. Probe_IcedEncode is green. Iced emits into the
            // real exec buffer; legacy bytes into scratch; parity-compare gates.
            int icedLen = EmitReadStubIced(destination, 16);
            int legacyLen = EmitReadStubLegacy(scratch);
            CompareOrPanic("ReadStub", destination, scratch, icedLen, legacyLen);

            if (!s_readGateLogged)
            {
                s_readGateLogged = true;
                OS.Hal.Console.Write("[cr3] readstub iced=legacy OK len=0x");
                OS.Hal.Console.WriteHex((ulong)icedLen);
                OS.Hal.Console.WriteLine("");
            }
            return true;
        }

        private static bool TryWriteWriteStub(byte* destination)
        {
            if (destination == null) return false;

            byte* scratch = stackalloc byte[16];
            // Runtime Iced restored (see ReadStub note above).
            int icedLen = EmitWriteStubIced(destination, 16);
            int legacyLen = EmitWriteStubLegacy(scratch);
            CompareOrPanic("WriteStub", destination, scratch, icedLen, legacyLen);

            if (!s_writeGateLogged)
            {
                s_writeGateLogged = true;
                OS.Hal.Console.Write("[cr3] writestub iced=legacy OK len=0x");
                OS.Hal.Console.WriteHex((ulong)icedLen);
                OS.Hal.Console.WriteLine("");
            }
            return true;
        }

        // ---- Legacy byte-stream emitters (return length for compare). ----

        private static int EmitReadStubLegacy(byte* destination)
        {
            // mov rax, cr3 ; ret
            destination[0] = 0x0F;
            destination[1] = 0x20;
            destination[2] = 0xD8;
            destination[3] = 0xC3;
            return 4;
        }

        private static int EmitWriteStubLegacy(byte* destination)
        {
            // mov rax, rcx ; mov cr3, rax ; ret  (Win64: rcx = new CR3)
            destination[0] = 0x48;
            destination[1] = 0x89;
            destination[2] = 0xC8;
            destination[3] = 0x0F;
            destination[4] = 0x22;
            destination[5] = 0xD8;
            destination[6] = 0xC3;
            return 7;
        }
    }
}
