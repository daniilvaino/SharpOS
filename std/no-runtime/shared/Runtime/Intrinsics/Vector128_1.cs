// Ported from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/Vector128_1.cs
//
// Cut to what a first bring-up needs: the type itself, its size and count, the
// zero value and equality. Everything that formats, debugs or converts is gone,
// as are the generic-math interfaces, and every
// ThrowForUnsupportedIntrinsicsVector128BaseType guard — an unsupported T is a
// compile-time mistake here, not a runtime one.
//
// The namespace is NOT a choice. ILC recognises intrinsics by namespace and type
// name, so a vector type that lives anywhere else is an ordinary struct and
// every operation on it stays a real method call. This is the one place where
// the naming rule gives way: the name IS the mechanism.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Runtime.Intrinsics
{
    [Intrinsic]
    [StructLayout(LayoutKind.Sequential, Size = Vector128.Size)]
    public readonly struct Vector128<T>
        where T : struct
    {
        // Present so the alignment is 8 rather than 1, exactly as upstream.
        private readonly ulong _00;
        private readonly ulong _01;

        /// <summary>Number of <typeparamref name="T"/> in the vector.</summary>
        public static int Count
        {
            [Intrinsic]
            get => Vector128.Size / Unsafe.SizeOf<T>();
        }

        /// <summary>A vector with every bit clear.</summary>
        public static Vector128<T> Zero
        {
            [Intrinsic]
            get => default;
        }

        /// <summary>A vector with every bit set.</summary>
        public static Vector128<T> AllBitsSet
        {
            [Intrinsic]
            get => Vector128.Create(0xFFFFFFFFFFFFFFFFul).As<ulong, T>();
        }

        [Intrinsic]
        public static bool operator ==(Vector128<T> left, Vector128<T> right)
        {
            // Upstream compares element by element through Scalar<T>; the whole
            // value is bits, and equality of the bits is equality of the value.
            return left._00 == right._00 && left._01 == right._01;
        }

        [Intrinsic]
        public static bool operator !=(Vector128<T> left, Vector128<T> right) => !(left == right);

        [Intrinsic]
        public static Vector128<T> operator |(Vector128<T> left, Vector128<T> right)
        {
            Vector128<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector128<ulong>, ulong>(ref result);
            slot = left._00 | right._00;
            Unsafe.Add(ref slot, 1) = left._01 | right._01;
            return result.As<ulong, T>();
        }

        [Intrinsic]
        public static Vector128<T> operator &(Vector128<T> left, Vector128<T> right)
        {
            Vector128<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector128<ulong>, ulong>(ref result);
            slot = left._00 & right._00;
            Unsafe.Add(ref slot, 1) = left._01 & right._01;
            return result.As<ulong, T>();
        }

        [Intrinsic]
        public static Vector128<T> operator ^(Vector128<T> left, Vector128<T> right)
        {
            Vector128<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector128<ulong>, ulong>(ref result);
            slot = left._00 ^ right._00;
            Unsafe.Add(ref slot, 1) = left._01 ^ right._01;
            return result.As<ulong, T>();
        }


        [Intrinsic]
        public static Vector128<T> operator ~(Vector128<T> value)
        {
            Vector128<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector128<ulong>, ulong>(ref result);
            slot = ~value._00;
            Unsafe.Add(ref slot, 1) = ~value._01;
            return result.As<ulong, T>();
        }

        /// <summary>Element-wise subtraction.</summary>
        /// <remarks>
        /// No software fallback, for the same reason the comparisons have none:
        /// subtracting lanes means knowing the element type, and guessing it
        /// would read as working SIMD until something downstream disagreed.
        /// </remarks>
        [Intrinsic]
        public static Vector128<T> operator -(Vector128<T> left, Vector128<T> right)
        {
            throw new NotSupportedException(
                "Vector128 arithmetic needs ILC to recognise it; check IsHardwareAccelerated first.");
        }

        public override bool Equals(object? obj) => obj is Vector128<T> other && this == other;

        public bool Equals(Vector128<T> other) => this == other;

        public override int GetHashCode() => (int)(_00 ^ (_00 >> 32) ^ _01 ^ (_01 >> 32));
    }
}
