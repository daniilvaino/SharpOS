// MemoryExtensions.Sort(keys, items) and CommonPrefixLength — BCL's
// signatures (System.Private.CoreLib, MemoryExtensions.cs); the sort goes
// through Array.Sort over copies (step224, System.Text.Json).

using System.Collections.Generic;

namespace System
{
    public static partial class MemoryExtensions
    {
        public static void Sort<TKey, TValue>(this Span<TKey> keys, Span<TValue> items)
        {
            if (keys.Length != items.Length)
                throw new ArgumentException("The keys and items spans must be of the same length.");
            if (keys.Length < 2) return;

            TKey[] keyCopy = keys.ToArray();
            int[] order = new int[keyCopy.Length];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Comparer<TKey> comparer = Comparer<TKey>.Default;
            Array.Sort(order, (a, b) =>
            {
                int c = comparer.Compare(keyCopy[a], keyCopy[b]);
                return c != 0 ? c : a.CompareTo(b);
            });
            TValue[] itemCopy = items.ToArray();
            for (int i = 0; i < order.Length; i++)
            {
                keys[i] = keyCopy[order[i]];
                items[i] = itemCopy[order[i]];
            }
        }

        public static int CommonPrefixLength<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other)
        {
            int length = Math.Min(span.Length, other.Length);
            EqualityComparer<T> comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < length; i++)
                if (!comparer.Equals(span[i], other[i])) return i;
            return length;
        }

        public static int CommonPrefixLength<T>(this Span<T> span, ReadOnlySpan<T> other) =>
            CommonPrefixLength((ReadOnlySpan<T>)span, other);
    }
}
