// System.Array as a non-generic collection: ICloneable, IList (→ ICollection →
// IEnumerable), SetValue, IndexOf(Array, object), GetEnumerator. Ported from
// dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Array.cs
//   src/libraries/System.Private.CoreLib/src/System/Array.Enumerators.cs (ArrayEnumerator)
// Member bodies are the BCL's; IStructuralComparable/IStructuralEquatable are
// not ported.
//
// Why now: C# turns a collection expression aimed at IEnumerable<T> or
// IReadOnlyList<T> into a list type of its own whose ICollection.CopyTo hands
// its T[] on as ICollection. With System.Array implementing no ICollection
// there was no such conversion, and the compiler crashed emitting it
// ("Unexpected value 'NoConversion'") instead of reporting anything.
//
// SharpOS: SetValue does what NativeAOT's InternalSetValue does without its
// type loader — a reference is stored when the element type accepts it, a
// value type when the box is of exactly the element type (the widening of
// primitives the BCL also allows, int into a long[], is not done here: an
// InvalidCastException); null sets a value-type element to its default.
// IndexOf compares through Object.Equals over GetValue, which is the BCL's
// answer for every element type, without its per-primitive fast paths.

using System.Collections;
using System.Runtime.CompilerServices;

namespace System
{
    public abstract partial class Array : ICloneable, IList
    {
        // Number of elements in the Array.
        int ICollection.Count => Length;

        // Returns an object appropriate for synchronizing access to this Array.
        public object SyncRoot => this;

        // Is this Array read-only?
        public bool IsReadOnly => false;

        public bool IsFixedSize => true;

        // Is this Array synchronized (i.e., thread-safe)?
        public bool IsSynchronized => false;

        object? IList.this[int index]
        {
            get => GetValue(index);
            set => SetValue(value, index);
        }

        int IList.Add(object? value)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        bool IList.Contains(object? value)
        {
            return IndexOf(this, value) >= this.GetLowerBound(0);
        }

        void IList.Clear()
        {
            Clear(this);
        }

        int IList.IndexOf(object? value)
        {
            return IndexOf(this, value);
        }

        void IList.Insert(int index, object? value)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        void IList.Remove(object? value)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        void IList.RemoveAt(int index)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        public IEnumerator GetEnumerator()
        {
            return new ArrayEnumerator(this);
        }

        public static int IndexOf(Array array, object? value)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            return IndexOf(array, value, array.GetLowerBound(0), array.Length);
        }

        public static int IndexOf(Array array, object? value, int startIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            int lb = array.GetLowerBound(0);
            return IndexOf(array, value, startIndex, array.Length - startIndex + lb);
        }

        public static int IndexOf(Array array, object? value, int startIndex, int count)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (array.Rank != 1) throw new RankException("Only single dimensional arrays are supported for the requested action.");

            int lb = array.GetLowerBound(0);
            if (startIndex < lb || startIndex > array.Length + lb)
                throw new ArgumentOutOfRangeException(nameof(startIndex), "Index was out of range. Must be non-negative and less than or equal to the size of the collection.");
            if (count < 0 || count > array.Length - startIndex + lb)
                throw new ArgumentOutOfRangeException(nameof(count), "Count must be positive and count must refer to a location within the string/array/collection.");

            int endIndex = startIndex + count;
            for (int i = startIndex; i < endIndex; i++)
            {
                object? obj = array.GetValue(i);
                if (obj == null)
                {
                    if (value == null) return i;
                }
                else if (obj.Equals(value))
                {
                    return i;
                }
            }
            // We didn't find the value; return one less than the lower bound.
            return lb - 1;
        }

        public unsafe void SetValue(object? value, int index)
        {
            if (Rank != 1) throw new ArgumentException("Only single dimensional arrays are supported for the requested action.");
            if ((uint)index >= (uint)Length) throw new IndexOutOfRangeException();

            Array self = this;
            byte* obj = *(byte**)Unsafe.AsPointer(ref self);
            var mt = *(SharpOS.Std.NoRuntime.GcMethodTable**)obj;
            ushort elementSize = *(ushort*)mt;
            byte* element = obj + 16 + (nuint)index * elementSize;
            SharpOS.Std.NoRuntime.GcMethodTable* elementType = mt->RelatedType;

            if (!elementType->IsValueType)
            {
                if (value != null)
                {
                    var target = new RuntimeType((IntPtr)elementType);
                    if (!target.IsAssignableFrom(value.GetType()))
                        throw new InvalidCastException("Object cannot be stored in an array of this type.");
                }
                Unsafe.As<byte, object?>(ref *element) = value;  // through the write barrier
                return;
            }

            if (value == null)
            {
                for (int i = 0; i < elementSize; i++) element[i] = 0;
                return;
            }

            object boxed = value;
            byte* box = *(byte**)Unsafe.AsPointer(ref boxed);
            if (*(SharpOS.Std.NoRuntime.GcMethodTable**)box != elementType)
                throw new InvalidCastException("Object cannot be stored in an array of this type.");
            SharpOS.Std.MemoryOps.Memmove(ref *element, ref *(box + 8), elementSize);
        }

        public void SetValue(object? value, long index)
        {
            if (index > int.MaxValue || index < int.MinValue)
                throw new ArgumentOutOfRangeException(nameof(index), "Arrays larger than 2GB are not supported.");
            SetValue(value, (int)index);
        }

        public object? GetValue(long index)
        {
            if (index > int.MaxValue || index < int.MinValue)
                throw new ArgumentOutOfRangeException(nameof(index), "Arrays larger than 2GB are not supported.");
            return GetValue((int)index);
        }

        private sealed class ArrayEnumerator : IEnumerator, ICloneable
        {
            private readonly Array _array;
            private nint _index;

            internal ArrayEnumerator(Array array)
            {
                _array = array;
                _index = -1;
            }

            public object Clone() => MemberwiseCopy();

            private ArrayEnumerator MemberwiseCopy()
            {
                var copy = new ArrayEnumerator(_array);
                copy._index = _index;
                return copy;
            }

            public bool MoveNext()
            {
                nint index = _index + 1;
                if ((nuint)index >= (nuint)_array.Length)
                {
                    _index = (nint)_array.Length;
                    return false;
                }
                _index = index;
                return true;
            }

            public object? Current
            {
                get
                {
                    nint index = _index;
                    Array array = _array;

                    if ((nuint)index >= (nuint)array.Length)
                    {
                        if (index < 0)
                            throw new InvalidOperationException("Enumeration has not started. Call MoveNext.");
                        throw new InvalidOperationException("Enumeration already finished.");
                    }

                    return array.GetValue((int)index);
                }
            }

            public void Reset()
            {
                _index = -1;
            }
        }
    }
}
