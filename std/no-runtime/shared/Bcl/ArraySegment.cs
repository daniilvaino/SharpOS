// System.ArraySegment<T> — ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/ArraySegment.cs
//
// Cuts:
//  - [Serializable] / TypeForwardedFrom attrs.
//  - Non-generic ICollection surface and HashCode.Combine dependency.
//    GetHashCode returns a cheap combination of (offset, count,
//    array.GetHashCode()) via xor/shift.
//  - System.Array.Copy / IndexOf intrinsics — we don't have them as
//    static helpers; replaced with manual loops.
//  - Throws → Halt().
//  - Static Empty property: allocated lazily instead of field-init to
//    avoid the ClassConstructorRunner static-reference-field crash.

namespace System
{
    public readonly struct ArraySegment<T> : System.Collections.Generic.IList<T>,
                                              System.Collections.Generic.IReadOnlyList<T>
    {
        public static ArraySegment<T> Empty => new ArraySegment<T>(new T[0]);

        private readonly T[] _array;
        private readonly int _offset;
        private readonly int _count;

        public ArraySegment(T[] array)
        {
            if (array == null) Halt();
            _array = array;
            _offset = 0;
            _count = array.Length;
        }

        public ArraySegment(T[] array, int offset, int count)
        {
            if (array == null || (uint)offset > (uint)array.Length || (uint)count > (uint)(array.Length - offset))
                Halt();

            _array = array;
            _offset = offset;
            _count = count;
        }

        public T[] Array => _array;

        public int Offset => _offset;

        public int Count => _count;

        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count) Halt();
                return _array[_offset + index];
            }
            set
            {
                if ((uint)index >= (uint)_count) Halt();
                _array[_offset + index] = value;
            }
        }

        public Enumerator GetEnumerator()
        {
            ThrowInvalidOperationIfDefault();
            return new Enumerator(this);
        }

        public override int GetHashCode()
        {
            if (_array == null) return 0;
            int h = _offset;
            h = (h * 31) ^ _count;
            h = (h * 31) ^ _array.GetHashCode();
            return h;
        }

        public void CopyTo(T[] destination) => CopyTo(destination, 0);

        public void CopyTo(T[] destination, int destinationIndex)
        {
            ThrowInvalidOperationIfDefault();
            for (int i = 0; i < _count; i++)
                destination[destinationIndex + i] = _array[_offset + i];
        }

        public void CopyTo(ArraySegment<T> destination)
        {
            ThrowInvalidOperationIfDefault();
            destination.ThrowInvalidOperationIfDefault();

            if (_count > destination._count) Halt();

            for (int i = 0; i < _count; i++)
                destination._array[destination._offset + i] = _array[_offset + i];
        }

        public override bool Equals(object obj)
            => obj is ArraySegment<T> other && Equals(other);

        public bool Equals(ArraySegment<T> obj)
            => obj._array == _array && obj._offset == _offset && obj._count == _count;

        public ArraySegment<T> Slice(int index)
        {
            ThrowInvalidOperationIfDefault();
            if ((uint)index > (uint)_count) Halt();
            return new ArraySegment<T>(_array, _offset + index, _count - index);
        }

        public ArraySegment<T> Slice(int index, int count)
        {
            ThrowInvalidOperationIfDefault();
            if ((uint)index > (uint)_count || (uint)count > (uint)(_count - index)) Halt();
            return new ArraySegment<T>(_array, _offset + index, count);
        }

        public T[] ToArray()
        {
            ThrowInvalidOperationIfDefault();
            if (_count == 0) return new T[0];
            T[] array = new T[_count];
            for (int i = 0; i < _count; i++) array[i] = _array[_offset + i];
            return array;
        }

        public static bool operator ==(ArraySegment<T> a, ArraySegment<T> b) => a.Equals(b);
        public static bool operator !=(ArraySegment<T> a, ArraySegment<T> b) => !(a == b);

        public static implicit operator ArraySegment<T>(T[] array)
            => array != null ? new ArraySegment<T>(array) : default;

        // IList<T>
        T System.Collections.Generic.IList<T>.this[int index]
        {
            get
            {
                ThrowInvalidOperationIfDefault();
                if (index < 0 || index >= _count) Halt();
                return _array[_offset + index];
            }
            set
            {
                ThrowInvalidOperationIfDefault();
                if (index < 0 || index >= _count) Halt();
                _array[_offset + index] = value;
            }
        }

        int System.Collections.Generic.IList<T>.IndexOf(T item)
        {
            ThrowInvalidOperationIfDefault();
            System.Collections.Generic.IEqualityComparer<T> cmp = System.Collections.Generic.EqualityComparer<T>.Default;
            for (int i = 0; i < _count; i++)
                if (cmp.Equals(_array[_offset + i], item)) return i;
            return -1;
        }

        void System.Collections.Generic.IList<T>.Insert(int index, T item) => Halt();
        void System.Collections.Generic.IList<T>.RemoveAt(int index) => Halt();

        // IReadOnlyList<T>
        T System.Collections.Generic.IReadOnlyList<T>.this[int index]
        {
            get
            {
                ThrowInvalidOperationIfDefault();
                if (index < 0 || index >= _count) Halt();
                return _array[_offset + index];
            }
        }

        // ICollection<T>
        bool System.Collections.Generic.ICollection<T>.IsReadOnly => true;
        void System.Collections.Generic.ICollection<T>.Add(T item) => Halt();
        void System.Collections.Generic.ICollection<T>.Clear() => Halt();

        bool System.Collections.Generic.ICollection<T>.Contains(T item)
        {
            ThrowInvalidOperationIfDefault();
            System.Collections.Generic.IEqualityComparer<T> cmp = System.Collections.Generic.EqualityComparer<T>.Default;
            for (int i = 0; i < _count; i++)
                if (cmp.Equals(_array[_offset + i], item)) return true;
            return false;
        }

        bool System.Collections.Generic.ICollection<T>.Remove(T item) { Halt(); return false; }

        System.Collections.Generic.IEnumerator<T> System.Collections.Generic.IEnumerable<T>.GetEnumerator() => GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private void ThrowInvalidOperationIfDefault()
        {
            if (_array == null) Halt();
        }

        private static void Halt() { while (true) ; }

        public struct Enumerator : System.Collections.Generic.IEnumerator<T>
        {
            private readonly T[] _array;
            private readonly int _start;
            private readonly int _end;
            private int _current;

            internal Enumerator(ArraySegment<T> arraySegment)
            {
                _array = arraySegment.Array;
                _start = arraySegment.Offset;
                _end = arraySegment.Offset + arraySegment.Count;
                _current = arraySegment.Offset - 1;
            }

            public bool MoveNext()
            {
                if (_current < _end)
                {
                    _current++;
                    return _current < _end;
                }
                return false;
            }

            public T Current
            {
                get
                {
                    if (_current < _start) Halt();
                    if (_current >= _end) Halt();
                    return _array[_current];
                }
            }

            object System.Collections.IEnumerator.Current => Current;

            void System.Collections.IEnumerator.Reset()
            {
                _current = _start - 1;
            }

            public void Dispose() { }
        }
    }
}
