// LibmPatcher — replaces SharpLibm's replaceable entry points with one-
// instruction bodies when the CPU has the instruction (step198).
//
// SharpLibm's base bodies are SSE2 bit manipulation and software FMA, right
// on any x86-64. Some of them are a single instruction on newer CPUs, and
// SharpLibm keeps exactly those out of line ([MethodImpl(NoInlining)]) so that
// overwriting the entry speeds up every caller, the library's own included —
// all FMAs inside the CORE-MATH port go through Libm.fma.
//
//   SSE4.1: floor, ceil, trunc, rint, nearbyint (and the float forms) →
//           roundsd/roundss xmm0, xmm0, mode; ret
//   FMA3 (with AVX state enabled in XCR0): fma, fmaf →
//           vfmadd213sd/ss xmm0, xmm1, xmm2; ret   (xmm0 = x·y + z)
//
// The features come from whoever can run CPUID: the kernel probes them, and
// an app gets them through AppServiceTable.CpuFeatures. Each image patches its
// own copy of SharpLibm. Code pages of both are writable (the other byte
// patchers rely on that too); like them, no serialising instruction follows
// the write (limits doc §9).

using System.Runtime.CompilerServices;
using SharpLibm;

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class LibmPatcher
    {
        /// <summary>SSE4.1 (CPUID.1:ECX bit 19): roundsd / roundss.</summary>
        public const ulong FeatureSse41 = 1;

        /// <summary>FMA3 usable: CPUID FMA, AVX and OSXSAVE, and XCR0 enabling SSE and AVX state.</summary>
        public const ulong FeatureFma = 2;

        /// <summary>How many entry points the last Apply replaced.</summary>
        public static int Replaced;

        public static void Apply(ulong features)
        {
            int n = 0;
            if ((features & FeatureSse41) != 0)
            {
                // roundsd/roundss immediate: bits 1:0 the mode (0 nearest, 1 down,
                // 2 up, 3 toward zero), bit 2 "use MXCSR's mode", bit 3 suppress
                // the precision exception (nearbyint; the CLI raises none anyway).
                n += Round((byte*)(delegate*<double, double>)&Libm.floor, single: false, 0x09);
                n += Round((byte*)(delegate*<double, double>)&Libm.ceil, single: false, 0x0A);
                n += Round((byte*)(delegate*<double, double>)&Libm.trunc, single: false, 0x0B);
                n += Round((byte*)(delegate*<double, double>)&Libm.rint, single: false, 0x04);
                n += Round((byte*)(delegate*<double, double>)&Libm.nearbyint, single: false, 0x0C);
                n += Round((byte*)(delegate*<float, float>)&Libm.floorf, single: true, 0x09);
                n += Round((byte*)(delegate*<float, float>)&Libm.ceilf, single: true, 0x0A);
                n += Round((byte*)(delegate*<float, float>)&Libm.truncf, single: true, 0x0B);
                n += Round((byte*)(delegate*<float, float>)&Libm.rintf, single: true, 0x04);
                n += Round((byte*)(delegate*<float, float>)&Libm.nearbyintf, single: true, 0x0C);
            }
            if ((features & FeatureFma) != 0)
            {
                // VEX.LIG.66.0F38.W1 A9 /r: vfmadd213sd xmm0, xmm1, xmm2 (W0: ss).
                n += Fma((byte*)(delegate*<double, double, double, double>)&Libm.fma, 0xF1);
                n += Fma((byte*)(delegate*<float, float, float, float>)&Libm.fmaf, 0x71);
            }
            Replaced = n;
        }

        // 66 0F 3A 0B|0A C0 ib   roundsd|roundss xmm0, xmm0, ib
        // C3                     ret
        private static int Round(byte* entry, bool single, byte mode)
        {
            if (entry == null) return 0;
            entry[0] = 0x66; entry[1] = 0x0F; entry[2] = 0x3A;
            entry[3] = single ? (byte)0x0A : (byte)0x0B;
            entry[4] = 0xC0; entry[5] = mode;
            entry[6] = 0xC3;
            return 1;
        }

        // C4 E2 F1|71 A9 C2   vfmadd213sd|ss xmm0, xmm1, xmm2
        // C3                  ret
        private static int Fma(byte* entry, byte vexW)
        {
            if (entry == null) return 0;
            entry[0] = 0xC4; entry[1] = 0xE2; entry[2] = vexW;
            entry[3] = 0xA9; entry[4] = 0xC2;
            entry[5] = 0xC3;
            return 1;
        }
    }
}
