// Ported from dotnet/runtime v8.0.27:
//   src/libraries/System.Private.CoreLib/src/System/ValueTuple.cs (MIT)
// Arities 6, 7 and 8 (TRest), cut from Runtime/ValueTuple.cs until a caller
// appeared (Lua-CSharp returns a 6-tuple). Same shape and same cuts as that
// file: fields, constructor, Equals and GetHashCode through
// EqualityComparer<T>.Default; no IStructural* / IComparable / ITuple, no
// ToString.

using System.Collections.Generic;

namespace System
{
    /// <summary>Represents a value tuple with 6 components.</summary>
    public struct ValueTuple<T1, T2, T3, T4, T5, T6> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3, T4, T5, T6> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3)
                && EqualityComparer<T4>.Default.Equals(Item4, other.Item4)
                && EqualityComparer<T5>.Default.Equals(Item5, other.Item5)
                && EqualityComparer<T6>.Default.Equals(Item6, other.Item6);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T4>.Default.GetHashCode(Item4!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T5>.Default.GetHashCode(Item5!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T6>.Default.GetHashCode(Item6!));
        }
    }

    /// <summary>Represents a value tuple with 7 components.</summary>
    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
            Item7 = item7;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3)
                && EqualityComparer<T4>.Default.Equals(Item4, other.Item4)
                && EqualityComparer<T5>.Default.Equals(Item5, other.Item5)
                && EqualityComparer<T6>.Default.Equals(Item6, other.Item6)
                && EqualityComparer<T7>.Default.Equals(Item7, other.Item7);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T4>.Default.GetHashCode(Item4!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T5>.Default.GetHashCode(Item5!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T6>.Default.GetHashCode(Item6!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T7>.Default.GetHashCode(Item7!));
        }
    }

    /// <summary>Represents a value tuple with 8 components.</summary>
    public struct ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> : IEquatable<ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest>>
        where TRest : struct
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;
        public TRest Rest;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, TRest rest)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
            Item7 = item7;
            Rest = rest;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3, T4, T5, T6, T7, TRest> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3)
                && EqualityComparer<T4>.Default.Equals(Item4, other.Item4)
                && EqualityComparer<T5>.Default.Equals(Item5, other.Item5)
                && EqualityComparer<T6>.Default.Equals(Item6, other.Item6)
                && EqualityComparer<T7>.Default.Equals(Item7, other.Item7)
                && EqualityComparer<TRest>.Default.Equals(Rest, other.Rest);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T4>.Default.GetHashCode(Item4!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T5>.Default.GetHashCode(Item5!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T6>.Default.GetHashCode(Item6!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T7>.Default.GetHashCode(Item7!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<TRest>.Default.GetHashCode(Rest!));
        }
    }
}
