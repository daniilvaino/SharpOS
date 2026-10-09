// System.Collections.Generic.CollectionExtensions — ported from dotnet/runtime
// main (dotnet-runtime-sharpos/), System.Private.CoreLib/src/System/Collections/
// Generic/CollectionExtensions.cs (MIT). Step198 (System.Text.Json.Nodes'
// JsonArray: list.AddRange(span)).
//
// Cuts:
//   - AsReadOnly(ISet<T>): no ReadOnlySet<T> in std.
//   - The List<T> span members reach std's List through InsertSpan (its fields
//     and growth differ from the BCL's _items/_size/Grow); behaviour is the
//     BCL's. ThrowHelper.ThrowArgumentNullException(ExceptionArgument.x) →
//     ArgumentNullException(nameof(x)).

using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Generic
{
    public static class CollectionExtensions
    {
        public static TValue? GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key) =>
            dictionary.GetValueOrDefault(key, default!);

        public static TValue GetValueOrDefault<TKey, TValue>(this IReadOnlyDictionary<TKey, TValue> dictionary, TKey key, TValue defaultValue)
        {
            if (dictionary is null)
                throw new ArgumentNullException(nameof(dictionary));

            return dictionary.TryGetValue(key, out TValue? value) ? value : defaultValue;
        }

        public static bool TryAdd<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, TValue value)
        {
            if (dictionary is null)
                throw new ArgumentNullException(nameof(dictionary));

            if (!dictionary.ContainsKey(key))
            {
                dictionary.Add(key, value);
                return true;
            }

            return false;
        }

        public static bool Remove<TKey, TValue>(this IDictionary<TKey, TValue> dictionary, TKey key, [MaybeNullWhen(false)] out TValue value)
        {
            if (dictionary is null)
                throw new ArgumentNullException(nameof(dictionary));

            if (dictionary.TryGetValue(key, out value))
            {
                dictionary.Remove(key);
                return true;
            }

            value = default;
            return false;
        }

        public static ReadOnlyCollection<T> AsReadOnly<T>(this IList<T> list) =>
            new ReadOnlyCollection<T>(list);

        public static ReadOnlyDictionary<TKey, TValue> AsReadOnly<TKey, TValue>(this IDictionary<TKey, TValue> dictionary) where TKey : notnull =>
            new ReadOnlyDictionary<TKey, TValue>(dictionary);

        public static void AddRange<T>(this List<T> list, params ReadOnlySpan<T> source)
        {
            if (list is null)
                throw new ArgumentNullException(nameof(list));

            list.InsertSpan(list.Count, source);
        }

        public static void InsertRange<T>(this List<T> list, int index, params ReadOnlySpan<T> source)
        {
            if (list is null)
                throw new ArgumentNullException(nameof(list));

            list.InsertSpan(index, source);
        }

        public static void CopyTo<T>(this List<T> list, Span<T> destination)
        {
            if (list is null)
                throw new ArgumentNullException(nameof(list));

            ((ReadOnlySpan<T>)list.ItemsSpan).CopyTo(destination);
        }
    }
}
