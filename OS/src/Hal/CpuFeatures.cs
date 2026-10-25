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

        /// <summary>The AVX register state is on (XCR0) and interrupts save it.</summary>
        public static bool AvxState;

        /// <param name="avxState">XCR0 enables AVX state and interrupts save it
        /// (BootSequence.ConfigureXcr0) — VEX instructions, vfmadd included,
        /// raise #UD without it.</param>
        public static void Initialize(bool avxState)
        {
            uint* regs = stackalloc uint[4];
            ulong features = 0;
            bool fmaCpu = false;
            if (X64Asm.Cpuid(1, 0, regs))
            {
                uint ecx = regs[2];
                if ((ecx & (1u << 19)) != 0) features |= LibmPatcher.FeatureSse41;
                fmaCpu = (ecx & (1u << 12)) != 0;
                if (fmaCpu && avxState) features |= LibmPatcher.FeatureFma;
            }
            Value = features;
            AvxState = avxState;
            LibmPatcher.Apply(features);

            Log.Begin(LogLevel.Info);
            Console.Write("cpu features: sse4.1=");
            Console.Write((features & LibmPatcher.FeatureSse41) != 0 ? "yes" : "no");
            Console.Write(" fma=");
            Console.Write((features & LibmPatcher.FeatureFma) != 0 ? "yes" : fmaCpu ? "no (AVX state off)" : "no");
            Console.Write("; libm entries replaced: ");
            Console.WriteUInt((uint)LibmPatcher.Replaced);
            Log.EndLine();
        }
    }
}
