using OS.Kernel.Diagnostics;
using SharpOS.Std.NoRuntime;

namespace OS.Hal
{
    /// <summary>
    /// CPU features beyond the x86-64 baseline that code may use, as
    /// LibmPatcher bits (step198). Probed once at boot; the kernel patches its
    /// own SharpLibm with them and hands them to every app
    /// (AppServiceTable.CpuFeatures), which patches its copy.
    /// </summary>
    internal static unsafe class CpuFeatures
    {
        public static ulong Value;

        public static void Initialize()
        {
            uint* regs = stackalloc uint[4];
            ulong features = 0;
            if (X64Asm.Cpuid(1, 0, regs))
            {
                uint ecx = regs[2];
                if ((ecx & (1u << 19)) != 0) features |= LibmPatcher.FeatureSse41;
                // FMA stays off: the kernel locks XCR0 to x87|SSE (BootSequence,
                // FXSAVE context switches), so AVX state — and every VEX
                // instruction, vfmadd included — is disabled and would #UD.
            }
            Value = features;
            LibmPatcher.Apply(features);

            Log.Begin(LogLevel.Info);
            Console.Write("cpu features: sse4.1=");
            Console.Write((features & LibmPatcher.FeatureSse41) != 0 ? "yes" : "no");
            Console.Write(" fma=no (XCR0 x87|SSE); libm entries replaced: ");
            Console.WriteUInt((uint)LibmPatcher.Replaced);
            Log.EndLine();
        }
    }
}
