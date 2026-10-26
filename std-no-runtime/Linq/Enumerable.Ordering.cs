// OrderBy / OrderByDescending / ThenBy / ThenByDescending and
// IOrderedEnumerable<T>. The shape is the BCL's (dotnet/runtime main,
// System.Linq: OrderBy.cs, OrderedEnumerable.cs, MIT): an ordered sequence
// keeps its source and a chain of keys, primary first; ThenBy adds a key to the
// chain; enumeration computes every key once and sorts stably, later keys
// breaking the ties of earlier ones (the BCL's EnumerableSorter).
//
// SharpOS: the sort is a bottom-up merge sort over indices (stable by
// construction) instead of the BCL's quicksort with an index tie-break; the
// partial-sort fast paths (First, ElementAt, Skip/Take over an ordering) are
// not ported. ThenBy reaches a foreign IOrderedEnumerable<T> through its
// CreateOrderedEnumerable, a generic virtual method.

using System.Collections;
using System.Collections.Generic;

namespace System.Linq
{
    public interface IOrderedEnumerable<out TElement> : IEnumerable<TElement>
    {
        IOrderedEnumerable<TElement> CreateOrderedEnumerable<TKey>(Func<TElement, TKey> keySelector, IComparer<TKey>? comparer, bool descending);
    }

    public static partial class Enumerable
    {
        public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => OrderBy(source, keySelector, null);

        public static IOrderedEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            return new OrderedEnumerable<TSource>(source, new SortKey<TSource, TKey>(keySelector, comparer, descending: false, next: null));
        }

        public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => OrderByDescending(source, keySelector, null);

        public static IOrderedEnumerable<TSource> OrderByDescending<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            return new OrderedEnumerable<TSource>(source, new SortKey<TSource, TKey>(keySelector, comparer, descending: true, next: null));
        }

        public static IOrderedEnumerable<TSource> ThenBy<TSource, TKey>(this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => ThenBy(source, keySelector, null);

        public static IOrderedEnumerable<TSource> ThenBy<TSource, TKey>(this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            if (source is OrderedEnumerable<TSource> ordered) return ordered.ThenBy(keySelector, comparer, descending: false);
            return source.CreateOrderedEnumerable(keySelector, comparer, false);
        }

        public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => ThenByDescending(source, keySelector, null);

        public static IOrderedEnumerable<TSource> ThenByDescending<TSource, TKey>(this IOrderedEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey>? comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            if (source is OrderedEnumerable<TSource> ordered) return ordered.ThenBy(keySelector, comparer, descending: true);
            return source.CreateOrderedEnumerable(keySelector, comparer, true);
        }

        // An ordering: the source and its keys, the last added first in the
        // chain built here and reversed when sorting (primary key first).
        private sealed class OrderedEnumerable<TElement> : IOrderedEnumerable<TElement>
        {
            private readonly IEnumerable<TElement> _source;
            private readonly SortKey<TElement> _lastKey;

            public OrderedEnumerable(IEnumerable<TElement> source, SortKey<TElement> lastKey)
            {
                _source = source;
                _lastKey = lastKey;
            }

            public IOrderedEnumerable<TElement> ThenBy<TKey>(Func<TElement, TKey> keySelector, IComparer<TKey>? comparer, bool descending)
                => new OrderedEnumerable<TElement>(_source, new SortKey<TElement, TKey>(keySelector, comparer, descending, _lastKey));

            IOrderedEnumerable<TElement> IOrderedEnumerable<TElement>.CreateOrderedEnumerable<TKey>(Func<TElement, TKey> keySelector, IComparer<TKey>? comparer, bool descending)
                => ThenBy(keySelector, comparer, descending);

            public IEnumerator<TElement> GetEnumerator()
            {
                TElement[] items = ToArray(_source);
                int n = items.Length;
                if (n == 0) yield break;

                // Every key computed once, per enumeration; primary key first.
                var keys = new List<KeyColumn>();
                for (SortKey<TElement>? k = _lastKey; k != null; k = k.Next) keys.Add(k.Compute(items));
                keys.Reverse();

                int[] idx = new int[n];
                for (int i = 0; i < n; i++) idx[i] = i;
                int[] buf = new int[n];
                for (int width = 1; width < n; width <<= 1)
                {
                    for (int lo = 0; lo < n; lo += width << 1)
                    {
                        int mid = lo + width; if (mid > n) mid = n;
                        int hi = lo + (width << 1); if (hi > n) hi = n;
                        int a = lo, b = mid, o = lo;
                        while (a < mid && b < hi)
                        {
                            // <= 0 keeps the left run first on ties -> stable.
                            if (Compare(keys, idx[a], idx[b]) <= 0) buf[o++] = idx[a++];
                            else buf[o++] = idx[b++];
                        }
                        while (a < mid) buf[o++] = idx[a++];
                        while (b < hi) buf[o++] = idx[b++];
                    }
                    int[] t = idx; idx = buf; buf = t;
                }

                for (int i = 0; i < n; i++) yield return items[idx[i]];
            }

            private static int Compare(List<KeyColumn> keys, int x, int y)
            {
                for (int i = 0; i < keys.Count; i++)
                {
                    int c = keys[i].Compare(x, y);
                    if (c != 0) return c;
                }
                return 0;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        private abstract class SortKey<TElement>
        {
            public SortKey<TElement>? Next;

            public abstract KeyColumn Compute(TElement[] elements);
        }

        private sealed class SortKey<TElement, TKey> : SortKey<TElement>
        {
            private readonly Func<TElement, TKey> _keySelector;
            private readonly IComparer<TKey> _comparer;
            private readonly bool _descending;

            public SortKey(Func<TElement, TKey> keySelector, IComparer<TKey>? comparer, bool descending, SortKey<TElement>? next)
            {
                _keySelector = keySelector;
                _comparer = comparer ?? Comparer<TKey>.Default;
                _descending = descending;
                Next = next;
            }

            public override KeyColumn Compute(TElement[] elements)
            {
                var keys = new TKey[elements.Length];
                for (int i = 0; i < keys.Length; i++) keys[i] = _keySelector(elements[i]);
                return new KeyColumn<TKey>(keys, _comparer, _descending);
            }
        }

        // One key over the elements of one enumeration.
        private abstract class KeyColumn
        {
            public abstract int Compare(int x, int y);
        }

        private sealed class KeyColumn<TKey> : KeyColumn
        {
            private readonly TKey[] _keys;
            private readonly IComparer<TKey> _comparer;
            private readonly bool _descending;

            public KeyColumn(TKey[] keys, IComparer<TKey> comparer, bool descending)
            {
                _keys = keys;
                _comparer = comparer;
                _descending = descending;
            }

            // As EnumerableSorter: a descending order flips the sign without negating.
            public override int Compare(int x, int y)
            {
                int c = _comparer.Compare(_keys[x], _keys[y]);
                if (c == 0) return 0;
                return _descending != (c > 0) ? 1 : -1;
            }
        }
    }
}