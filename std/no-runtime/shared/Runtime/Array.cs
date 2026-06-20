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

using System.Collections.Generic;
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

        // ---- Sort ----
        // Ported from dotnet/runtime
        // src/libraries/System.Private.CoreLib/src/System/Collections/
        // Generic/ArraySortHelper.cs (IntrospectiveSort).
        //
        // Introsort = quicksort with two safeguards:
        //   - partitions ≤ 16 elements fall through to insertion sort
        //     (best on small inputs, no recursion overhead),
        //   - recursion depth capped at 2*log2(length); on overflow we
        //     switch to heapsort to enforce O(n log n) worst-case (defeats
        //     adversarial inputs that would otherwise hit quicksort O(n²)).
        // Pivot is median-of-three (first / middle / last). Same algorithm
        // as Array.Sort in shipping .NET — only difference is we drop the
        // ThrowHelper.ThrowInvalidOperationException on comparer-throws
        // (we have no real exception unwind on the kernel tier yet).

        private const int IntrosortSizeThreshold = 16;

        // ---- Overload surface (matches BCL) ----

        public static void Sort<T>(T[] array) where T : IComparable<T>
        {
            if (array == null || array.Length < 2) return;
            ComparableIntroSort(array, 0, array.Length - 1, 2 * Log2((uint)array.Length));
        }

        public static void Sort<T>(T[] array, int index, int length) where T : IComparable<T>
        {
            if (array == null || length < 2) return;
            if (index < 0 || index + length > array.Length) return;
            ComparableIntroSort(array, index, index + length - 1, 2 * Log2((uint)length));
        }

        public static void Sort<T>(T[] array, IComparer<T> comparer)
        {
            if (array == null || array.Length < 2) return;
            if (comparer == null) return;
            ComparerIntroSort(array, 0, array.Length - 1, 2 * Log2((uint)array.Length), comparer);
        }

        public static void Sort<T>(T[] array, int index, int length, IComparer<T> comparer)
        {
            if (array == null || length < 2) return;
            if (index < 0 || index + length > array.Length) return;
            if (comparer == null) return;
            ComparerIntroSort(array, index, index + length - 1, 2 * Log2((uint)length), comparer);
        }

        // Comparison<T> overload — wraps the delegate in an IComparer<T>
        // adapter so the engine stays uniform. If ILC can't codegen real
        // managed delegates yet, callers will need to switch to the
        // IComparer<T> overload directly.
        public static void Sort<T>(T[] array, Comparison<T> comparison)
        {
            if (array == null || array.Length < 2) return;
            if (comparison == null) return;
            var cmp = new ComparisonAdapter<T>(comparison);
            ComparerIntroSort(array, 0, array.Length - 1, 2 * Log2((uint)array.Length), cmp);
        }

        // ---- log2 (integer) ----

        private static int Log2(uint n)
        {
            int k = 0;
            while ((n >>= 1) != 0) k++;
            return k;
        }

        // ---- IComparer<T> engine ----

        private static void ComparerIntroSort<T>(T[] keys, int lo, int hi, int depthLimit, IComparer<T> comparer)
        {
            while (hi > lo)
            {
                int partitionSize = hi - lo + 1;
                if (partitionSize <= IntrosortSizeThreshold)
                {
                    if (partitionSize == 1) return;
                    if (partitionSize == 2)
                    {
                        ComparerSwapIfGreater(keys, comparer, lo, hi);
                        return;
                    }
                    if (partitionSize == 3)
                    {
                        ComparerSwapIfGreater(keys, comparer, lo, hi - 1);
                        ComparerSwapIfGreater(keys, comparer, lo, hi);
                        ComparerSwapIfGreater(keys, comparer, hi - 1, hi);
                        return;
                    }
                    ComparerInsertionSort(keys, lo, hi, comparer);
                    return;
                }

                if (depthLimit == 0)
                {
                    ComparerHeapSort(keys, lo, hi, comparer);
                    return;
                }
                depthLimit--;

                int p = ComparerPickPivotAndPartition(keys, lo, hi, comparer);
                ComparerIntroSort(keys, p + 1, hi, depthLimit, comparer);
                hi = p - 1;
            }
        }

        private static int ComparerPickPivotAndPartition<T>(T[] keys, int lo, int hi, IComparer<T> comparer)
        {
            int middle = lo + ((hi - lo) >> 1);
            // median-of-three: arrange lo ≤ middle ≤ hi
            ComparerSwapIfGreater(keys, comparer, lo, middle);
            ComparerSwapIfGreater(keys, comparer, lo, hi);
            ComparerSwapIfGreater(keys, comparer, middle, hi);

            T pivot = keys[middle];
            Swap(keys, middle, hi - 1);
            int left = lo, right = hi - 1;

            while (left < right)
            {
                while (comparer.Compare(keys[++left], pivot) < 0) { }
                while (comparer.Compare(pivot, keys[--right]) < 0) { }
                if (left >= right) break;
                Swap(keys, left, right);
            }
            if (left != hi - 1) Swap(keys, left, hi - 1);
            return left;
        }

        private static void ComparerHeapSort<T>(T[] keys, int lo, int hi, IComparer<T> comparer)
        {
            int n = hi - lo + 1;
            for (int i = n / 2; i >= 1; i--) ComparerDownHeap(keys, i, n, lo, comparer);
            for (int i = n; i > 1; i--)
            {
                Swap(keys, lo, lo + i - 1);
                ComparerDownHeap(keys, 1, i - 1, lo, comparer);
            }
        }

        private static void ComparerDownHeap<T>(T[] keys, int i, int n, int lo, IComparer<T> comparer)
        {
            T d = keys[lo + i - 1];
            while (i <= n / 2)
            {
                int child = 2 * i;
                if (child < n && comparer.Compare(keys[lo + child - 1], keys[lo + child]) < 0) child++;
                if (comparer.Compare(d, keys[lo + child - 1]) >= 0) break;
                keys[lo + i - 1] = keys[lo + child - 1];
                i = child;
            }
            keys[lo + i - 1] = d;
        }

        private static void ComparerInsertionSort<T>(T[] keys, int lo, int hi, IComparer<T> comparer)
        {
            for (int i = lo; i < hi; i++)
            {
                int j = i;
                T t = keys[i + 1];
                while (j >= lo && comparer.Compare(t, keys[j]) < 0)
                {
                    keys[j + 1] = keys[j];
                    j--;
                }
                keys[j + 1] = t;
            }
        }

        private static void ComparerSwapIfGreater<T>(T[] keys, IComparer<T> comparer, int a, int b)
        {
            if (a != b && comparer.Compare(keys[a], keys[b]) > 0)
            {
                T tmp = keys[a];
                keys[a] = keys[b];
                keys[b] = tmp;
            }
        }

        // ---- IComparable<T> engine (no comparer indirection — direct call) ----
        // Duplicated to keep the IComparable<T> path comparer-free; lets
        // primitive-element sorts avoid a virtual call per compare and
        // dodges the IComparer<T> wrapper allocation for the no-arg
        // Sort<T>(T[]) overload entirely.

        private static void ComparableIntroSort<T>(T[] keys, int lo, int hi, int depthLimit) where T : IComparable<T>
        {
            while (hi > lo)
            {
                int partitionSize = hi - lo + 1;
                if (partitionSize <= IntrosortSizeThreshold)
                {
                    if (partitionSize == 1) return;
                    if (partitionSize == 2)
                    {
                        ComparableSwapIfGreater(keys, lo, hi);
                        return;
                    }
                    if (partitionSize == 3)
                    {
                        ComparableSwapIfGreater(keys, lo, hi - 1);
                        ComparableSwapIfGreater(keys, lo, hi);
                        ComparableSwapIfGreater(keys, hi - 1, hi);
                        return;
                    }
                    ComparableInsertionSort(keys, lo, hi);
                    return;
                }

                if (depthLimit == 0)
                {
                    ComparableHeapSort(keys, lo, hi);
                    return;
                }
                depthLimit--;

                int p = ComparablePickPivotAndPartition(keys, lo, hi);
                ComparableIntroSort(keys, p + 1, hi, depthLimit);
                hi = p - 1;
            }
        }

        private static int ComparablePickPivotAndPartition<T>(T[] keys, int lo, int hi) where T : IComparable<T>
        {
            int middle = lo + ((hi - lo) >> 1);
            ComparableSwapIfGreater(keys, lo, middle);
            ComparableSwapIfGreater(keys, lo, hi);
            ComparableSwapIfGreater(keys, middle, hi);

            T pivot = keys[middle];
            Swap(keys, middle, hi - 1);
            int left = lo, right = hi - 1;

            while (left < right)
            {
                while (keys[++left].CompareTo(pivot) < 0) { }
                while (pivot.CompareTo(keys[--right]) < 0) { }
                if (left >= right) break;
                Swap(keys, left, right);
            }
            if (left != hi - 1) Swap(keys, left, hi - 1);
            return left;
        }

        private static void ComparableHeapSort<T>(T[] keys, int lo, int hi) where T : IComparable<T>
        {
            int n = hi - lo + 1;
            for (int i = n / 2; i >= 1; i--) ComparableDownHeap(keys, i, n, lo);
            for (int i = n; i > 1; i--)
            {
                Swap(keys, lo, lo + i - 1);
                ComparableDownHeap(keys, 1, i - 1, lo);
            }
        }

        private static void ComparableDownHeap<T>(T[] keys, int i, int n, int lo) where T : IComparable<T>
        {
            T d = keys[lo + i - 1];
            while (i <= n / 2)
            {
                int child = 2 * i;
                if (child < n && keys[lo + child - 1].CompareTo(keys[lo + child]) < 0) child++;
                if (d.CompareTo(keys[lo + child - 1]) >= 0) break;
                keys[lo + i - 1] = keys[lo + child - 1];
                i = child;
            }
            keys[lo + i - 1] = d;
        }

        private static void ComparableInsertionSort<T>(T[] keys, int lo, int hi) where T : IComparable<T>
        {
            for (int i = lo; i < hi; i++)
            {
                int j = i;
                T t = keys[i + 1];
                while (j >= lo && t.CompareTo(keys[j]) < 0)
                {
                    keys[j + 1] = keys[j];
                    j--;
                }
                keys[j + 1] = t;
            }
        }

        private static void ComparableSwapIfGreater<T>(T[] keys, int a, int b) where T : IComparable<T>
        {
            if (a != b && keys[a].CompareTo(keys[b]) > 0)
            {
                T tmp = keys[a];
                keys[a] = keys[b];
                keys[b] = tmp;
            }
        }

        private static void Swap<T>(T[] keys, int i, int j)
        {
            T tmp = keys[i];
            keys[i] = keys[j];
            keys[j] = tmp;
        }

        // Adapter from Comparison<T> delegate to IComparer<T>. Used only
        // by the Sort(T[], Comparison<T>) overload so that the engine
        // stays parametrised on IComparer<T> alone.
        private sealed class ComparisonAdapter<T> : IComparer<T>
        {
            private readonly Comparison<T> _comparison;
            public ComparisonAdapter(Comparison<T> comparison) { _comparison = comparison; }
            public int Compare(T x, T y) => _comparison(x, y);
        }
    }
}
