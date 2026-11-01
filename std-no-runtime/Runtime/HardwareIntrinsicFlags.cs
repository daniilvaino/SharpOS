// What ILC's optimistic IsSupported reads (release 0.1).
//
// Without an instruction set given, ILC 8 builds for SSE2 and compiles the
// branches of SSE3..SSE4.2, AES, PCLMULQDQ, POPCNT, LZCNT, MOVBE and SERIALIZE
// both ways: X.IsSupported becomes (g_cpuFeatures & flag) != 0
// (HardwareIntrinsicHelpers.Aot.cs). The stock runtime fills g_cpuFeatures at
// startup with minipal_getcpufeatures; here the kernel computes the same
// flags (Compute, a port of src/native/minipal/cpufeatures.c, .NET 8, MIT),
// and every image writes them into its own g_cpuFeatures.

namespace SharpOS.Std.NoRuntime
{
    public static unsafe class HardwareIntrinsicFlags
    {
        // XArchIntrinsicConstants (src/native/minipal/cpufeatures.h).
        public const int Aes = 0x0001;
        public const int Pclmulqdq = 0x0002;
        public const int Sse3 = 0x0004;
        public const int Ssse3 = 0x0008;
        public const int Sse41 = 0x0010;
        public const int Sse42 = 0x0020;
        public const int Popcnt = 0x0040;
        public const int Avx = 0x0080;
        public const int Fma = 0x0100;
        public const int Avx2 = 0x0200;
        public const int Bmi1 = 0x0400;
        public const int Bmi2 = 0x0800;
        public const int Lzcnt = 0x1000;
        public const int AvxVnni = 0x2000;
        public const int Movbe = 0x4000;
        public const int Serialize = 0x2000000;
        public const int VectorT128 = 0x4000000;
        public const int VectorT256 = 0x8000000;

        /// <summary>Where X86Base.CpuId runs: (cpuInfo[4], function, subfunction). Null — not on this tier.</summary>
        public static delegate* unmanaged<int*, int, int, void> CpuId;

        /// <summary>
        /// The flags minipal_getcpufeatures gives on x64. <paramref name="avxState"/>:
        /// the OS saves the AVX registers (XCR0, which only the kernel can read).
        /// AVX-512 is left out: the kernel's XCR0 holds x87|SSE|AVX only
        /// (ConfigureXcr0), so its state is neither enabled nor saved across
        /// interrupts — minipal itself sets none of it without XCR0[7:5].
        /// </summary>
        public static int Compute(bool avxState)
        {
            if (CpuId == null) return 0;
            int* r = stackalloc int[4];
            const int EAX = 0, EBX = 1, ECX = 2, EDX = 3;
            int result = 0;

            CpuId(r, 0, 0);
            uint maxCpuId = (uint)r[EAX];
            if (maxCpuId >= 1)
            {
                CpuId(r, 1, 0);
                const int requiredBaselineEdxFlags = (1 << 25) | (1 << 26);     // SSE, SSE2
                if ((r[EDX] & requiredBaselineEdxFlags) == requiredBaselineEdxFlags)
                {
                    result |= VectorT128;
                    if ((r[ECX] & (1 << 25)) != 0) result |= Aes;
                    if ((r[ECX] & (1 << 1)) != 0) result |= Pclmulqdq;
                    if ((r[ECX] & (1 << 0)) != 0)
                    {
                        result |= Sse3;
                        if ((r[ECX] & (1 << 9)) != 0)
                        {
                            result |= Ssse3;
                            if ((r[ECX] & (1 << 19)) != 0)
                            {
                                result |= Sse41;
                                if ((r[ECX] & (1 << 20)) != 0)
                                {
                                    result |= Sse42;
                                    if ((r[ECX] & (1 << 22)) != 0) result |= Movbe;
                                    if ((r[ECX] & (1 << 23)) != 0) result |= Popcnt;

                                    const int requiredAvxEcxFlags = (1 << 27) | (1 << 28);   // OSXSAVE, AVX
                                    if ((r[ECX] & requiredAvxEcxFlags) == requiredAvxEcxFlags && avxState)
                                    {
                                        result |= Avx;
                                        if ((r[ECX] & (1 << 12)) != 0) result |= Fma;
                                        if (maxCpuId >= 7)
                                        {
                                            CpuId(r, 7, 0);
                                            if ((r[EBX] & (1 << 5)) != 0)
                                            {
                                                result |= Avx2 | VectorT256;
                                                CpuId(r, 7, 1);
                                                if ((r[EAX] & (1 << 4)) != 0) result |= AvxVnni;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (maxCpuId >= 7)
                {
                    CpuId(r, 7, 0);
                    if ((r[EBX] & (1 << 3)) != 0) result |= Bmi1;
                    if ((r[EBX] & (1 << 8)) != 0) result |= Bmi2;
                    if ((r[EDX] & (1 << 14)) != 0) result |= Serialize;
                }
            }

            CpuId(r, unchecked((int)0x80000000), 0);
            uint maxCpuIdEx = (uint)r[EAX];
            if (maxCpuIdEx >= 0x80000001)
            {
                CpuId(r, unchecked((int)0x80000001), 0);
                if ((r[ECX] & (1 << 5)) != 0) result |= Lzcnt;
            }
            return result;
        }
    }
}
