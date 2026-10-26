namespace OS.Hal
{
    // Cryptographic random bytes from the CPU: RDRAND, the output of the
    // processor's own DRBG reseeded from its entropy source (Intel's "Digital
    // Random Number Generator", AMD's equivalent). It is the source behind
    // System.Security.Cryptography.RandomNumberGenerator in the kernel, and in
    // every app through the service table (AppServiceTable.RandomFillAddress).
    //
    // RDRAND may answer "no data this time" (CF clear) when the DRBG is being
    // drained faster than it reseeds; Intel's guidance is to retry, and to give
    // up after ten failures in a row, which then means the hardware is broken.
    // A CPU without the instruction (CPUID.1:ECX bit 30 clear — QEMU's plain
    // qemu64 model, for one) has no source here at all: the fill says so, and
    // the generator above throws rather than hand out predictable bytes.
    internal static unsafe class HardwareRandom
    {
        private const int RetriesPerQword = 10;

        // 0 not asked yet, 1 present, 2 absent.
        private static int s_present;

        public static bool IsAvailable
        {
            get
            {
                if (s_present == 0)
                {
                    uint* regs = stackalloc uint[4];
                    bool has = X64Asm.Cpuid(1, 0, regs) && (regs[2] & (1u << 30)) != 0;
                    s_present = has ? 1 : 2;
                }
                return s_present == 1;
            }
        }

        /// <summary>
        /// Fills <paramref name="count"/> bytes. 0 when done; Unsupported
        /// without RDRAND, DeviceError when it kept refusing — the
        /// AppServiceStatus values, since apps call this through the table.
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static int Fill(byte* buffer, int count)
        {
            if (count <= 0) return 0;
            if (buffer == null) return (int)OS.Kernel.Process.AppServiceStatus.InvalidParameter;
            if (!IsAvailable) return (int)OS.Kernel.Process.AppServiceStatus.Unsupported;

            int at = 0;
            while (at < count)
            {
                if (!Next(out ulong value)) return (int)OS.Kernel.Process.AppServiceStatus.DeviceError;
                for (int i = 0; i < 8 && at < count; i++, at++)
                {
                    buffer[at] = (byte)value;
                    value >>= 8;
                }
            }
            return 0;
        }

        private static bool Next(out ulong value)
        {
            for (int attempt = 0; attempt < RetriesPerQword; attempt++)
                if (X64Asm.RdRand(out value)) return true;
            value = 0;
            return false;
        }

        /// <summary>Makes this the kernel's source for RandomNumberGenerator.</summary>
        public static void Install()
        {
            // Place the stub now, on the boot thread, rather than on the first
            // caller's — two first callers would write it at once.
            if (IsAvailable) X64Asm.RdRand(out _);
            SharpOS.Std.NoRuntime.SystemRandom.Install(&Fill);
        }
    }
}
