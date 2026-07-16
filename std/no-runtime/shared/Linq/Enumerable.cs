// Mini-LINQ for the NoStdLib kernel/std environment.
//
// BCL-signature-compatible System.Linq.Enumerable so real LINQ-to-objects code
// compiles against our std unchanged. Lazy operators are yield-based (identical
// deferred-execution semantics to the BCL); the modern BCL's Iterator<T> /
// partitioning fast-paths are intentionally NOT reproduced (correctness over the
// array/list special-casing perf work). Materializing operators use List<T> /
// growing arrays.
//
// First batch (step134): Where/Select/SelectMany, ToArray/ToList/ToDictionary,
// Count/Any/All, First/Last/Single/ElementAt (+OrDefault), Contains, Skip/Take
// (+While), Concat/Distinct/Reverse, Cast/OfType, Empty/Range/Repeat, Aggregate,
// Sum/Min/Max/Average, OrderBy/OrderByDescending (stable, returns IEnumerable).
// Deferred: ThenBy / IOrderedEnumerable, GroupBy, Join, Zip, Union/Intersect/
// Except, nullable-numeric aggregates, decimal. Add per first consumer.

using System;
using System.Collections;
using System.Collections.Generic;

namespace System.Linq
{
    public static class Enumerable
    {
        private static void ThrowNull(string name) => throw new ArgumentNullException(name);
        private static void ThrowNoElements() => throw new InvalidOperationException("Sequence contains no elements");
        private static void ThrowMoreThanOne() => throw new InvalidOperationException("Sequence contains more than one element");

        // ---- projection / filtering -----------------------------------

        public static IEnumerable<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            foreach (var item in source)
                if (predicate(item))
                    yield return item;
        }

        public static IEnumerable<TSource> Where<TSource>(this IEnumerable<TSource> source, Func<TSource, int, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            int i = 0;
            foreach (var item in source)
            {
                if (predicate(item, i)) yield return item;
                i++;
            }
        }

        public static IEnumerable<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, TResult> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            foreach (var item in source)
                yield return selector(item);
        }

        public static IEnumerable<TResult> Select<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, int, TResult> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            int i = 0;
            foreach (var item in source)
                yield return selector(item, i++);
        }

        public static IEnumerable<TResult> SelectMany<TSource, TResult>(this IEnumerable<TSource> source, Func<TSource, IEnumerable<TResult>> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            foreach (var item in source)
                foreach (var sub in selector(item))
                    yield return sub;
        }

        // ---- partitioning ---------------------------------------------

        public static IEnumerable<TSource> Skip<TSource>(this IEnumerable<TSource> source, int count)
        {
            if (source == null) ThrowNull(nameof(source));
            int i = 0;
            foreach (var item in source)
            {
                if (i++ >= count) yield return item;
            }
        }

        public static IEnumerable<TSource> Take<TSource>(this IEnumerable<TSource> source, int count)
        {
            if (source == null) ThrowNull(nameof(source));
            if (count <= 0) yield break;
            int i = 0;
            foreach (var item in source)
            {
                yield return item;
                if (++i >= count) yield break;
            }
        }

        public static IEnumerable<TSource> SkipWhile<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            bool yielding = false;
            foreach (var item in source)
            {
                if (!yielding && !predicate(item)) yielding = true;
                if (yielding) yield return item;
            }
        }

        public static IEnumerable<TSource> TakeWhile<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var item in source)
            {
                if (!predicate(item)) yield break;
                yield return item;
            }
        }

        // ---- concatenation / set --------------------------------------

        public static IEnumerable<TSource> Concat<TSource>(this IEnumerable<TSource> first, IEnumerable<TSource> second)
        {
            if (first == null) ThrowNull(nameof(first));
            if (second == null) ThrowNull(nameof(second));
            foreach (var item in first) yield return item;
            foreach (var item in second) yield return item;
        }

        public static IEnumerable<TSource> Distinct<TSource>(this IEnumerable<TSource> source)
            => Distinct(source, null);

        public static IEnumerable<TSource> Distinct<TSource>(this IEnumerable<TSource> source, IEqualityComparer<TSource> comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            var seen = new HashSet<TSource>(comparer);
            foreach (var item in source)
                if (seen.Add(item))
                    yield return item;
        }

        public static IEnumerable<TSource> Reverse<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            var buf = ToArray(source);
            for (int i = buf.Length - 1; i >= 0; i--)
                yield return buf[i];
        }

        public static IEnumerable<TSource> DefaultIfEmpty<TSource>(this IEnumerable<TSource> source)
            => DefaultIfEmpty(source, default);

        public static IEnumerable<TSource> DefaultIfEmpty<TSource>(this IEnumerable<TSource> source, TSource defaultValue)
        {
            if (source == null) ThrowNull(nameof(source));
            bool any = false;
            foreach (var item in source) { any = true; yield return item; }
            if (!any) yield return defaultValue;
        }

        // ---- casting --------------------------------------------------

        public static IEnumerable<TResult> Cast<TResult>(this IEnumerable source)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var item in source)
                yield return (TResult)item;
        }

        public static IEnumerable<TResult> OfType<TResult>(this IEnumerable source)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var item in source)
                if (item is TResult t)
                    yield return t;
        }

        // ---- generation -----------------------------------------------

        public static IEnumerable<TResult> Empty<TResult>()
        {
            yield break;
        }

        public static IEnumerable<int> Range(int start, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
                yield return start + i;
        }

        public static IEnumerable<TResult> Repeat<TResult>(TResult element, int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            for (int i = 0; i < count; i++)
                yield return element;
        }

        // ---- ordering (stable; returns IEnumerable, ThenBy deferred) ---

        public static IEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => OrderByCore(source, keySelector, Comparer<TKey>.Default, false);

        public static IEnumerable<TSource> OrderBy<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer)
            => OrderByCore(source, keySelector, comparer ?? Comparer<TKey>.Default, false);

        public static IEnumerable<TSource> OrderByDescending<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => OrderByCore(source, keySelector, Comparer<TKey>.Default, true);

        public static IEnumerable<TSource> OrderByDescending<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer)
            => OrderByCore(source, keySelector, comparer ?? Comparer<TKey>.Default, true);

        // Wrap the sorted array in a real iterator. Returning the TSource[]
        // directly as IEnumerable<TSource> would fault when the caller iterates
        // it: arrays don't implement our IEnumerable<T> (no SZArrayHelper),
        // so GetEnumerator on the array -> "iface-resolve: no impl slot". The
        // foreach below walks the array directly (ldelem), not via IEnumerable.
        private static IEnumerable<TSource> OrderByCore<TSource, TKey>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer, bool descending)
        {
            var sorted = StableSort(source, keySelector, comparer, descending);
            foreach (var item in sorted)
                yield return item;
        }

        private static TSource[] StableSort<TSource, TKey>(IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IComparer<TKey> comparer, bool descending)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            var items = ToArray(source);
            int n = items.Length;
            if (n <= 1) return items;

            var keys = new TKey[n];
            var idx = new int[n];
            for (int i = 0; i < n; i++) { keys[i] = keySelector(items[i]); idx[i] = i; }

            // Stable bottom-up merge sort on the index array, comparing keys
            // directly via IComparer<TKey>. OrderBy must be STABLE, which
            // Array.Sort's introsort is not; we already hold keys[] + comparer,
            // so an inline merge is the natural fit. Comparer<T>.Default.Compare
            // works fine here (Min/Max use the same call).
            int sign = descending ? -1 : 1;
            var buf = new int[n];
            for (int width = 1; width < n; width <<= 1)
            {
                for (int lo = 0; lo < n; lo += width << 1)
                {
                    int mid = lo + width; if (mid > n) mid = n;
                    int hi = lo + (width << 1); if (hi > n) hi = n;
                    int i = lo, j = mid, k = lo;
                    while (i < mid && j < hi)
                    {
                        // <= 0 keeps the left run first on ties -> stable.
                        if (sign * comparer.Compare(keys[idx[i]], keys[idx[j]]) <= 0)
                            buf[k++] = idx[i++];
                        else
                            buf[k++] = idx[j++];
                    }
                    while (i < mid) buf[k++] = idx[i++];
                    while (j < hi) buf[k++] = idx[j++];
                }
                var t = idx; idx = buf; buf = t;
            }

            var result = new TSource[n];
            for (int i = 0; i < n; i++) result[i] = items[idx[i]];
            return result;
        }

        // ---- materialization ------------------------------------------

        public static TSource[] ToArray<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            var buf = new TSource[4];
            int n = 0;
            foreach (var item in source)
            {
                if (n == buf.Length)
                {
                    var nb = new TSource[buf.Length * 2];
                    for (int i = 0; i < n; i++) nb[i] = buf[i];
                    buf = nb;
                }
                buf[n++] = item;
            }
            if (n == buf.Length) return buf;
            var result = new TSource[n];
            for (int i = 0; i < n; i++) result[i] = buf[i];
            return result;
        }

        public static List<TSource> ToList<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            var list = new List<TSource>();
            foreach (var item in source) list.Add(item);
            return list;
        }

        public static Dictionary<TKey, TSource> ToDictionary<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector)
            => ToDictionary(source, keySelector, null);

        public static Dictionary<TKey, TSource> ToDictionary<TSource, TKey>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, IEqualityComparer<TKey> comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            var dict = new Dictionary<TKey, TSource>(comparer);
            foreach (var item in source) dict.Add(keySelector(item), item);
            return dict;
        }

        public static Dictionary<TKey, TValue> ToDictionary<TSource, TKey, TValue>(this IEnumerable<TSource> source, Func<TSource, TKey> keySelector, Func<TSource, TValue> valueSelector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (keySelector == null) ThrowNull(nameof(keySelector));
            if (valueSelector == null) ThrowNull(nameof(valueSelector));
            var dict = new Dictionary<TKey, TValue>();
            foreach (var item in source) dict.Add(keySelector(item), valueSelector(item));
            return dict;
        }

        // ---- quantifiers / counts -------------------------------------

        public static int Count<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            if (source is ICollection<TSource> c) return c.Count;
            int n = 0;
            foreach (var _ in source) n++;
            return n;
        }

        public static int Count<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            int n = 0;
            foreach (var item in source) if (predicate(item)) n++;
            return n;
        }

        public static long LongCount<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            long n = 0;
            foreach (var _ in source) n++;
            return n;
        }

        public static bool Any<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var _ in source) return true;
            return false;
        }

        public static bool Any<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            foreach (var item in source) if (predicate(item)) return true;
            return false;
        }

        public static bool All<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            foreach (var item in source) if (!predicate(item)) return false;
            return true;
        }

        public static bool Contains<TSource>(this IEnumerable<TSource> source, TSource value)
            => Contains(source, value, null);

        public static bool Contains<TSource>(this IEnumerable<TSource> source, TSource value, IEqualityComparer<TSource> comparer)
        {
            if (source == null) ThrowNull(nameof(source));
            comparer ??= EqualityComparer<TSource>.Default;
            foreach (var item in source) if (comparer.Equals(item, value)) return true;
            return false;
        }

        // ---- element access -------------------------------------------

        public static TSource First<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var item in source) return item;
            ThrowNoElements();
            return default;
        }

        public static TSource First<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            foreach (var item in source) if (predicate(item)) return item;
            ThrowNoElements();
            return default;
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            foreach (var item in source) return item;
            return default;
        }

        public static TSource FirstOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            foreach (var item in source) if (predicate(item)) return item;
            return default;
        }

        public static TSource Last<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            bool found = false;
            TSource last = default;
            foreach (var item in source) { last = item; found = true; }
            if (!found) ThrowNoElements();
            return last;
        }

        public static TSource Last<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            bool found = false;
            TSource last = default;
            foreach (var item in source) if (predicate(item)) { last = item; found = true; }
            if (!found) ThrowNoElements();
            return last;
        }

        public static TSource LastOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            TSource last = default;
            foreach (var item in source) last = item;
            return last;
        }

        public static TSource LastOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            TSource last = default;
            foreach (var item in source) if (predicate(item)) last = item;
            return last;
        }

        public static TSource Single<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            bool found = false;
            TSource result = default;
            foreach (var item in source)
            {
                if (found) ThrowMoreThanOne();
                result = item; found = true;
            }
            if (!found) ThrowNoElements();
            return result;
        }

        public static TSource Single<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            bool found = false;
            TSource result = default;
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    if (found) ThrowMoreThanOne();
                    result = item; found = true;
                }
            }
            if (!found) ThrowNoElements();
            return result;
        }

        public static TSource SingleOrDefault<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            bool found = false;
            TSource result = default;
            foreach (var item in source)
            {
                if (found) ThrowMoreThanOne();
                result = item; found = true;
            }
            return result;
        }

        public static TSource SingleOrDefault<TSource>(this IEnumerable<TSource> source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            bool found = false;
            TSource result = default;
            foreach (var item in source)
            {
                if (predicate(item))
                {
                    if (found) ThrowMoreThanOne();
                    result = item; found = true;
                }
            }
            return result;
        }

        public static TSource ElementAt<TSource>(this IEnumerable<TSource> source, int index)
        {
            if (source == null) ThrowNull(nameof(source));
            if (index >= 0)
            {
                int i = 0;
                foreach (var item in source)
                {
                    if (i == index) return item;
                    i++;
                }
            }
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        public static TSource ElementAtOrDefault<TSource>(this IEnumerable<TSource> source, int index)
        {
            if (source == null) ThrowNull(nameof(source));
            if (index >= 0)
            {
                int i = 0;
                foreach (var item in source)
                {
                    if (i == index) return item;
                    i++;
                }
            }
            return default;
        }

        // ---- aggregation ----------------------------------------------

        public static TSource Aggregate<TSource>(this IEnumerable<TSource> source, Func<TSource, TSource, TSource> func)
        {
            if (source == null) ThrowNull(nameof(source));
            if (func == null) ThrowNull(nameof(func));
            using (var e = source.GetEnumerator())
            {
                if (!e.MoveNext()) ThrowNoElements();
                TSource acc = e.Current;
                while (e.MoveNext()) acc = func(acc, e.Current);
                return acc;
            }
        }

        public static TAccumulate Aggregate<TSource, TAccumulate>(this IEnumerable<TSource> source, TAccumulate seed, Func<TAccumulate, TSource, TAccumulate> func)
        {
            if (source == null) ThrowNull(nameof(source));
            if (func == null) ThrowNull(nameof(func));
            TAccumulate acc = seed;
            foreach (var item in source) acc = func(acc, item);
            return acc;
        }

        public static TResult Aggregate<TSource, TAccumulate, TResult>(this IEnumerable<TSource> source, TAccumulate seed, Func<TAccumulate, TSource, TAccumulate> func, Func<TAccumulate, TResult> resultSelector)
        {
            if (resultSelector == null) ThrowNull(nameof(resultSelector));
            return resultSelector(Aggregate(source, seed, func));
        }

        // ---- numeric aggregates (int/long/double + selector) ----------

        public static int Sum(this IEnumerable<int> source)
        {
            if (source == null) ThrowNull(nameof(source));
            int sum = 0;
            foreach (var v in source) sum += v;
            return sum;
        }

        public static int Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, int> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            int sum = 0;
            foreach (var item in source) sum += selector(item);
            return sum;
        }

        public static long Sum(this IEnumerable<long> source)
        {
            if (source == null) ThrowNull(nameof(source));
            long sum = 0;
            foreach (var v in source) sum += v;
            return sum;
        }

        public static long Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, long> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            long sum = 0;
            foreach (var item in source) sum += selector(item);
            return sum;
        }

        public static double Sum(this IEnumerable<double> source)
        {
            if (source == null) ThrowNull(nameof(source));
            double sum = 0;
            foreach (var v in source) sum += v;
            return sum;
        }

        public static double Sum<TSource>(this IEnumerable<TSource> source, Func<TSource, double> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            double sum = 0;
            foreach (var item in source) sum += selector(item);
            return sum;
        }

        public static TSource Min<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            var comparer = Comparer<TSource>.Default;
            bool found = false;
            TSource min = default;
            foreach (var item in source)
            {
                if (!found) { min = item; found = true; }
                else if (comparer.Compare(item, min) < 0) min = item;
            }
            if (!found) ThrowNoElements();
            return min;
        }

        public static TSource Max<TSource>(this IEnumerable<TSource> source)
        {
            if (source == null) ThrowNull(nameof(source));
            var comparer = Comparer<TSource>.Default;
            bool found = false;
            TSource max = default;
            foreach (var item in source)
            {
                if (!found) { max = item; found = true; }
                else if (comparer.Compare(item, max) > 0) max = item;
            }
            if (!found) ThrowNoElements();
            return max;
        }

        public static int Min<TSource>(this IEnumerable<TSource> source, Func<TSource, int> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            bool found = false;
            int min = 0;
            foreach (var item in source)
            {
                int value = selector(item);
                if (!found) { min = value; found = true; }
                else if (value < min) min = value;
            }
            if (!found) ThrowNoElements();
            return min;
        }

        public static int Max<TSource>(this IEnumerable<TSource> source, Func<TSource, int> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            bool found = false;
            int max = 0;
            foreach (var item in source)
            {
                int value = selector(item);
                if (!found) { max = value; found = true; }
                else if (value > max) max = value;
            }
            if (!found) ThrowNoElements();
            return max;
        }

        public static double Average(this IEnumerable<int> source)
        {
            if (source == null) ThrowNull(nameof(source));
            long sum = 0;
            long count = 0;
            foreach (var v in source) { sum += v; count++; }
            if (count == 0) ThrowNoElements();
            return (double)sum / count;
        }

        // ---- array sources (step142) ----------------------------------
        //
        // NOT in the BCL. Overload resolution prefers the exact T[] over
        // the IEnumerable<TSource> form, so array call sites bind here and
        // skip interface dispatch + enumerator boxing entirely. Originally
        // a workaround for arrays not being runtime-IEnumerable<T>; since
        // the Array<T> port (Runtime/ArrayT.cs, same step) arrays are
        // honest interface sources and these are a pure fast path.
        // Semantics identical to the generic forms.

        public static IEnumerable<TResult> Select<TSource, TResult>(this TSource[] source, Func<TSource, TResult> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            for (int i = 0; i < source.Length; i++)
                yield return selector(source[i]);
        }

        public static IEnumerable<TSource> Where<TSource>(this TSource[] source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            for (int i = 0; i < source.Length; i++)
                if (predicate(source[i]))
                    yield return source[i];
        }

        public static bool Contains<TSource>(this TSource[] source, TSource value)
        {
            if (source == null) ThrowNull(nameof(source));
            var comparer = EqualityComparer<TSource>.Default;
            for (int i = 0; i < source.Length; i++)
                if (comparer.Equals(source[i], value))
                    return true;
            return false;
        }

        public static bool Any<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            return source.Length > 0;
        }

        public static bool Any<TSource>(this TSource[] source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            for (int i = 0; i < source.Length; i++)
                if (predicate(source[i]))
                    return true;
            return false;
        }

        public static bool All<TSource>(this TSource[] source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            for (int i = 0; i < source.Length; i++)
                if (!predicate(source[i]))
                    return false;
            return true;
        }

        public static TSource First<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            if (source.Length == 0) ThrowNoElements();
            return source[0];
        }

        public static TSource FirstOrDefault<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            return source.Length == 0 ? default : source[0];
        }

        public static TSource Last<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            if (source.Length == 0) ThrowNoElements();
            return source[source.Length - 1];
        }

        public static TSource LastOrDefault<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            return source.Length == 0 ? default : source[source.Length - 1];
        }

        public static IEnumerable<TSource> Skip<TSource>(this TSource[] source, int count)
        {
            if (source == null) ThrowNull(nameof(source));
            for (int i = count < 0 ? 0 : count; i < source.Length; i++)
                yield return source[i];
        }

        public static IEnumerable<TSource> Take<TSource>(this TSource[] source, int count)
        {
            if (source == null) ThrowNull(nameof(source));
            int limit = count > source.Length ? source.Length : count;
            for (int i = 0; i < limit; i++)
                yield return source[i];
        }

        public static IEnumerable<TSource> SkipWhile<TSource>(this TSource[] source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            int i = 0;
            while (i < source.Length && predicate(source[i])) i++;
            for (; i < source.Length; i++)
                yield return source[i];
        }

        public static IEnumerable<TSource> TakeWhile<TSource>(this TSource[] source, Func<TSource, bool> predicate)
        {
            if (source == null) ThrowNull(nameof(source));
            if (predicate == null) ThrowNull(nameof(predicate));
            for (int i = 0; i < source.Length && predicate(source[i]); i++)
                yield return source[i];
        }

        public static TSource[] ToArray<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            var copy = new TSource[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        public static IEnumerable<TSource> Concat<TSource>(this TSource[] first, IEnumerable<TSource> second)
        {
            if (first == null) ThrowNull(nameof(first));
            if (second == null) ThrowNull(nameof(second));
            for (int i = 0; i < first.Length; i++)
                yield return first[i];
            foreach (var item in second)
                yield return item;
        }

        public static List<TSource> ToList<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            var list = new List<TSource>(source.Length);
            for (int i = 0; i < source.Length; i++)
                list.Add(source[i]);
            return list;
        }

        public static int Count<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            return source.Length;
        }

        public static TSource Min<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            if (source.Length == 0) ThrowNoElements();
            var comparer = Comparer<TSource>.Default;
            TSource min = source[0];
            for (int i = 1; i < source.Length; i++)
                if (comparer.Compare(source[i], min) < 0) min = source[i];
            return min;
        }

        public static TSource Max<TSource>(this TSource[] source)
        {
            if (source == null) ThrowNull(nameof(source));
            if (source.Length == 0) ThrowNoElements();
            var comparer = Comparer<TSource>.Default;
            TSource max = source[0];
            for (int i = 1; i < source.Length; i++)
                if (comparer.Compare(source[i], max) > 0) max = source[i];
            return max;
        }

        public static int Min<TSource>(this TSource[] source, Func<TSource, int> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            if (source.Length == 0) ThrowNoElements();
            int min = selector(source[0]);
            for (int i = 1; i < source.Length; i++)
            {
                int value = selector(source[i]);
                if (value < min) min = value;
            }
            return min;
        }

        public static int Max<TSource>(this TSource[] source, Func<TSource, int> selector)
        {
            if (source == null) ThrowNull(nameof(source));
            if (selector == null) ThrowNull(nameof(selector));
            if (source.Length == 0) ThrowNoElements();
            int max = selector(source[0]);
            for (int i = 1; i < source.Length; i++)
            {
                int value = selector(source[i]);
                if (value > max) max = value;
            }
            return max;
        }
    }
}
