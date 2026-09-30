// System.Collections.Generic.Comparer<T> — BCL-compat API.
//
// Twin to EqualityComparer<T>. Default returns a factory comparer that
// prefers IComparable<T> via shared-generic iface dispatch, falls back
// to non-generic IComparable. Like EqualityComparer.Default, this is a
// factory (one alloc per read) rather than a lazy static field —
// ClassConstructorRunner limitations documented in
// docs/nativeaot-nostdlib-limits.md §1.

namespace System.Collections.Generic
{
    public abstract class Comparer<T> : IComparer<T>
    {
        public static Comparer<T> Default => new DefaultComparerImpl<T>();

        public abstract int Compare(T x, T y);
    }

    internal sealed class DefaultComparerImpl<T> : Comparer<T>
    {
        public override int Compare(T x, T y)
        {
            // null handling mirrors BCL: null is less than any non-null.
            if (x == null) return y == null ? 0 : -1;
            if (y == null) return 1;

            if (x is IComparable<T> g) return g.CompareTo(y);
            if (x is IComparable ng) return ng.CompareTo(y);

            // No IComparable surface — treat as equal. BCL throws
            // ArgumentException here; we degrade gracefully.
            return 0;
        }
    }
}
