// System.Buffer — thin forwarder to SharpOS.Std.MemoryOps.Memmove. We
// keep `System.Buffer.Memmove` reserved under the canonical name so BCL
// code (StringBuilder, Array.Copy, Encoding, ...) compiles as-is; the
// actual implementation lives under SharpOS.Std because it is the
// simple overlap-aware loop, not BCL's SIMD-optimised version.
//
// Signatures copied verbatim from BCL:
//   src/libraries/System.Private.CoreLib/src/System/Buffer.cs

using System.Runtime.CompilerServices;

namespace System
{
    public static class Buffer
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Memmove(ref byte dest, ref byte src, nuint len)
            => SharpOS.Std.MemoryOps.Memmove(ref dest, ref src, len);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void Memmove<T>(ref T dest, ref T src, nuint len)
            => SharpOS.Std.MemoryOps.Memmove<T>(ref dest, ref src, len);
    }
}

namespace SharpOS.Std
{
    // Simple overlap-aware byte copy. GC non-moving, so fixed pointers
    // we take here stay valid for the duration of the call. Callers
    // pass a `ref` and a byte count; we don't assert any alignment.
    public static class MemoryOps
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Memmove(ref byte dest, ref byte src, nuint len)
        {
            fixed (byte* pDest = &dest)
            fixed (byte* pSrc = &src)
            {
                if (pDest < pSrc || pDest >= pSrc + len)
                {
                    // No overlap OR dest is below src — forward copy.
                    for (nuint i = 0; i < len; i++) pDest[i] = pSrc[i];
                }
                else
                {
                    // Overlapping with dest above src — backward copy.
                    for (nuint i = len; i > 0; i--) pDest[i - 1] = pSrc[i - 1];
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Memmove<T>(ref T dest, ref T src, nuint elementCount)
        {
            int elemSize = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
            nuint byteCount = elementCount * (nuint)elemSize;
            Memmove(
                ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref dest),
                ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref src),
                byteCount);
        }
    }
}
