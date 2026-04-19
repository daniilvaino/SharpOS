// System.Collections.Generic.List<T> — API-compatible with BCL.
//
// Structure follows dotnet/runtime's List<T>: T[] _items backing array,
// int _size for element count. Grows x2 with DefaultCapacity=4. Interface
// implementations pass through to the generic methods. Boxing-less foreach
// via a public struct Enumerator — foreach picks it up through the
// non-interface GetEnumerator() overload (BCL does the same trick).
//
// Cut from the real List<T>:
//  - `_version` bump / enumerator invalidation (we don't throw on
//    concurrent modification — we halt anyway via ThrowHelpers).
//  - Argument*Exception throws — our ThrowHelpers.ThrowArgument* spin
//    forever, same end result.
//  - ctor(IEnumerable<T>) — add later when a real use-case shows up.
//  - AddRange / InsertRange / GetRange — same reason.
//  - Sort / BinarySearch / FindIndex — same.

namespace System.Collections.Generic
{
    public class List<T> : IList<T>, IReadOnlyList<T>
    {
        private const int DefaultCapacity = 4;

        private T[] _items;
        private int _size;

        public List()
        {
            _items = new T[0];
        }

        public List(int capacity)
        {
            _items = capacity <= 0 ? new T[0] : new T[capacity];
        }

        public int Count => _size;

        public int Capacity
        {
            get => _items.Length;
            set
            {
                if (value < _size) return; // silently clamp; BCL throws
                if (value == _items.Length) return;
                if (value > 0)
                {
                    T[] larger = new T[value];
                    for (int i = 0; i < _size; i++) larger[i] = _items[i];
                    _items = larger;
                }
                else
                {
                    _items = new T[0];
                }
            }
        }

        public bool IsReadOnly => false;

        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public void Add(T item)
        {
            if (_size == _items.Length) EnsureCapacity(_size + 1);
            _items[_size] = item;
            _size++;
        }

        public void Clear()
        {
            if (_size == 0) return;
            for (int i = 0; i < _size; i++) _items[i] = default;
            _size = 0;
        }

        public bool Contains(T item) => IndexOf(item) >= 0;

        public int IndexOf(T item)
        {
            EqualityComparer<T> cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < _size; i++)
                if (cmp.Equals(_items[i], item))
                    return i;
            return -1;
        }

        public void Insert(int index, T item)
        {
            if (_size == _items.Length) EnsureCapacity(_size + 1);
            for (int i = _size; i > index; i--)
                _items[i] = _items[i - 1];
            _items[index] = item;
            _size++;
        }

        public bool Remove(T item)
        {
            int idx = IndexOf(item);
            if (idx < 0) return false;
            RemoveAt(idx);
            return true;
        }

        public void RemoveAt(int index)
        {
            _size--;
            for (int i = index; i < _size; i++)
                _items[i] = _items[i + 1];
            _items[_size] = default;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            for (int i = 0; i < _size; i++)
                array[arrayIndex + i] = _items[i];
        }

        private void EnsureCapacity(int min)
        {
            if (_items.Length >= min) return;
            int newCap = _items.Length == 0 ? DefaultCapacity : _items.Length * 2;
            if (newCap < min) newCap = min;
            T[] larger = new T[newCap];
            for (int i = 0; i < _size; i++) larger[i] = _items[i];
            _items = larger;
        }

        // Boxing-less foreach via public struct Enumerator. Interface-typed
        // GetEnumerator calls also return the struct but through a boxed copy.
        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        public struct Enumerator : IEnumerator<T>
        {
            private readonly List<T> _list;
            private int _index;
            private T _current;

            internal Enumerator(List<T> list)
            {
                _list = list;
                _index = 0;
                _current = default;
            }

            public T Current => _current;
            object IEnumerator.Current => _current;

            public bool MoveNext()
            {
                if ((uint)_index < (uint)_list._size)
                {
                    _current = _list._items[_index];
                    _index++;
                    return true;
                }
                _current = default;
                _index = _list._size + 1;
                return false;
            }

            public void Reset()
            {
                _index = 0;
                _current = default;
            }

            public void Dispose() { }
        }
    }
}
