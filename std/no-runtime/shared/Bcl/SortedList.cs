// System.Collections.Generic.SortedList<TKey, TValue> — BCL-compat core.
//
// Structure mirrors BCL:
//   TKey[] keys + TValue[] values + int _size,
// grown x2 with DefaultCapacity=4. Keys kept sorted via binary-search
// insert; lookup is O(log n).
//
// Ordered via IComparer<TKey>: caller-supplied in ctor, else falls back
// to Comparer<TKey>.Default (which prefers IComparable<T>).
//
// Cuts vs real BCL SortedList<T, U>:
//  - Non-generic IDictionary / ICollection surface.
//  - Serialization, ctor(IDictionary<TKey, TValue>).
//  - Array.Copy/Sort/BinarySearch — inlined manual loops/search.
//  - Throws → Halt().
//  - Keys/Values exposed as IList<T> snapshot arrays for simplicity;
//    BCL exposes live views via KeyList/ValueList. Add when a caller
//    needs live mutation through the view.

namespace System.Collections.Generic
{
    public class SortedList<TKey, TValue> : IDictionary<TKey, TValue>, IReadOnlyDictionary<TKey, TValue>
    {
        private const int DefaultCapacity = 4;

        private TKey[] keys;
        private TValue[] values;
        private int _size;
        private int version;
        private readonly IComparer<TKey> comparer;

        public SortedList()
        {
            keys = new TKey[0];
            values = new TValue[0];
            _size = 0;
            comparer = Comparer<TKey>.Default;
        }

        public SortedList(int capacity)
        {
            if (capacity < 0) Halt();
            keys = capacity == 0 ? new TKey[0] : new TKey[capacity];
            values = capacity == 0 ? new TValue[0] : new TValue[capacity];
            comparer = Comparer<TKey>.Default;
        }

        public SortedList(IComparer<TKey> comparer) : this()
        {
            if (comparer != null) this.comparer = comparer;
        }

        public SortedList(int capacity, IComparer<TKey> comparer) : this(comparer)
        {
            Capacity = capacity;
        }

        public int Count => _size;

        public bool IsReadOnly => false;

        public IComparer<TKey> Comparer => comparer;

        public int Capacity
        {
            get => keys.Length;
            set
            {
                if (value == keys.Length) return;
                if (value < _size) Halt();

                if (value > 0)
                {
                    TKey[] newKeys = new TKey[value];
                    TValue[] newValues = new TValue[value];
                    for (int i = 0; i < _size; i++)
                    {
                        newKeys[i] = keys[i];
                        newValues[i] = values[i];
                    }
                    keys = newKeys;
                    values = newValues;
                }
                else
                {
                    keys = new TKey[0];
                    values = new TValue[0];
                }
            }
        }

        public TValue this[TKey key]
        {
            get
            {
                int i = IndexOfKey(key);
                if (i < 0) Halt();
                return values[i];
            }
            set
            {
                if (key == null) Halt();
                int i = InternalBinarySearch(key);
                if (i >= 0) { values[i] = value; version++; return; }
                Insert(~i, key, value);
            }
        }

        public void Add(TKey key, TValue value)
        {
            if (key == null) Halt();
            int i = InternalBinarySearch(key);
            if (i >= 0) Halt();   // duplicate
            Insert(~i, key, value);
        }

        void ICollection<KeyValuePair<TKey, TValue>>.Add(KeyValuePair<TKey, TValue> kv)
            => Add(kv.Key, kv.Value);

        public void Clear()
        {
            version++;
            for (int i = 0; i < _size; i++) { keys[i] = default; values[i] = default; }
            _size = 0;
        }

        public bool ContainsKey(TKey key)
        {
            if (key == null) Halt();
            return InternalBinarySearch(key) >= 0;
        }

        public bool ContainsValue(TValue value)
        {
            IEqualityComparer<TValue> cmp = EqualityComparer<TValue>.Default;
            for (int i = 0; i < _size; i++)
                if (cmp.Equals(values[i], value)) return true;
            return false;
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Contains(KeyValuePair<TKey, TValue> kv)
        {
            int idx = IndexOfKey(kv.Key);
            return idx >= 0 && EqualityComparer<TValue>.Default.Equals(values[idx], kv.Value);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            if (array == null) Halt();
            if (arrayIndex < 0 || arrayIndex > array.Length) Halt();
            if (array.Length - arrayIndex < _size) Halt();

            for (int i = 0; i < _size; i++)
                array[arrayIndex + i] = new KeyValuePair<TKey, TValue>(keys[i], values[i]);
        }

        public TKey GetKeyAtIndex(int index)
        {
            if ((uint)index >= (uint)_size) Halt();
            return keys[index];
        }

        public TValue GetValueAtIndex(int index)
        {
            if ((uint)index >= (uint)_size) Halt();
            return values[index];
        }

        public void SetValueAtIndex(int index, TValue value)
        {
            if ((uint)index >= (uint)_size) Halt();
            values[index] = value;
            version++;
        }

        public int IndexOfKey(TKey key)
        {
            if (key == null) Halt();
            int ret = InternalBinarySearch(key);
            return ret >= 0 ? ret : -1;
        }

        public int IndexOfValue(TValue value)
        {
            IEqualityComparer<TValue> cmp = EqualityComparer<TValue>.Default;
            for (int i = 0; i < _size; i++)
                if (cmp.Equals(values[i], value)) return i;
            return -1;
        }

        public bool Remove(TKey key)
        {
            int i = IndexOfKey(key);
            if (i < 0) return false;
            RemoveAt(i);
            return true;
        }

        bool ICollection<KeyValuePair<TKey, TValue>>.Remove(KeyValuePair<TKey, TValue> kv)
        {
            int i = IndexOfKey(kv.Key);
            if (i >= 0 && EqualityComparer<TValue>.Default.Equals(values[i], kv.Value))
            {
                RemoveAt(i);
                return true;
            }
            return false;
        }

        public void RemoveAt(int index)
        {
            if ((uint)index >= (uint)_size) Halt();
            _size--;
            if (index < _size)
            {
                for (int j = index; j < _size; j++)
                {
                    keys[j] = keys[j + 1];
                    values[j] = values[j + 1];
                }
            }
            keys[_size] = default;
            values[_size] = default;
            version++;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            int i = IndexOfKey(key);
            if (i >= 0) { value = values[i]; return true; }
            value = default;
            return false;
        }

        public void TrimExcess()
        {
            int threshold = (int)(keys.Length * 0.9);
            if (_size < threshold) Capacity = _size;
        }

        // Exposed as a read-only snapshot. Mutation via the return value
        // does not propagate back to the SortedList (BCL's KeyList/ValueList
        // would, but we keep this simpler).
        public TKey[] GetKeyArray()
        {
            TKey[] arr = new TKey[_size];
            for (int i = 0; i < _size; i++) arr[i] = keys[i];
            return arr;
        }

        public TValue[] GetValueArray()
        {
            TValue[] arr = new TValue[_size];
            for (int i = 0; i < _size; i++) arr[i] = values[i];
            return arr;
        }

        // Minimum IDictionary surface Keys/Values — return simple wrappers
        // that satisfy ICollection<T>/IReadOnlyCollection<T>. Not mutation-
        // aware; behave like read-only snapshots.
        public ICollection<TKey> Keys => new KeyCollection(this);
        public ICollection<TValue> Values => new ValueCollection(this);
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        private void Insert(int index, TKey key, TValue value)
        {
            if (_size == keys.Length) EnsureCapacity(_size + 1);
            if (index < _size)
            {
                for (int j = _size; j > index; j--)
                {
                    keys[j] = keys[j - 1];
                    values[j] = values[j - 1];
                }
            }
            keys[index] = key;
            values[index] = value;
            _size++;
            version++;
        }

        private void EnsureCapacity(int min)
        {
            int newCap = keys.Length == 0 ? DefaultCapacity : keys.Length * 2;
            if (newCap < min) newCap = min;
            Capacity = newCap;
        }

        // Returns insertion-inverted index on miss: `~result` is the index
        // where `key` should be inserted to keep the keys array sorted.
        private int InternalBinarySearch(TKey key)
        {
            int lo = 0;
            int hi = _size - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                int cmp = comparer.Compare(keys[mid], key);
                if (cmp == 0) return mid;
                if (cmp < 0) lo = mid + 1;
                else hi = mid - 1;
            }
            return ~lo;
        }

        private static void Halt() { while (true) ; }

        // ---- Enumeration ----

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        // Same class-enumerator decision as Dictionary — struct form hits
        // ILC 7.0.20's boxed-struct-enumerator path for shared-generic
        // IEnumerator<KVP> which we can't support cleanly.
        public sealed class Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly SortedList<TKey, TValue> _list;
            private int _index;
            private KeyValuePair<TKey, TValue> _current;
            private readonly int _version;

            internal Enumerator(SortedList<TKey, TValue> list)
            {
                _list = list;
                _index = 0;
                _current = default;
                _version = list.version;
            }

            public KeyValuePair<TKey, TValue> Current => _current;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_version != _list.version) Halt();
                if ((uint)_index < (uint)_list._size)
                {
                    _current = new KeyValuePair<TKey, TValue>(_list.keys[_index], _list.values[_index]);
                    _index++;
                    return true;
                }
                _index = _list._size + 1;
                _current = default;
                return false;
            }

            public void Reset()
            {
                if (_version != _list.version) Halt();
                _index = 0;
                _current = default;
            }

            public void Dispose() { }
        }

        // ---- Keys / Values collection wrappers ----

        private sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
        {
            private readonly SortedList<TKey, TValue> _list;
            internal KeyCollection(SortedList<TKey, TValue> list) { _list = list; }
            public int Count => _list._size;
            public bool IsReadOnly => true;
            public bool Contains(TKey item) => _list.ContainsKey(item);
            public void Add(TKey item) => Halt();
            public void Clear() => Halt();
            public bool Remove(TKey item) { Halt(); return false; }
            public void CopyTo(TKey[] array, int arrayIndex)
            {
                for (int i = 0; i < _list._size; i++) array[arrayIndex + i] = _list.keys[i];
            }
            public IEnumerator<TKey> GetEnumerator() => new KeyEnum(_list);
            IEnumerator IEnumerable.GetEnumerator() => new KeyEnum(_list);

            private sealed class KeyEnum : IEnumerator<TKey>
            {
                private readonly SortedList<TKey, TValue> _list;
                private int _index;
                private readonly int _version;
                public KeyEnum(SortedList<TKey, TValue> list) { _list = list; _index = -1; _version = list.version; }
                public TKey Current => _index >= 0 && _index < _list._size ? _list.keys[_index] : default;
                object IEnumerator.Current => Current;
                public bool MoveNext()
                {
                    if (_version != _list.version) Halt();
                    _index++;
                    return _index < _list._size;
                }
                public void Reset() { _index = -1; }
                public void Dispose() { }
            }
        }

        private sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
        {
            private readonly SortedList<TKey, TValue> _list;
            internal ValueCollection(SortedList<TKey, TValue> list) { _list = list; }
            public int Count => _list._size;
            public bool IsReadOnly => true;
            public bool Contains(TValue item) => _list.ContainsValue(item);
            public void Add(TValue item) => Halt();
            public void Clear() => Halt();
            public bool Remove(TValue item) { Halt(); return false; }
            public void CopyTo(TValue[] array, int arrayIndex)
            {
                for (int i = 0; i < _list._size; i++) array[arrayIndex + i] = _list.values[i];
            }
            public IEnumerator<TValue> GetEnumerator() => new ValueEnum(_list);
            IEnumerator IEnumerable.GetEnumerator() => new ValueEnum(_list);

            private sealed class ValueEnum : IEnumerator<TValue>
            {
                private readonly SortedList<TKey, TValue> _list;
                private int _index;
                private readonly int _version;
                public ValueEnum(SortedList<TKey, TValue> list) { _list = list; _index = -1; _version = list.version; }
                public TValue Current => _index >= 0 && _index < _list._size ? _list.values[_index] : default;
                object IEnumerator.Current => Current;
                public bool MoveNext()
                {
                    if (_version != _list.version) Halt();
                    _index++;
                    return _index < _list._size;
                }
                public void Reset() { _index = -1; }
                public void Dispose() { }
            }
        }
    }
}
