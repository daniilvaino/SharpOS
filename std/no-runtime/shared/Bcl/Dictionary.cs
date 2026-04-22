// System.Collections.Generic.Dictionary<TKey, TValue>
//
// Source-of-truth: dotnet/runtime's nativeaot LowLevelDictionary<TKey, TValue>
//   src/coreclr/nativeaot/Common/src/System/Collections/Generic/LowLevelDictionary.cs
//
// Transplanted with minimal changes:
//   - namespace System.Collections.Generic (was same)
//   - type made public
//   - `throw new ArgumentNullException` / `KeyNotFoundException` /
//     `ArgumentException` replaced with infinite loop (our kernel has no
//     exception engine; halt is the honest behavior)
//   - `out TValue?` → `out TValue` (no nullable reference types surface in
//     our stubbed Nullable)
//   - removed `try/catch (OutOfMemoryException)` in ExpandBuckets (no
//     exception runtime); if alloc fails, the next array access will halt
//     the program via ThrowHelpers, which is fine
//   - removed the nested `LowLevelDictEnumerator` sibling `LowLevelDictionaryWithIEnumerable`;
//     our Dictionary has an integrated `GetEnumerator` with a struct
//     Enumerator matching the BCL shape
//   - added `IDictionary<TKey, TValue>`, `IReadOnlyDictionary<TKey, TValue>`,
//     `ICollection<KeyValuePair<TKey, TValue>>` interface implementations
//     (mostly boilerplate over the core Find/UncheckedAdd)
//
// Key comparisons go through `object.Equals(a, b)` + `key.GetHashCode()` —
// both virtual on System.Object's non-generic vtable. Works because Object's
// vtable is resolved at compile time per concrete type, no generic/interface
// dispatch helper needed at runtime.
//
// The BCL `where TKey : IEquatable<TKey>` route ends up in ILC-emitted
// `constrained.callvirt IEquatable<TKey>::Equals` which routes to
// RhpInitialDynamicInterfaceDispatch — currently a `while(true)` stub in
// our runtime, so that path halts. Same issue for virtual calls on a
// generic abstract class parameterised by a reference type (e.g.
// `EqualityComparer<MyKey>`) — goes through __Canon shared-generic
// dispatch that also wants a helper we don't have.
//
// Practical contract: for value-equality on reference-typed keys, override
// Object.Equals(object) + GetHashCode() on the key type (standard C#
// convention anyway). BCL's IEqualityComparer<TKey> ctor overloads are
// intentionally absent here until the missing dispatch helpers are ported
// — see docs/nativeaot-nostdlib-limits.md for the full writeup.

namespace System.Collections.Generic
{
    public class Dictionary<TKey, TValue>
        : IDictionary<TKey, TValue>,
          IReadOnlyDictionary<TKey, TValue>,
          ICollection<KeyValuePair<TKey, TValue>>
    {
        private const int DefaultSize = 17;

        public Dictionary() : this(DefaultSize) { }

        public Dictionary(int capacity)
        {
            Clear(capacity);
        }

        // IEqualityComparer<TKey> constructors present in BCL are absent here
        // on purpose — see docs/nativeaot-nostdlib-limits.md. Virtual dispatch
        // on a generic reference-typed abstract base (EqualityComparer<TKey>
        // for a class TKey) goes through shared-generic helpers we don't have.
        // For value-equality on reference keys, override Object.Equals +
        // GetHashCode on the key type — Dictionary below routes through those.
        // We'll reinstate the comparer ctors when the missing helpers land.

        public int Count => _numEntries;

        public bool IsReadOnly => false;

        public TValue this[TKey key]
        {
            get
            {
                if (key == null) Halt();
                Entry entry = Find(key);
                if (entry == null) Halt();
                return entry.m_value;
            }
            set
            {
                if (key == null) Halt();
                _version++;
                Entry entry = Find(key);
                if (entry != null)
                    entry.m_value = value;
                else
                    UncheckedAdd(key, value);
            }
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            value = default;
            if (key == null) Halt();
            Entry entry = Find(key);
            if (entry != null)
            {
                value = entry.m_value;
                return true;
            }
            return false;
        }

        public void Add(TKey key, TValue value)
        {
            if (key == null) Halt();
            Entry entry = Find(key);
            if (entry != null) Halt(); // duplicate key — BCL throws ArgumentException
            _version++;
            UncheckedAdd(key, value);
        }

        public bool ContainsKey(TKey key)
        {
            if (key == null) Halt();
            return Find(key) != null;
        }

        public void Clear(int capacity = DefaultSize)
        {
            _version++;
            _buckets = new Entry[capacity];
            _numEntries = 0;
        }

        public void Clear() => Clear(DefaultSize);

        public bool Remove(TKey key)
        {
            if (key == null) Halt();
            int bucket = GetBucket(key);
            Entry prev = null;
            Entry entry = _buckets[bucket];
            while (entry != null)
            {
                if (object.Equals(key, entry.m_key))
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

        internal TValue LookupOrAdd(TKey key, TValue value)
        {
            Entry entry = Find(key);
            if (entry != null) return entry.m_value;
            UncheckedAdd(key, value);
            return value;
        }

        private Entry Find(TKey key)
        {
            int bucket = GetBucket(key);
            Entry entry = _buckets[bucket];
            while (entry != null)
            {
                if (object.Equals(key, entry.m_key))
                    return entry;
                entry = entry.m_next;
            }
            return null;
        }

        private Entry UncheckedAdd(TKey key, TValue value)
        {
            Entry entry = new Entry();
            entry.m_key = key;
            entry.m_value = value;

            int bucket = GetBucket(key);
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
                    int bucket = GetBucket(entry.m_key, newNumBuckets);
                    entry.m_next = newBuckets[bucket];
                    newBuckets[bucket] = entry;
                    entry = nextEntry;
                }
            }
            _buckets = newBuckets;
        }

        private int GetBucket(TKey key, int numBuckets = 0)
        {
            // Goes through Object.GetHashCode virtual (kernel verified).
            int h = key.GetHashCode();
            h &= 0x7fffffff;
            return (h % (numBuckets == 0 ? _buckets.Length : numBuckets));
        }

        // Halt without a real exception engine. Keeps the API shape of the
        // BCL `throw` sites but maps to a loop, same as our ThrowHelpers.
        private static void Halt() { while (true) ; }

        private sealed class Entry
        {
            public TKey m_key;
            public TValue m_value;
            public Entry m_next;
        }

        private Entry[] _buckets;
        private int _numEntries;
        private int _version;

        // ---- ICollection<KeyValuePair<TKey, TValue>> boilerplate ----

        public void Add(KeyValuePair<TKey, TValue> item) => Add(item.Key, item.Value);

        public bool Contains(KeyValuePair<TKey, TValue> item)
        {
            if (!TryGetValue(item.Key, out TValue v)) return false;
            // BCL uses EqualityComparer<TValue>.Default.Equals(v, item.Value); we
            // don't have that safely for all T (our Default is a factory that
            // returns a boxing comparer). Reference/identity comparison is a
            // reasonable default for managed code; callers who need value
            // semantics on TValue should use TryGetValue directly.
            object o1 = v;
            object o2 = item.Value;
            return ReferenceEquals(o1, o2);
        }

        public bool Remove(KeyValuePair<TKey, TValue> item)
        {
            if (!Contains(item)) return false;
            return Remove(item.Key);
        }

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
        {
            int j = arrayIndex;
            for (int i = 0; i < _buckets.Length; i++)
            {
                Entry e = _buckets[i];
                while (e != null)
                {
                    array[j] = new KeyValuePair<TKey, TValue>(e.m_key, e.m_value);
                    j++;
                    e = e.m_next;
                }
            }
        }

        // ---- IDictionary<TKey, TValue> Keys / Values ----

        public ICollection<TKey> Keys => new KeyCollection(this);
        public ICollection<TValue> Values => new ValueCollection(this);

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        // ---- Enumeration ----

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        // NOTE: this is a class, not a struct like BCL's Dictionary.Enumerator.
        // ILC 7.0.20 chokes on `Boxed_Enumerator<__Canon, int>.MoveNext_Unbox()`
        // when a generic struct Enumerator is used through shared-generic code
        // and later exposed via IEnumerator<KVP>. Making the enumerator a class
        // avoids the boxing/unbox-stub path entirely. Costs one heap alloc
        // per foreach; acceptable until we can update past that ILC bug.
        public sealed class Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly Dictionary<TKey, TValue> _dict;
            private int _bucketIdx;
            private Entry _current;

            internal Enumerator(Dictionary<TKey, TValue> dict)
            {
                _dict = dict;
                _bucketIdx = -1;
                _current = null;
            }

            public KeyValuePair<TKey, TValue> Current
                => _current == null
                    ? default
                    : new KeyValuePair<TKey, TValue>(_current.m_key, _current.m_value);

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_current != null && _current.m_next != null)
                {
                    _current = _current.m_next;
                    return true;
                }
                _bucketIdx++;
                while (_bucketIdx < _dict._buckets.Length)
                {
                    if (_dict._buckets[_bucketIdx] != null)
                    {
                        _current = _dict._buckets[_bucketIdx];
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

        // ---- Keys/Values collection wrappers ----

        private sealed class KeyCollection : ICollection<TKey>, IReadOnlyCollection<TKey>
        {
            private readonly Dictionary<TKey, TValue> _dict;
            internal KeyCollection(Dictionary<TKey, TValue> d) { _dict = d; }
            public int Count => _dict.Count;
            public bool IsReadOnly => true;
            public bool Contains(TKey item) => _dict.ContainsKey(item);
            public void Add(TKey item) => Halt();
            public void Clear() => Halt();
            public bool Remove(TKey item) { Halt(); return false; }
            public void CopyTo(TKey[] array, int arrayIndex)
            {
                int j = arrayIndex;
                foreach (var kv in _dict) { array[j] = kv.Key; j++; }
            }
            public IEnumerator<TKey> GetEnumerator() => new KeyEnumerator(_dict);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            private sealed class KeyEnumerator : IEnumerator<TKey>
            {
                private Enumerator _inner;
                internal KeyEnumerator(Dictionary<TKey, TValue> d) { _inner = d.GetEnumerator(); }
                public TKey Current => _inner.Current.Key;
                object IEnumerator.Current => Current;
                public bool MoveNext() => _inner.MoveNext();
                public void Reset() => _inner.Reset();
                public void Dispose() { }
            }
        }

        private sealed class ValueCollection : ICollection<TValue>, IReadOnlyCollection<TValue>
        {
            private readonly Dictionary<TKey, TValue> _dict;
            internal ValueCollection(Dictionary<TKey, TValue> d) { _dict = d; }
            public int Count => _dict.Count;
            public bool IsReadOnly => true;
            public bool Contains(TValue item) { Halt(); return false; }
            public void Add(TValue item) => Halt();
            public void Clear() => Halt();
            public bool Remove(TValue item) { Halt(); return false; }
            public void CopyTo(TValue[] array, int arrayIndex)
            {
                int j = arrayIndex;
                foreach (var kv in _dict) { array[j] = kv.Value; j++; }
            }
            public IEnumerator<TValue> GetEnumerator() => new ValueEnumerator(_dict);
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            private sealed class ValueEnumerator : IEnumerator<TValue>
            {
                private Enumerator _inner;
                internal ValueEnumerator(Dictionary<TKey, TValue> d) { _inner = d.GetEnumerator(); }
                public TValue Current => _inner.Current.Value;
                object IEnumerator.Current => Current;
                public bool MoveNext() => _inner.MoveNext();
                public void Reset() => _inner.Reset();
                public void Dispose() { }
            }
        }
    }
}
