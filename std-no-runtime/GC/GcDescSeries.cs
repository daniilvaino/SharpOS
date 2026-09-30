// GC descriptor series — describes reference fields in a type's layout.
//
// Adapted from Kevin Gosse's ManagedDotnetGC (MIT):
//   https://github.com/kevingosse/ManagedDotnetGC
//
// GCDescSeries is stored in negative space relative to the MethodTable pointer.
// Format (from dotnet/runtime):
//   mt[-1] = series count
//   mt[-2..-(count+1)] = GCDescSeries entries { Size, Offset }
// For arrays of value types with embedded refs:
//   mt[-2] = offset to first element
//   mt[-3..-(2+n)] = ValSerieItem { Nptrs, Skip }

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct GcDescSeries
    {
        public nint Size;
        public nint Offset;
    }

    // ValSerieItem is packed: high half = Skip, low half = Nptrs.
    // On 64-bit: Nptrs = low 32 bits, Skip = high 32 bits.
    [StructLayout(LayoutKind.Sequential)]
    internal struct ValSerieItem
    {
        public nint Packed;

        public uint Nptrs => (uint)((long)Packed & 0xFFFFFFFF);
        public uint Skip => (uint)(((long)Packed >> 32) & 0xFFFFFFFF);
    }
}
