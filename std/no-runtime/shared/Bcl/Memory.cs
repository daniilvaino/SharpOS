// System.Memory<T> / System.ReadOnlyMemory<T> — the part of them that is a
// span you are allowed to put in a field.
//
// Written for System.Formats.Cbor, whose reader keeps the document it is
// reading in a field: a Span cannot live there (it is a ref struct and the
// stack is the only place it may point at), and the BCL's answer to exactly
// that is Memory. Changing the reader to carry an array and an offset instead
// would have worked and would have been the wrong trade — a ported type that
// no longer matches the type it was ported from, for the sake of sixty lines.
//
// Backed by an array and nothing else. The real thing can also stand for a
// string's characters or a MemoryManager's buffer; neither has a caller here,
// and a Memory that silently accepted one and then handed back the wrong bytes
// would be worse than one that cannot be built at all.

namespace System
{
    public readonly struct ReadOnlyMemory<T>
    {
        private readonly T[] _array;
        private readonly int _index;
        private readonly int _length;

        public ReadOnlyMemory(T[] array)
        {
            _array = array;
            _index = 0;
            _length = array == null ? 0 : array.Length;
        }

        public ReadOnlyMemory(T[] array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0) throw new ArgumentOutOfRangeException(nameof(start));
                _array = null; _index = 0; _length = 0;
                return;
            }
            if ((uint)start > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(start));
            if ((uint)length > (uint)(array.Length - start)) throw new ArgumentOutOfRangeException(nameof(length));

            _array = array;
            _index = start;
            _length = length;
        }

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        public ReadOnlySpan<T> Span => _array == null
            ? default
            : new ReadOnlySpan<T>(_array, _index, _length);

        public ReadOnlyMemory<T> Slice(int start)
        {
            if ((uint)start > (uint)_length) throw new ArgumentOutOfRangeException(nameof(start));
            return new ReadOnlyMemory<T>(_array, _index + start, _length - start);
        }

        public ReadOnlyMemory<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length) throw new ArgumentOutOfRangeException(nameof(start));
            if ((uint)length > (uint)(_length - start)) throw new ArgumentOutOfRangeException(nameof(length));
            return new ReadOnlyMemory<T>(_array, _index + start, length);
        }

        public T[] ToArray()
        {
            T[] copy = new T[_length];
            for (int i = 0; i < _length; i++) copy[i] = _array[_index + i];
            return copy;
        }

        public static implicit operator ReadOnlyMemory<T>(T[] array) => new ReadOnlyMemory<T>(array);
    }

    public readonly struct Memory<T>
    {
        private readonly T[] _array;
        private readonly int _index;
        private readonly int _length;

        public Memory(T[] array)
        {
            _array = array;
            _index = 0;
            _length = array == null ? 0 : array.Length;
        }

        public Memory(T[] array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0) throw new ArgumentOutOfRangeException(nameof(start));
                _array = null; _index = 0; _length = 0;
                return;
            }
            if ((uint)start > (uint)array.Length) throw new ArgumentOutOfRangeException(nameof(start));
            if ((uint)length > (uint)(array.Length - start)) throw new ArgumentOutOfRangeException(nameof(length));

            _array = array;
            _index = start;
            _length = length;
        }

        public int Length => _length;
        public bool IsEmpty => _length == 0;

        public Span<T> Span => _array == null
            ? default
            : new Span<T>(_array, _index, _length);

        public Memory<T> Slice(int start)
        {
            if ((uint)start > (uint)_length) throw new ArgumentOutOfRangeException(nameof(start));
            return new Memory<T>(_array, _index + start, _length - start);
        }

        public Memory<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length) throw new ArgumentOutOfRangeException(nameof(start));
            if ((uint)length > (uint)(_length - start)) throw new ArgumentOutOfRangeException(nameof(length));
            return new Memory<T>(_array, _index + start, length);
        }

        public T[] ToArray()
        {
            T[] copy = new T[_length];
            for (int i = 0; i < _length; i++) copy[i] = _array[_index + i];
            return copy;
        }

        public static implicit operator Memory<T>(T[] array) => new Memory<T>(array);
        public static implicit operator ReadOnlyMemory<T>(Memory<T> memory)
            => new ReadOnlyMemory<T>(memory._array, memory._index, memory._length);
    }
}
