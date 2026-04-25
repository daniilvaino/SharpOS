// System.Array — statics needed by BCL-verbatim code. Shape mirrors BCL:
//   Empty<T>() — returns a T[0].
//   Copy(Array, Array, int) + offset-based overload — byte-level copy
//     between two arrays of the same element size.
//
// Implementation notes:
//
// - Empty<T>() in BCL caches the instance. We can't (static lazy field =
//   ClassConstructorRunner trap) so each call allocates. Behavior
//   identical to callers.
// - Copy reads ComponentSize from the source array's MethodTable (first
//   ushort of the MT — verified against our GcMethodTable layout) and
//   copies `length * ComponentSize` bytes starting at offset 16 (after
//   MT* and Length fields). Works as long as src and dst have the same
//   element type; mismatched types would be undefined, which matches
//   BCL's "ArrayTypeMismatchException" response conceptually even
//   though we can't throw.

using System.Runtime.CompilerServices;

namespace System
{
    public abstract partial class Array
    {
        public static T[] Empty<T>() => new T[0];

        public static unsafe void Copy(Array sourceArray, Array destinationArray, int length)
        {
            Copy(sourceArray, 0, destinationArray, 0, length);
        }

        public static unsafe void Copy(Array sourceArray, int sourceIndex, Array destinationArray, int destinationIndex, int length)
        {
            if (length <= 0) return;
            Array src = sourceArray;
            Array dst = destinationArray;

            nint srcObj = *(nint*)Unsafe.AsPointer(ref src);
            nint dstObj = *(nint*)Unsafe.AsPointer(ref dst);

            // ComponentSize at offset 0 of MT. Zero for non-component-sized
            // types, in which case Copy is a no-op here.
            ushort elemSize = *(ushort*)srcObj;
            if (elemSize == 0) return;

            byte* srcStart = (byte*)srcObj + 16 + (uint)sourceIndex * elemSize;
            byte* dstStart = (byte*)dstObj + 16 + (uint)destinationIndex * elemSize;

            nuint bytesToCopy = (nuint)length * elemSize;
            // Use our Memmove for overlap safety — same array src/dst is
            // a legitimate caller pattern (e.g. List<T>.RemoveAt shift).
            SharpOS.Std.MemoryOps.Memmove(ref *dstStart, ref *srcStart, bytesToCopy);
        }

        public static unsafe void Copy<T>(T[] sourceArray, T[] destinationArray, int length)
        {
            if (length <= 0) return;
            for (int i = 0; i < length; i++) destinationArray[i] = sourceArray[i];
        }
    }
}
