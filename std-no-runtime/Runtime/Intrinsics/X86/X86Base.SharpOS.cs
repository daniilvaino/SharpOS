// X86Base's one non-intrinsic member, CpuId: NativeAOT routes it to the
// runtime (RuntimeImports.RhCpuIdEx, X86Base.NativeAot.cs); here each tier
// sets where CPUID runs — the kernel its X64Asm shellcode. A tier that set
// nothing throws, as BCL does on a platform without the instruction.

namespace System.Runtime.Intrinsics.X86
{
    public abstract partial class X86Base
    {
        private static unsafe void __cpuidex(int* cpuInfo, int functionId, int subFunctionId)
        {
            var cpuid = SharpOS.Std.NoRuntime.HardwareIntrinsicFlags.CpuId;
            if (cpuid == null) throw new PlatformNotSupportedException();
            cpuid(cpuInfo, functionId, subFunctionId);
        }
    }
}
