// Fundamental BCL interfaces wired into the exact canonical namespaces so
// BCL-style code (and anything lifted from dotnet/runtime later, including
// LINQ) compiles against them without source changes.
//
// Kept deliberately minimal — only shape required by our collections.
// Add members when a concrete consumer needs them.

namespace System
{
    public interface IEquatable<T>
    {
        bool Equals(T other);
    }

    public interface IDisposable
    {
        void Dispose();
    }
}

namespace System.Collections
{
    public interface IEnumerable
    {
        IEnumerator GetEnumerator();
    }

    public interface IEnumerator
    {
        object Current { get; }
        bool MoveNext();
        void Reset();
    }
}

namespace System.Collections.Generic
{
    public interface IEnumerable<out T> : IEnumerable
    {
        new IEnumerator<T> GetEnumerator();
    }

    public interface IEnumerator<out T> : IEnumerator, System.IDisposable
    {
        new T Current { get; }
    }

    public interface ICollection<T> : IEnumerable<T>
    {
        int Count { get; }
        bool IsReadOnly { get; }
        void Add(T item);
        void Clear();
        bool Contains(T item);
        void CopyTo(T[] array, int arrayIndex);
        bool Remove(T item);
    }

    public interface IReadOnlyCollection<out T> : IEnumerable<T>
    {
        int Count { get; }
    }

    public interface IList<T> : ICollection<T>
    {
        T this[int index] { get; set; }
        int IndexOf(T item);
        void Insert(int index, T item);
        void RemoveAt(int index);
    }

    public interface IReadOnlyList<out T> : IReadOnlyCollection<T>
    {
        T this[int index] { get; }
    }

    public interface IEqualityComparer<T>
    {
        bool Equals(T x, T y);
        int GetHashCode(T obj);
    }

    public interface IDictionary<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>>
    {
        TValue this[TKey key] { get; set; }
        ICollection<TKey> Keys { get; }
        ICollection<TValue> Values { get; }
        bool ContainsKey(TKey key);
        void Add(TKey key, TValue value);
        bool Remove(TKey key);
        bool TryGetValue(TKey key, out TValue value);
    }

    public interface IReadOnlyDictionary<TKey, TValue> : IReadOnlyCollection<KeyValuePair<TKey, TValue>>
    {
        TValue this[TKey key] { get; }
        IEnumerable<TKey> Keys { get; }
        IEnumerable<TValue> Values { get; }
        bool ContainsKey(TKey key);
        bool TryGetValue(TKey key, out TValue value);
    }

    public readonly struct KeyValuePair<TKey, TValue>
    {
        public TKey Key { get; }
        public TValue Value { get; }

        public KeyValuePair(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public void Deconstruct(out TKey key, out TValue value)
        {
            key = Key;
            value = Value;
        }
    }
}
