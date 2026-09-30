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

    public interface IComparable
    {
        int CompareTo(object obj);
    }

    public interface IComparable<in T>
    {
        int CompareTo(T other);
    }

    // Format-string surface. `IFormatProvider` is a culture/locale source —
    // we never have one (invariant only), so callers pass null. Primitives
    // implement `IFormattable` to honour spec strings like "X" (hex) / "N2"
    // (numeric) when string.Format / StringBuilder.AppendFormat encounter
    // `{N:fmt}`. If a type doesn't implement IFormattable, the format spec
    // is silently ignored and ToString() is used.
    public interface IFormatProvider
    {
        object GetFormat(Type formatType);
    }

    public interface IFormattable
    {
        string ToString(string format, IFormatProvider formatProvider);
    }

    // Delegate type for callers passing a lambda comparator (e.g.
    // `Array.Sort(arr, (a, b) => a.X.CompareTo(b.X))`). Roslyn compiles
    // it against MulticastDelegate base; runtime needs the Delegate
    // machinery (target/method ptr/Invoke) to actually fire. If a real
    // lambda survives ILC codegen on our stubs it'll work — otherwise
    // callers should switch to the IComparer<T> overload of Sort.
    public delegate int Comparison<in T>(T x, T y);
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

    // Non-generic legacy interfaces. Needed by BCL-lifted code that
    // implements both modern generic and legacy non-generic surface on
    // the same type (e.g. Iced's InstructionList). Member shapes match
    // canonical .NET — no algorithmic content, types only.
    public interface ICollection : IEnumerable
    {
        int Count { get; }
        bool IsSynchronized { get; }
        object SyncRoot { get; }
        void CopyTo(System.Array array, int index);
    }

    public interface IList : ICollection
    {
        object this[int index] { get; set; }
        bool IsFixedSize { get; }
        bool IsReadOnly { get; }
        int Add(object value);
        void Clear();
        bool Contains(object value);
        int IndexOf(object value);
        void Insert(int index, object value);
        void Remove(object value);
        void RemoveAt(int index);
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

    public interface IComparer<in T>
    {
        int Compare(T x, T y);
    }

    public interface ISet<T> : ICollection<T>
    {
        new bool Add(T item);
        void ExceptWith(IEnumerable<T> other);
        void IntersectWith(IEnumerable<T> other);
        bool IsProperSubsetOf(IEnumerable<T> other);
        bool IsProperSupersetOf(IEnumerable<T> other);
        bool IsSubsetOf(IEnumerable<T> other);
        bool IsSupersetOf(IEnumerable<T> other);
        bool Overlaps(IEnumerable<T> other);
        bool SetEquals(IEnumerable<T> other);
        void SymmetricExceptWith(IEnumerable<T> other);
        void UnionWith(IEnumerable<T> other);
    }

    public interface IReadOnlySet<T> : IReadOnlyCollection<T>
    {
        bool Contains(T item);
        bool IsProperSubsetOf(IEnumerable<T> other);
        bool IsProperSupersetOf(IEnumerable<T> other);
        bool IsSubsetOf(IEnumerable<T> other);
        bool IsSupersetOf(IEnumerable<T> other);
        bool Overlaps(IEnumerable<T> other);
        bool SetEquals(IEnumerable<T> other);
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
