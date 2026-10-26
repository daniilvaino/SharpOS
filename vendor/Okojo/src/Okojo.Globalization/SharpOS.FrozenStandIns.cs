// SharpOS cut: std has no System.Collections.Frozen (it lives in
// System.Collections.Immutable). CalendarData and UnitData build their tables
// once and only read them; these stand-ins are the same lookups over
// HashSet/Dictionary, named so the two files stay as upstream wrote them.
#if SHARPOS
namespace Okojo.Globalization;

internal sealed class FrozenSet<T> : HashSet<T>
{
    internal FrozenSet(IEnumerable<T> source, IEqualityComparer<T>? comparer) : base(source, comparer) { }
}

internal sealed class FrozenDictionary<TKey, TValue> : Dictionary<TKey, TValue>
    where TKey : notnull
{
    internal FrozenDictionary(IDictionary<TKey, TValue> source, IEqualityComparer<TKey>? comparer)
        : base(source, comparer) { }
}

internal static class FrozenStandInExtensions
{
    internal static FrozenSet<T> ToFrozenSet<T>(this IEnumerable<T> source, IEqualityComparer<T>? comparer = null) =>
        new(source, comparer);

    internal static FrozenDictionary<TKey, TValue> ToFrozenDictionary<TKey, TValue>(
        this Dictionary<TKey, TValue> source,
        IEqualityComparer<TKey>? comparer = null
    )
        where TKey : notnull => new(source, comparer);
}
#endif
