// Ported from dotnet/runtime v8.0.27:
//   src/libraries/System.Private.CoreLib/src/System/ValueTuple.cs (MIT)
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/
//     TupleElementNamesAttribute.cs (MIT)
//
// The C# compiler lowers every tuple expression to these types by name, so a
// method returning `(bool ok, int value)` does not compile without them.
//
// Cuts vs original:
//   - Arities 6..8 + TRest nesting — add when a caller appears.
//   - IStructuralEquatable / IStructuralComparable / IComparable / ITuple /
//     IValueTupleInternal — structural-comparer plumbing; Equals/GetHashCode
//     below go through EqualityComparer<T>.Default directly, same observable
//     result for the default-comparer case (same cut as Bcl/Tuple.cs).
//   - ToString() — needs string.Concat over boxed items.
//   - ValueTuple.Create helpers — the compiler emits constructors.
// Field names (Item1..Item5) are unchanged; they are part of the contract the
// compiler generates against.

using System.Collections.Generic;

namespace System
{
    /// <summary>
    /// The ValueTuple types (from arity 0 to 8) comprise the runtime implementation that underlies tuples in C# and struct tuples in F#.
    /// </summary>
    public struct ValueTuple : IEquatable<ValueTuple>
    {
        public override bool Equals(object? obj) => obj is ValueTuple;

        public bool Equals(ValueTuple other) => true;

        public override int GetHashCode() => 0;
    }

    /// <summary>Represents a value tuple with a single component.</summary>
    public struct ValueTuple<T1> : IEquatable<ValueTuple<T1>>
    {
        public T1 Item1;

        public ValueTuple(T1 item1)
        {
            Item1 = item1;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1);
        }

        public override int GetHashCode()
        {
            return EqualityComparer<T1>.Default.GetHashCode(Item1!);
        }
    }

    /// <summary>Represents a value tuple with 2 components.</summary>
    public struct ValueTuple<T1, T2> : IEquatable<ValueTuple<T1, T2>>
    {
        /// <summary>
        /// The current <see cref="ValueTuple{T1, T2}"/> instance's first component.
        /// </summary>
        public T1 Item1;

        /// <summary>
        /// The current <see cref="ValueTuple{T1, T2}"/> instance's second component.
        /// </summary>
        public T2 Item2;

        public ValueTuple(T1 item1, T2 item2)
        {
            Item1 = item1;
            Item2 = item2;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2);
        }

        public override int GetHashCode()
        {
            return Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
        }

        // Upstream defers to HashCode.Combine; ours is the same rotate-and-mix shape,
        // kept local because std ships no System.HashCode yet.
        internal static int Combine(int h1, int h2)
        {
            uint rol5 = ((uint)h1 << 5) | ((uint)h1 >> 27);
            return ((int)rol5 + h1) ^ h2;
        }
    }

    /// <summary>Represents a value tuple with 3 components.</summary>
    public struct ValueTuple<T1, T2, T3> : IEquatable<ValueTuple<T1, T2, T3>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;

        public ValueTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
        }
    }

    /// <summary>Represents a value tuple with 4 components.</summary>
    public struct ValueTuple<T1, T2, T3, T4> : IEquatable<ValueTuple<T1, T2, T3, T4>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3, T4> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3, T4> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3)
                && EqualityComparer<T4>.Default.Equals(Item4, other.Item4);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T4>.Default.GetHashCode(Item4!));
        }
    }

    /// <summary>Represents a value tuple with 5 components.</summary>
    public struct ValueTuple<T1, T2, T3, T4, T5> : IEquatable<ValueTuple<T1, T2, T3, T4, T5>>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;

        public ValueTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
        }

        public override bool Equals(object? obj)
        {
            return obj is ValueTuple<T1, T2, T3, T4, T5> tuple && Equals(tuple);
        }

        public bool Equals(ValueTuple<T1, T2, T3, T4, T5> other)
        {
            return EqualityComparer<T1>.Default.Equals(Item1, other.Item1)
                && EqualityComparer<T2>.Default.Equals(Item2, other.Item2)
                && EqualityComparer<T3>.Default.Equals(Item3, other.Item3)
                && EqualityComparer<T4>.Default.Equals(Item4, other.Item4)
                && EqualityComparer<T5>.Default.Equals(Item5, other.Item5);
        }

        public override int GetHashCode()
        {
            var hash = ValueTuple<T1, T2>.Combine(
                EqualityComparer<T1>.Default.GetHashCode(Item1!),
                EqualityComparer<T2>.Default.GetHashCode(Item2!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T3>.Default.GetHashCode(Item3!));
            hash = ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T4>.Default.GetHashCode(Item4!));
            return ValueTuple<T1, T2>.Combine(hash, EqualityComparer<T5>.Default.GetHashCode(Item5!));
        }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Indicates that the use of <see cref="System.ValueTuple"/> on a member is meant to be treated as a tuple with element names.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Event)]
    public sealed class TupleElementNamesAttribute : Attribute
    {
        private readonly string?[] _transformNames;

        public TupleElementNamesAttribute(string?[] transformNames)
        {
            _transformNames = transformNames;
        }

        /// <summary>
        /// Specifies, in a pre-order depth-first traversal of a type's
        /// construction, which <see cref="System.ValueTuple"/> elements are
        /// meant to carry element names.
        /// </summary>
        public string?[] TransformNames => _transformNames;
    }
}
