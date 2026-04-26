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

        // ---- IndexOf / LastIndexOf ----
        // Generic versions go through IEquatable<T> via shared-generic
        // interface dispatch (step 32). For element types that don't
        // implement IEquatable<T>, callers should use the non-constrained
        // search themselves; we don't ship the object-based search to keep
        // the surface narrow.

        public static int IndexOf<T>(T[] array, T value) where T : IEquatable<T>
        {
            if (array == null) return -1;
            return IndexOf(array, value, 0, array.Length);
        }

        public static int IndexOf<T>(T[] array, T value, int startIndex) where T : IEquatable<T>
        {
            if (array == null) return -1;
            return IndexOf(array, value, startIndex, array.Length - startIndex);
        }

        public static int IndexOf<T>(T[] array, T value, int startIndex, int count) where T : IEquatable<T>
        {
            if (array == null) return -1;
            if (startIndex < 0 || count < 0 || startIndex + count > array.Length) return -1;
            int end = startIndex + count;
            for (int i = startIndex; i < end; i++)
            {
                if (value.Equals(array[i])) return i;
            }
            return -1;
        }

        public static int LastIndexOf<T>(T[] array, T value) where T : IEquatable<T>
        {
            if (array == null) return -1;
            if (array.Length == 0) return -1;
            return LastIndexOf(array, value, array.Length - 1, array.Length);
        }

        public static int LastIndexOf<T>(T[] array, T value, int startIndex) where T : IEquatable<T>
        {
            if (array == null) return -1;
            return LastIndexOf(array, value, startIndex, startIndex + 1);
        }

        public static int LastIndexOf<T>(T[] array, T value, int startIndex, int count) where T : IEquatable<T>
        {
            if (array == null) return -1;
            if (startIndex < 0 || count < 0 || startIndex - count + 1 < 0) return -1;
            int end = startIndex - count + 1;
            for (int i = startIndex; i >= end; i--)
            {
                if (value.Equals(array[i])) return i;
            }
            return -1;
        }

        // ---- Reverse ----

        public static void Reverse<T>(T[] array)
        {
            if (array == null || array.Length <= 1) return;
            Reverse(array, 0, array.Length);
        }

        public static void Reverse<T>(T[] array, int index, int length)
        {
            if (array == null || length <= 1) return;
            if (index < 0 || index + length > array.Length) return;
            int i = index;
            int j = index + length - 1;
            while (i < j)
            {
                T tmp = array[i];
                array[i] = array[j];
                array[j] = tmp;
                i++; j--;
            }
        }

        // ---- Resize ----
        // Allocates a fresh array of the new size, copies min(old, new)
        // elements over, swaps the ref. Mirrors BCL Array.Resize semantics.

        public static void Resize<T>(ref T[] array, int newSize)
        {
            if (newSize < 0) return;
            T[] oldArray = array;
            if (oldArray == null)
            {
                array = new T[newSize];
                return;
            }
            if (oldArray.Length == newSize) return;

            T[] newArray = new T[newSize];
            int copyLen = oldArray.Length < newSize ? oldArray.Length : newSize;
            for (int i = 0; i < copyLen; i++) newArray[i] = oldArray[i];
            array = newArray;
        }

        // ---- Clear ----
        // BCL has Clear(Array, int, int) and Clear<T>(T[], int, int).
        // We provide both shapes, both write `default` per slot.

        public static void Clear<T>(T[] array, int index, int length)
        {
            if (array == null || length <= 0) return;
            if (index < 0 || index + length > array.Length) return;
            int end = index + length;
            for (int i = index; i < end; i++) array[i] = default;
        }
    }
}
