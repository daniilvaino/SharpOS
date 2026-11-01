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

        // MemoryCopy (step201): as BCL — a copy between raw memory, the size
        // checked against the destination, overlap allowed (Memmove).
        [CLSCompliant(false)]
        public static unsafe void MemoryCopy(void* source, void* destination, long destinationSizeInBytes, long sourceBytesToCopy)
        {
            if (sourceBytesToCopy > destinationSizeInBytes)
                throw new ArgumentOutOfRangeException(nameof(sourceBytesToCopy));
            Memmove(ref *(byte*)destination, ref *(byte*)source, checked((nuint)sourceBytesToCopy));
        }

        [CLSCompliant(false)]
        public static unsafe void MemoryCopy(void* source, void* destination, ulong destinationSizeInBytes, ulong sourceBytesToCopy)
        {
            if (sourceBytesToCopy > destinationSizeInBytes)
                throw new ArgumentOutOfRangeException(nameof(sourceBytesToCopy));
            Memmove(ref *(byte*)destination, ref *(byte*)source, checked((nuint)sourceBytesToCopy));
        }

        /// <summary>
        /// Byte-wise block copy between arrays. Offsets and count are in
        /// BYTES, not elements — the BCL signature's oldest trap, preserved
        /// because the point of this method is that BCL code compiles onto it.
        /// </summary>
        /// <remarks>
        /// Cut from BCL: only single-dimension arrays of primitives are
        /// supported (any primitive since step223). The real method also accepts multidimensional arrays,
        /// whose element storage sits behind a different object layout that
        /// this implementation does not decode; those throw rather than copy
        /// the wrong bytes silently. (Fami's savestate helpers pass byte[,] and
        /// are the reason this is stated rather than assumed — they compile,
        /// and would throw if a savestate were ever taken.)
        /// </remarks>
        public static void BlockCopy(Array src, int srcOffset, Array dst, int dstOffset, int count)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (dst == null) throw new ArgumentNullException(nameof(dst));
            if (srcOffset < 0) throw new ArgumentOutOfRangeException(nameof(srcOffset));
            if (dstOffset < 0) throw new ArgumentOutOfRangeException(nameof(dstOffset));
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));

            // Any single-dimension array of primitives, as BCL (until step223
            // only byte[] — RijndaelManagedTransform copies int[]): lengths in
            // bytes from the element size the MethodTable carries.
            long srcBytes = PrimitiveByteLength(src, nameof(src));
            long dstBytes = PrimitiveByteLength(dst, nameof(dst));
            if (srcOffset + (long)count > srcBytes || dstOffset + (long)count > dstBytes)
                throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            if (count == 0) return;

            unsafe
            {
                byte* srcData = *(byte**)Unsafe.AsPointer(ref src) + 16;
                byte* dstData = *(byte**)Unsafe.AsPointer(ref dst) + 16;
                Memmove(ref *(dstData + dstOffset), ref *(srcData + srcOffset), (nuint)count);
            }
        }

        public static int ByteLength(Array array) => checked((int)PrimitiveByteLength(array, nameof(array)));

        // The array's size in bytes when it is a single-dimension array of a
        // primitive type; ArgumentException otherwise, as BCL words it.
        private static unsafe long PrimitiveByteLength(Array array, string paramName)
        {
            if (array.Rank != 1 || array.GetLowerBound(0) != 0)
                throw new NotSupportedException("Buffer.BlockCopy supports single-dimension arrays only.");
            var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)*(void**)*(byte**)Unsafe.AsPointer(ref array);
            var element = mt->RelatedType;
            SharpOS.Std.NoRuntime.GcEETypeElementType kind = element->ElementType;
            if (kind < SharpOS.Std.NoRuntime.GcEETypeElementType.Boolean || kind > SharpOS.Std.NoRuntime.GcEETypeElementType.Double)
                throw new ArgumentException("Object must be an array of primitives.", paramName);
            return (long)array.Length * *(ushort*)mt;
        }
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
