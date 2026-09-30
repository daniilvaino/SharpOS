// Ported shape from dotnet/runtime, NOT its behaviour.
//
// The real table holds its keys weakly: an entry disappears when the key
// becomes garbage, which is what lets a library attach side data to objects it
// does not own without keeping them alive forever. That needs weak handles,
// and neither collector here has any — so the entries below are ordinary
// strong references and a key lives as long as the table does.
//
// For the use this was written for — a parser hanging provenance off syntax
// nodes for the length of one parse — the difference is a few dozen small
// objects retained per command. For anything that attaches to long-lived or
// numerous objects it is a leak, and the fix is weak handles in the collector,
// not more code here. Recorded in docs/nativeaot-nostd-kernel-limits.md.
//
// Lookup is by reference identity, as the real one is. That is not the same as
// Dictionary's default: a record type overrides Equals and GetHashCode by
// value, so two distinct nodes that happen to be equal would collapse into one
// entry. The identity hash is the object's address — legitimate precisely
// because neither collector moves objects.

using System.Collections.Generic;

namespace System.Runtime.CompilerServices
{
    public sealed class ConditionalWeakTable<TKey, TValue>
        where TKey : class
        where TValue : class
    {
        public delegate TValue CreateValueCallback(TKey key);

        private readonly Dictionary<TKey, TValue> _entries;

        public ConditionalWeakTable()
        {
            _entries = new Dictionary<TKey, TValue>(new IdentityComparer());
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            return _entries.TryGetValue(key, out value);
        }

        /// <summary>Adds a key that is not in the table yet.</summary>
        public void Add(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (_entries.ContainsKey(key))
                throw new ArgumentException("key already present in the table");
            _entries[key] = value;
        }

        public bool TryAdd(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (_entries.ContainsKey(key)) return false;
            _entries[key] = value;
            return true;
        }

        public void AddOrUpdate(TKey key, TValue value)
        {
            if (key == null) throw new ArgumentNullException("key");
            _entries[key] = value;
        }

        public bool Remove(TKey key)
        {
            if (key == null) throw new ArgumentNullException("key");
            return _entries.Remove(key);
        }

        public void Clear() => _entries.Clear();

        public TValue GetValue(TKey key, CreateValueCallback createValueCallback)
        {
            if (key == null) throw new ArgumentNullException("key");
            if (createValueCallback == null) throw new ArgumentNullException("createValueCallback");

            if (_entries.TryGetValue(key, out TValue existing)) return existing;

            TValue created = createValueCallback(key);
            _entries[key] = created;
            return created;
        }

        private sealed class IdentityComparer : IEqualityComparer<TKey>
        {
            public bool Equals(TKey x, TKey y) => ReferenceEquals(x, y);

            public int GetHashCode(TKey obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
