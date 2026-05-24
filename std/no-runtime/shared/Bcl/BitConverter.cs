// System.BitConverter — minimal subset ported from dotnet/runtime.
// BCL version is large (float/double bit-casts, ToString hex-pretty,
// GetBytes overloads). Kernel-tier currently needs only IsLittleEndian
// (BinaryPrimitives uses it as a compile-time const branch).
//
// Cuts:
//   - Int{16,32,64}BitsToHalf/Single/Double — need Half/Single/Double
//     bit-cast intrinsics we don't have; revisit when float runtime lands.
//   - GetBytes / ToBoolean / ToInt* / ToDouble — string-side, no
//     consumer in our std yet.
//   - ToString(...) — Halt's text formatting path, no consumer yet.
//
// Endianness is hard-coded little-endian: kernel target is x86_64.
// Adding ARM/big-endian targets later flips this to a runtime check.

namespace System
{
    public static class BitConverter
    {
        public const bool IsLittleEndian = true;
    }
}
