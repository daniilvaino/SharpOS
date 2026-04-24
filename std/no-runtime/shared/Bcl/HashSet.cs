// System.Collections.Generic.HashSet<T> — BCL-compat surface.
//
// Separate chaining hash set: `_buckets` is an array of head-of-chain
// Entry refs, each Entry holds one value + a `m_next` pointer to the
// next collider in the same bucket. Identical shape to our Dictionary —
// shares all the reasoning from step 31-32 (routes lookups through
// `IEqualityComparer<T>`, which in turn works under shared-generic iface
// dispatch).
//
// Cut from real HashSet<T>:
//  - `_version` bump / enumerator invalidation (we halt anyway).
//  - Argument* / InvalidOperation exceptions → Halt().
//  - ctor(IEnumerable<T>) — add when a caller needs it.
//  - TrimExcess / EnsureCapacity / CopyTo with arrayIndex ranges beyond
//    the simple case.
//  - ISet<T> set-ops (Union/Intersect/Except/…) — stubbed as Halt(),
//    wire them up when a consumer asks.

namespace System.Collections.Generic
{
    public class HashSet<T> : ISet<T>, IReadOnlySet<T>, ICollection<T>
    {
        private const int DefaultSize = 17;

        public HashSet() : this(DefaultSize, null) { }
        public HashSet(int capacity) : this(capacity, null) { }
        public HashSet(IEqualityComparer<T> comparer) : this(DefaultSize, comparer) { }

        public HashSet(int capacity, IEqualityComparer<T> comparer)
        {
            _comparer = comparer ?? EqualityComparer<T>.Default;
            Clear(capacity);
        }

        public IEqualityComparer<T> Comparer => _comparer;

        public int Count => _numEntries;

        public bool IsReadOnly => false;

        public bool Add(T item)
        {
            if (item == null) Halt();
            Entry existing = Find(item);
            if (existing != null) return false;
            _version++;
            UncheckedAdd(item);
            return true;
        }

        void ICollection<T>.Add(T item) => Add(item);

        public bool Contains(T item)
        {
            if (item == null) Halt();
            return Find(item) != null;
        }

        public bool Remove(T item)
        {
            if (item == null) Halt();
            int bucket = GetBucket(item);
            Entry prev = null;
            Entry entry = _buckets[bucket];
            while (entry != null)
            {
                if (_comparer.Equals(item, entry.m_value))
                {
                    if (prev == null)
                        _buckets[bucket] = entry.m_next;
                    else
                        prev.m_next = entry.m_next;
                    _version++;
                    _numEntries--;
                    return true;
                }
                prev = entry;
                entry = entry.m_next;
            }
            return false;
        }

        public void Clear() => Clear(DefaultSize);

        public void Clear(int capacity)
        {
            _version++;
            _buckets = new Entry[capacity];
            _numEntries = 0;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            int j = arrayIndex;
            for (int i = 0; i < _buckets.Length; i++)
            {
                Entry e = _buckets[i];
                while (e != null)
                {
                    array[j] = e.m_value;
                    j++;
                    e = e.m_next;
                }
            }
        }

        // Set-ops: present to satisfy ISet<T>, stubbed until a caller needs
        // them. BCL implementations also walk `other` with special-case
        // fast paths when it's another HashSet; we'll copy those at that
        // point. For now behavior is "halt" so it's obvious an unstubbed
        // path was hit.
        public void ExceptWith(IEnumerable<T> other) => Halt();
        public void IntersectWith(IEnumerable<T> other) => Halt();
        public bool IsProperSubsetOf(IEnumerable<T> other) { Halt(); return false; }
        public bool IsProperSupersetOf(IEnumerable<T> other) { Halt(); return false; }
        public bool IsSubsetOf(IEnumerable<T> other) { Halt(); return false; }
        public bool IsSupersetOf(IEnumerable<T> other) { Halt(); return false; }
        public bool Overlaps(IEnumerable<T> other) { Halt(); return false; }
        public bool SetEquals(IEnumerable<T> other) { Halt(); return false; }
        public void SymmetricExceptWith(IEnumerable<T> other) => Halt();

        public void UnionWith(IEnumerable<T> other)
        {
            foreach (T item in other) Add(item);
        }

        private Entry Find(T item)
        {
            int bucket = GetBucket(item);
            Entry entry = _buckets[bucket];
            while (entry != null)
            {
                if (_comparer.Equals(item, entry.m_value))
                    return entry;
                entry = entry.m_next;
            }
            return null;
        }

        private Entry UncheckedAdd(T item)
        {
            Entry entry = new Entry();
            entry.m_value = item;

            int bucket = GetBucket(item);
            entry.m_next = _buckets[bucket];
            _buckets[bucket] = entry;

            _numEntries++;
            if (_numEntries > (_buckets.Length * 2))
                ExpandBuckets();

            return entry;
        }

        private void ExpandBuckets()
        {
            int newNumBuckets = _buckets.Length * 2 + 1;
            Entry[] newBuckets = new Entry[newNumBuckets];
            for (int i = 0; i < _buckets.Length; i++)
            {
                Entry entry = _buckets[i];
                while (entry != null)
                {
                    Entry nextEntry = entry.m_next;
                    int bucket = GetBucket(entry.m_value, newNumBuckets);
                    entry.m_next = newBuckets[bucket];
                    newBuckets[bucket] = entry;
                    entry = nextEntry;
                }
            }
            _buckets = newBuckets;
        }

        private int GetBucket(T item, int numBuckets = 0)
        {
            int h = _comparer.GetHashCode(item);
            h &= 0x7fffffff;
            return (h % (numBuckets == 0 ? _buckets.Length : numBuckets));
        }

        private static void Halt() { while (true) ; }

        private sealed class Entry
        {
            public T m_value;
            public Entry m_next;
        }

        private Entry[] _buckets;
        private int _numEntries;
        private int _version;
        private IEqualityComparer<T> _comparer;

        // ---- Enumeration ----

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        // See Dictionary.Enumerator for why this is a class, not a struct —
        // ILC 7.0.20 stumbles on the Boxed_Enumerator<__Canon>.MoveNext
        // path when a generic struct enumerator is surfaced through
        // IEnumerator<T>. Class enumerator sidesteps the boxing stub.
        public sealed class Enumerator : IEnumerator<T>
        {
            private readonly HashSet<T> _set;
            private int _bucketIdx;
            private Entry _current;

            internal Enumerator(HashSet<T> set)
            {
                _set = set;
                _bucketIdx = -1;
                _current = null;
            }

            public T Current => _current == null ? default : _current.m_value;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_current != null && _current.m_next != null)
                {
                    _current = _current.m_next;
                    return true;
                }
                _bucketIdx++;
                while (_bucketIdx < _set._buckets.Length)
                {
                    if (_set._buckets[_bucketIdx] != null)
                    {
                        _current = _set._buckets[_bucketIdx];
                        return true;
                    }
                    _bucketIdx++;
                }
                _current = null;
                return false;
            }

            public void Reset()
            {
                _bucketIdx = -1;
                _current = null;
            }

            public void Dispose() { }
        }
    }
}
