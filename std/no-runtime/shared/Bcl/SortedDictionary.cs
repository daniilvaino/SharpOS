// System.Collections.Generic.SortedDictionary<TKey, TValue>.
//
// The real one is a red-black tree. This one keeps a SortedList inside it — the
// same sorted-array structure already ported next door — and forwards to it.
//
// That is a deliberate substitution, and the difference is worth stating rather
// than hiding, because the API cannot show it:
//
//   * behaviour is the same: keys ordered by IComparer<TKey>, enumeration in
//     key order, duplicate keys rejected, TryGetValue on a missing key false;
//   * cost is NOT the same: Add and Remove are O(n) here, where a tree gives
//     O(log n). Lookup is O(log n) either way, since both binary-search.
//
// Which is fine for what asked for it — Terminal.Gui's TreeView keeps a handful
// of expanded nodes — and would not be fine for a dictionary taking thousands
// of inserts. When one appears, this file is where the tree goes; nothing above
// it has to change.

namespace System.Collections.Generic
{
    public class SortedDictionary<TKey, TValue> : IDictionary<TKey, TValue>,
        IReadOnlyDictionary<TKey, TValue>
    {
        private readonly SortedList<TKey, TValue> _items;

        public SortedDictionary()
        {
            _items = new SortedList<TKey, TValue>();
        }

        public SortedDictionary(IComparer<TKey> comparer)
        {
            _items = new SortedList<TKey, TValue>(comparer);
        }

        public SortedDictionary(IDictionary<TKey, TValue> dictionary) : this()
        {
            if (dictionary == null) throw new ArgumentNullException(nameof(dictionary));

            foreach (KeyValuePair<TKey, TValue> pair in dictionary)
                _items.Add(pair.Key, pair.Value);
        }

        public IComparer<TKey> Comparer => _items.Comparer;

        public int Count => _items.Count;

        public bool IsReadOnly => false;

        public TValue this[TKey key]
        {
            get => _items[key];
            set => _items[key] = value;
        }

        public ICollection<TKey> Keys => _items.Keys;

        public ICollection<TValue> Values => _items.Values;

        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => _items.Keys;

        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => _items.Values;

        public void Add(TKey key, TValue value) => _items.Add(key, value);

        public void Add(KeyValuePair<TKey, TValue> item) => _items.Add(item.Key, item.Value);

        public void Clear() => _items.Clear();

        public bool Contains(KeyValuePair<TKey, TValue> item)
            => _items.TryGetValue(item.Key, out TValue value)
               && EqualityComparer<TValue>.Default.Equals(value, item.Value);

        public bool ContainsKey(TKey key) => _items.ContainsKey(key);

        public bool ContainsValue(TValue value) => _items.ContainsValue(value);

        public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
            => _items.CopyTo(array, arrayIndex);

        public bool Remove(TKey key) => _items.Remove(key);

        public bool Remove(KeyValuePair<TKey, TValue> item)
            => Contains(item) && _items.Remove(item.Key);

        public bool TryGetValue(TKey key, out TValue value) => _items.TryGetValue(key, out value);

        public SortedList<TKey, TValue>.Enumerator GetEnumerator() => _items.GetEnumerator();

        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator()
            => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }
}
