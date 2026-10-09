// System.Runtime.InteropServices.CollectionsMarshal — direct access to the
// storage of List<T> and Dictionary<TKey, TValue> (step198, System.Text.Json's
// JsonElement.DeepEquals). The members and their meaning are dotnet/runtime's
// (System.Private.CoreLib CollectionsMarshal.cs, MIT); the bodies sit on std's
// own List and Dictionary storage, whose fields differ from the BCL's.

using System.Collections.Generic;

namespace System.Runtime.InteropServices
{
    public static class CollectionsMarshal
    {
        /// <summary>A span over the list's items; invalid once the list grows.</summary>
        public static Span<T> AsSpan<T>(List<T>? list) =>
            list is null ? default : list.ItemsSpan;

        /// <summary>A ref to the value of <paramref name="key"/>, or a null ref when absent.</summary>
        public static ref TValue GetValueRefOrNullRef<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key) where TKey : notnull =>
            ref dictionary.GetValueRefOrNullRef(key);

        /// <summary>A ref to the value of <paramref name="key"/>, adding default(TValue) first when absent.</summary>
        public static ref TValue? GetValueRefOrAddDefault<TKey, TValue>(Dictionary<TKey, TValue> dictionary, TKey key, out bool exists) where TKey : notnull =>
            ref dictionary.GetValueRefOrAddDefault(key, out exists)!;
    }
}
