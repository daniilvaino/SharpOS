// Ported from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/Vector256_1.cs
//
// Mirrors Vector128_1.cs at twice the width. Whether it is ever accelerated is
// ILC's call, not ours: without AVX in the target instruction set it folds
// IsHardwareAccelerated to false and a consumer's Vector256 branch disappears
// with it. That is the wanted state today — AVX register state would have to be
// saved across context switches first.
//
// Cut to what a first bring-up needs: the type itself, its size and count, the
// zero value and equality. Everything that formats, debugs or converts is gone,
// as are the generic-math interfaces, and every
// ThrowForUnsupportedIntrinsicsVector256BaseType guard — an unsupported T is a
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
    [StructLayout(LayoutKind.Sequential, Size = Vector256.Size)]
    public readonly struct Vector256<T>
        where T : struct
    {
        // Present so the alignment is 8 rather than 1, exactly as upstream.
        private readonly ulong _00;
        private readonly ulong _01;
        private readonly ulong _02;
        private readonly ulong _03;

        /// <summary>Number of <typeparamref name="T"/> in the vector.</summary>
        public static int Count
        {
            [Intrinsic]
            get => Vector256.Size / Unsafe.SizeOf<T>();
        }

        /// <summary>A vector with every bit clear.</summary>
        public static Vector256<T> Zero
        {
            [Intrinsic]
            get => default;
        }

        /// <summary>A vector with every bit set.</summary>
        public static Vector256<T> AllBitsSet
        {
            [Intrinsic]
            get => Vector256.Create(0xFFFFFFFFFFFFFFFFul).As<ulong, T>();
        }

        [Intrinsic]
        public static bool operator ==(Vector256<T> left, Vector256<T> right)
        {
            // Upstream compares element by element through Scalar<T>; the whole
            // value is bits, and equality of the bits is equality of the value.
            return left._00 == right._00 && left._01 == right._01
                && left._02 == right._02 && left._03 == right._03;
        }

        [Intrinsic]
        public static bool operator !=(Vector256<T> left, Vector256<T> right) => !(left == right);

        [Intrinsic]
        public static Vector256<T> operator |(Vector256<T> left, Vector256<T> right)
        {
            Vector256<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector256<ulong>, ulong>(ref result);
            slot = left._00 | right._00;
            Unsafe.Add(ref slot, 1) = left._01 | right._01;
            Unsafe.Add(ref slot, 2) = left._02 | right._02;
            Unsafe.Add(ref slot, 3) = left._03 | right._03;
            return result.As<ulong, T>();
        }

        [Intrinsic]
        public static Vector256<T> operator &(Vector256<T> left, Vector256<T> right)
        {
            Vector256<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector256<ulong>, ulong>(ref result);
            slot = left._00 & right._00;
            Unsafe.Add(ref slot, 1) = left._01 & right._01;
            Unsafe.Add(ref slot, 2) = left._02 & right._02;
            Unsafe.Add(ref slot, 3) = left._03 & right._03;
            return result.As<ulong, T>();
        }

        [Intrinsic]
        public static Vector256<T> operator ^(Vector256<T> left, Vector256<T> right)
        {
            Vector256<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector256<ulong>, ulong>(ref result);
            slot = left._00 ^ right._00;
            Unsafe.Add(ref slot, 1) = left._01 ^ right._01;
            Unsafe.Add(ref slot, 2) = left._02 ^ right._02;
            Unsafe.Add(ref slot, 3) = left._03 ^ right._03;
            return result.As<ulong, T>();
        }


        [Intrinsic]
        public static Vector256<T> operator ~(Vector256<T> value)
        {
            Vector256<ulong> result = default;
            ref ulong slot = ref Unsafe.As<Vector256<ulong>, ulong>(ref result);
            slot = ~value._00;
            Unsafe.Add(ref slot, 1) = ~value._01;
            Unsafe.Add(ref slot, 2) = ~value._02;
            Unsafe.Add(ref slot, 3) = ~value._03;
            return result.As<ulong, T>();
        }

        /// <summary>Element-wise subtraction.</summary>
        /// <remarks>
        /// No software fallback, for the same reason the comparisons have none:
        /// subtracting lanes means knowing the element type, and guessing it
        /// would read as working SIMD until something downstream disagreed.
        /// </remarks>
        [Intrinsic]
        public static Vector256<T> operator -(Vector256<T> left, Vector256<T> right)
        {
            throw new NotSupportedException(
                "Vector256 arithmetic needs ILC to recognise it; check IsHardwareAccelerated first.");
        }

        public override bool Equals(object? obj) => obj is Vector256<T> other && this == other;

        public bool Equals(Vector256<T> other) => this == other;

        public override int GetHashCode()
        {
            ulong mixed = _00 ^ _01 ^ _02 ^ _03;
            return (int)(mixed ^ (mixed >> 32));
        }
    }
}
