namespace OS.Kernel.Paging
{
    internal static unsafe class Cr3Accessor
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

        private static bool TryWriteReadStub(byte* destination)
        {
            if (destination == null)
                return false;

            // mov rax, cr3
            // ret
            destination[0] = 0x0F;
            destination[1] = 0x20;
            destination[2] = 0xD8;
            destination[3] = 0xC3;
            return true;
        }

        private static bool TryWriteWriteStub(byte* destination)
        {
            if (destination == null)
                return false;

            // Windows x64 ABI: rcx = new CR3 value.
            // mov rax, rcx
            // mov cr3, rax
            // ret
            destination[0] = 0x48;
            destination[1] = 0x89;
            destination[2] = 0xC8;
            destination[3] = 0x0F;
            destination[4] = 0x22;
            destination[5] = 0xD8;
            destination[6] = 0xC3;
            return true;
        }
    }
}
