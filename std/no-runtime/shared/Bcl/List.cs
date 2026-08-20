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
    // The non-generic IList too: old APIs still take one — Terminal.Gui's
    // ComboBox and ListView hand their sources around as IList — and without
    // it every such call needs a wrapper at the call site.
    public class List<T> : IList<T>, IReadOnlyList<T>, System.Collections.IList
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

        // Ported from dotnet/runtime v8.0.27. The header used to list this
        // among the cuts, "add later when a real use-case shows up"; the use
        // case is `new List<Rune> (runes)`, which Terminal.Gui does on every
        // line of text it touches. Without it the call bound to the capacity
        // ctor instead and failed as "cannot convert to int", which reads like
        // a type error rather than a missing member.
        public List(IEnumerable<T> collection)
        {
            if (collection == null)
                throw new ArgumentNullException(nameof(collection));

            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                _items = count == 0 ? new T[0] : new T[count];
                foreach (T item in c) _items[_size++] = item;
                return;
            }

            _items = new T[0];
            foreach (T item in collection) Add(item);
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

        public T[] ToArray()
        {
            T[] array = new T[_size];
            for (int i = 0; i < _size; i++) array[i] = _items[i];
            return array;
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

        // The non-generic half. Each member forwards to its typed twin, with a
        // cast at the boundary: a wrong type in gets a clear InvalidCastException
        // rather than being silently ignored.
        object? System.Collections.IList.this[int index]
        {
            get => _items[index];
            set => _items[index] = (T)value!;
        }

        bool System.Collections.IList.IsFixedSize => false;

        bool System.Collections.IList.IsReadOnly => false;

        bool System.Collections.ICollection.IsSynchronized => false;

        object System.Collections.ICollection.SyncRoot => this;

        int System.Collections.IList.Add(object? value)
        {
            Add((T)value!);
            return _size - 1;
        }

        bool System.Collections.IList.Contains(object? value) => value is T item && Contains(item);

        int System.Collections.IList.IndexOf(object? value) => value is T item ? IndexOf(item) : -1;

        void System.Collections.IList.Insert(int index, object? value) => Insert(index, (T)value!);

        void System.Collections.IList.Remove(object? value)
        {
            if (value is T item) Remove(item);
        }

        void System.Collections.ICollection.CopyTo(Array array, int index)
        {
            // Only a T[] target: Array.SetValue needs per-element type checks
            // this runtime cannot make, and a silent partial copy would be
            // worse than saying no.
            if (array is T[] typed) CopyTo(typed, index);
            else throw new ArgumentException("Target array must be of type T[].", nameof(array));
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            for (int i = 0; i < _size; i++)
                array[arrayIndex + i] = _items[i];
        }

        // The range family, ported from dotnet/runtime v8.0.27 List.cs. The
        // header used to say these would come when a real caller appeared;
        // Terminal.Gui is that caller, and it uses all four heavily on its
        // List<Rune> text buffers.
        //
        // Cut, as elsewhere in this file: the _version bump and the argument
        // validation, which our ThrowHelpers cannot report anyway.
        public void AddRange(IEnumerable<T> collection) => InsertRange(_size, collection);

        public void InsertRange(int index, IEnumerable<T> collection)
        {
            if (collection is ICollection<T> c)
            {
                int count = c.Count;
                if (count == 0) return;

                EnsureCapacity(_size + count);

                // Shift the tail up in one pass rather than per element: an
                // insert of n items into a list of m must stay O(n+m), not
                // O(n*m) — text editing does this on every keystroke.
                for (int i = _size - 1; i >= index; i--)
                    _items[i + count] = _items[i];

                if (ReferenceEquals(this, c))
                {
                    // Inserting a list into itself. The shift above has already
                    // torn the source in two, so the copy has to be done in the
                    // same two pieces — enumerating would read the hole.
                    for (int i = 0; i < index; i++)
                        _items[index + i] = _items[i];
                    for (int i = 0; i < _size - index; i++)
                        _items[index * 2 + i] = _items[index + count + i];
                }
                else
                {
                    int at = index;
                    foreach (T item in c) _items[at++] = item;
                }

                _size += count;
                return;
            }

            foreach (T item in collection) Insert(index++, item);
        }

        // Ported from dotnet/runtime v8.0.27. Listed among the header's cuts
        // as "add later"; Terminal.Gui is the caller that arrived.
        public T? Find(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            for (int i = 0; i < _size; i++)
                if (match(_items[i])) return _items[i];
            return default;
        }

        public int FindIndex(Predicate<T> match) => FindIndex(0, _size, match);

        public int FindIndex(int startIndex, int count, Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            int end = startIndex + count;
            for (int i = startIndex; i < end; i++)
                if (match(_items[i])) return i;
            return -1;
        }

        public List<T> FindAll(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            var found = new List<T>();
            for (int i = 0; i < _size; i++)
                if (match(_items[i])) found.Add(_items[i]);
            return found;
        }

        public bool Exists(Predicate<T> match) => FindIndex(match) >= 0;

        public T? FindLast(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            for (int i = _size - 1; i >= 0; i--)
                if (match(_items[i])) return _items[i];
            return default;
        }

        public int FindLastIndex(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            for (int i = _size - 1; i >= 0; i--)
                if (match(_items[i])) return i;
            return -1;
        }

        public int RemoveAll(Predicate<T> match)
        {
            if (match == null) throw new ArgumentNullException(nameof(match));

            int removed = 0;
            for (int i = _size - 1; i >= 0; i--)
            {
                if (match(_items[i]))
                {
                    RemoveAt(i);
                    removed++;
                }
            }
            return removed;
        }

        public System.Collections.ObjectModel.ReadOnlyCollection<T> AsReadOnly() => new System.Collections.ObjectModel.ReadOnlyCollection<T>(this);

        public List<T> GetRange(int index, int count)
        {
            var list = new List<T>(count);
            for (int i = 0; i < count; i++) list.Add(_items[index + i]);
            return list;
        }

        public void RemoveRange(int index, int count)
        {
            if (count <= 0) return;

            _size -= count;
            for (int i = index; i < _size; i++)
                _items[i] = _items[i + count];
            for (int i = _size; i < _size + count; i++)
                _items[i] = default;
        }

        // Ported from dotnet/runtime v8.0.27 List.cs. Cut: the _version check (this port
        // does not track versions, see the header).
        public void ForEach(Action<T> action)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            for (int i = 0; i < _size; i++)
            {
                action(_items[i]);
            }
        }

        public void Reverse() => Reverse(0, _size);

        public void Reverse(int index, int count)
        {
            int i = index;
            int j = index + count - 1;
            while (i < j)
            {
                T tmp = _items[i];
                _items[i] = _items[j];
                _items[j] = tmp;
                i++;
                j--;
            }
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
