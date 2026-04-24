// System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue> —
// ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Collections/ObjectModel/ReadOnlyDictionary.cs
//
// Cuts:
//  - Non-generic IDictionary / ICollection surface and
//    IDictionaryEnumerator — not in our interface stubs.
//  - Serialization attrs + [NonSerialized] caching pattern — we cache
//    Keys/Values lazily via factory-or-null instead of ??=.
//  - Throws → Halt() (no exception engine).
//  - KeyCollection / ValueCollection non-generic ICollection overrides.

namespace System.Collections.ObjectModel
{
    public class ReadOnlyDictionary<TKey, TValue> : System.Collections.Generic.IDictionary<TKey, TValue>,
                                                     System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>
    {
        private readonly System.Collections.Generic.IDictionary<TKey, TValue> m_dictionary;
        private KeyCollection _keys;
        private ValueCollection _values;

        public ReadOnlyDictionary(System.Collections.Generic.IDictionary<TKey, TValue> dictionary)
        {
            if (dictionary == null) Halt();
            m_dictionary = dictionary;
        }

        protected System.Collections.Generic.IDictionary<TKey, TValue> Dictionary => m_dictionary;

        public KeyCollection Keys
        {
            get
            {
                if (_keys == null) _keys = new KeyCollection(m_dictionary.Keys);
                return _keys;
            }
        }

        public ValueCollection Values
        {
            get
            {
                if (_values == null) _values = new ValueCollection(m_dictionary.Values);
                return _values;
            }
        }

        public bool ContainsKey(TKey key) => m_dictionary.ContainsKey(key);

        System.Collections.Generic.ICollection<TKey> System.Collections.Generic.IDictionary<TKey, TValue>.Keys => Keys;
        System.Collections.Generic.ICollection<TValue> System.Collections.Generic.IDictionary<TKey, TValue>.Values => Values;

        System.Collections.Generic.IEnumerable<TKey> System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        System.Collections.Generic.IEnumerable<TValue> System.Collections.Generic.IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public bool TryGetValue(TKey key, out TValue value) => m_dictionary.TryGetValue(key, out value);

        public TValue this[TKey key] => m_dictionary[key];

        void System.Collections.Generic.IDictionary<TKey, TValue>.Add(TKey key, TValue value) => Halt();
        bool System.Collections.Generic.IDictionary<TKey, TValue>.Remove(TKey key) { Halt(); return false; }

        TValue System.Collections.Generic.IDictionary<TKey, TValue>.this[TKey key]
        {
            get => m_dictionary[key];
            set => Halt();
        }

        public int Count => m_dictionary.Count;

        bool System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.Contains(
            System.Collections.Generic.KeyValuePair<TKey, TValue> item)
            => m_dictionary.Contains(item);

        void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.CopyTo(
            System.Collections.Generic.KeyValuePair<TKey, TValue>[] array, int arrayIndex)
            => m_dictionary.CopyTo(array, arrayIndex);

        bool System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.IsReadOnly => true;

        void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.Add(
            System.Collections.Generic.KeyValuePair<TKey, TValue> item) => Halt();

        void System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.Clear() => Halt();

        bool System.Collections.Generic.ICollection<System.Collections.Generic.KeyValuePair<TKey, TValue>>.Remove(
            System.Collections.Generic.KeyValuePair<TKey, TValue> item) { Halt(); return false; }

        public System.Collections.Generic.IEnumerator<System.Collections.Generic.KeyValuePair<TKey, TValue>> GetEnumerator()
            => m_dictionary.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => ((System.Collections.IEnumerable)m_dictionary).GetEnumerator();

        private static void Halt() { while (true) ; }

        public sealed class KeyCollection : System.Collections.Generic.ICollection<TKey>,
                                            System.Collections.Generic.IReadOnlyCollection<TKey>
        {
            private readonly System.Collections.Generic.ICollection<TKey> _collection;

            internal KeyCollection(System.Collections.Generic.ICollection<TKey> collection)
            {
                if (collection == null) Halt();
                _collection = collection;
            }

            void System.Collections.Generic.ICollection<TKey>.Add(TKey item) => Halt();
            void System.Collections.Generic.ICollection<TKey>.Clear() => Halt();
            bool System.Collections.Generic.ICollection<TKey>.Contains(TKey item) => _collection.Contains(item);
            public void CopyTo(TKey[] array, int arrayIndex) => _collection.CopyTo(array, arrayIndex);
            public int Count => _collection.Count;
            bool System.Collections.Generic.ICollection<TKey>.IsReadOnly => true;
            bool System.Collections.Generic.ICollection<TKey>.Remove(TKey item) { Halt(); return false; }
            public System.Collections.Generic.IEnumerator<TKey> GetEnumerator() => _collection.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => ((System.Collections.IEnumerable)_collection).GetEnumerator();
        }

        public sealed class ValueCollection : System.Collections.Generic.ICollection<TValue>,
                                               System.Collections.Generic.IReadOnlyCollection<TValue>
        {
            private readonly System.Collections.Generic.ICollection<TValue> _collection;

            internal ValueCollection(System.Collections.Generic.ICollection<TValue> collection)
            {
                if (collection == null) Halt();
                _collection = collection;
            }

            void System.Collections.Generic.ICollection<TValue>.Add(TValue item) => Halt();
            void System.Collections.Generic.ICollection<TValue>.Clear() => Halt();
            bool System.Collections.Generic.ICollection<TValue>.Contains(TValue item) => _collection.Contains(item);
            public void CopyTo(TValue[] array, int arrayIndex) => _collection.CopyTo(array, arrayIndex);
            public int Count => _collection.Count;
            bool System.Collections.Generic.ICollection<TValue>.IsReadOnly => true;
            bool System.Collections.Generic.ICollection<TValue>.Remove(TValue item) { Halt(); return false; }
            public System.Collections.Generic.IEnumerator<TValue> GetEnumerator() => _collection.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
                => ((System.Collections.IEnumerable)_collection).GetEnumerator();
        }
    }
}
