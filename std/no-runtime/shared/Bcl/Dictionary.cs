// System.Collections.Generic.Dictionary<TKey, TValue>
//
// The surface. Storage lives in Dictionary.Storage.cs, ported from the real
// dotnet/runtime Dictionary — buckets of indices over a dense entries array,
// so ENUMERATION FOLLOWS INSERTION.
//
// It used to be dotnet/runtime's LowLevelDictionary, which chains one list per
// bucket and therefore enumerates in hash order. Same API, different order —
// and order is observable: Terminal.Gui renders a TreeView's roots straight
// from a Dictionary, so a sorted list of folders came back shuffled.
//
// Historical source-of-truth (the surface below still follows its shape):
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
// Key comparisons route through an IEqualityComparer<TKey> field. The
// comparer is whatever the caller passes to the ctor (BCL-compat); falls
// back to EqualityComparer<TKey>.Default — which prefers IEquatable<T>
// when T implements it (primitives, custom types) and otherwise delegates
// to Object.Equals. Both dispatch paths are shared-generic interface
// calls; the resolver backing them lives in
// OS/src/Kernel/Memory/InterfaceDispatchResolver.cs and was enabled in
// step 32.

namespace System.Collections.Generic
{
    public partial class Dictionary<TKey, TValue>
        : IDictionary<TKey, TValue>,
          IReadOnlyDictionary<TKey, TValue>,
          ICollection<KeyValuePair<TKey, TValue>>
    {
        private const int DefaultSize = 17;

        public Dictionary() : this(DefaultSize, null) { }

        public Dictionary(int capacity) : this(capacity, null) { }

        public Dictionary(IEqualityComparer<TKey> comparer) : this(DefaultSize, comparer) { }

        public Dictionary(int capacity, IEqualityComparer<TKey> comparer)
        {
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            Clear(capacity);
        }

        public IEqualityComparer<TKey> Comparer => _comparer;

        /// <summary>Live entries: everything ever used, less what was freed.</summary>
        public int Count => _count - _freeCount;

        public bool IsReadOnly => false;

        public TValue this[TKey key]
        {
            get
            {
                int i = FindEntry(key);
                if (i < 0) Halt();          // BCL throws KeyNotFoundException
                return _entries![i].value;
            }
            set => TryInsert(key, value, overwrite: true);
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            int i = FindEntry(key);
            if (i < 0)
            {
                value = default!;
                return false;
            }

            value = _entries![i].value;
            return true;
        }

        public void Add(TKey key, TValue value)
        {
            // BCL throws ArgumentException on a duplicate key.
            if (!TryInsert(key, value, overwrite: false)) Halt();
        }

        public bool ContainsKey(TKey key) => FindEntry(key) >= 0;

        /// <summary>Adds if absent; false on a duplicate, without throwing.</summary>
        public bool TryAdd(TKey key, TValue value) => TryInsert(key, value, overwrite: false);

        public void Clear(int capacity = DefaultSize)
        {
            _version++;
            _buckets = null;
            _entries = null;
            _count = 0;
            _freeCount = 0;
            _freeList = -1;

            if (capacity > 0) Initialize(capacity);
        }

        public void Clear() => Clear(DefaultSize);

        public bool Remove(TKey key) => RemoveEntry(key);

        internal TValue LookupOrAdd(TKey key, TValue value)
        {
            int i = FindEntry(key);
            if (i >= 0) return _entries![i].value;

            TryInsert(key, value, overwrite: false);
            return value;
        }

        private static void ThrowKeyNull() => Halt();

        // Halt without a real exception engine. Keeps the shape of the BCL
        // throw sites but maps to a loop, same as our ThrowHelpers.
        private static void Halt() { while (true) ; }

        private int _version;
        private IEqualityComparer<TKey> _comparer;

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
            for (int i = 0; i < _count; i++)
            {
                if (_entries![i].next < -1) continue;   // freed slot

                array[j] = new KeyValuePair<TKey, TValue>(_entries[i].key, _entries[i].value);
                j++;
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

            // An index into the entries array, walked in order — which is why
            // enumeration comes out in insertion order, as the BCL's does.
            private int _index;
            private KeyValuePair<TKey, TValue> _current;

            internal Enumerator(Dictionary<TKey, TValue> dict)
            {
                _dict = dict;
                _index = 0;
                _current = default;
            }

            public KeyValuePair<TKey, TValue> Current => _current;

            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                while (_index < _dict._count)
                {
                    int i = _index;
                    _index++;

                    // A freed slot keeps its place so the indices the buckets
                    // point at stay valid; it is skipped rather than compacted.
                    if (_dict._entries![i].next < -1) continue;

                    _current = new KeyValuePair<TKey, TValue>(
                        _dict._entries[i].key, _dict._entries[i].value);
                    return true;
                }

                _current = default;
                return false;
            }

            public void Reset()
            {
                _index = 0;
                _current = default;
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
