// Ported from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Runtime/Intrinsics/Vector256.cs
//
// Cut to create, reinterpret, load, store and compare — what the first consumer
// needs. Upstream's software fallbacks go through the generic-math Scalar<T>
// helpers, which need an interface surface this environment does not have, so
// the comparisons here refuse rather than reimplement (see Compare below).
//
// Signatures follow upstream EXACTLY, generic parameters included. That is not
// tidiness: ILC recognises an intrinsic by its shape, and a ushort overload
// where upstream declares Vector256.Equals<T> is a different method — it
// compiles, it computes the right answer, and it never becomes an instruction.
//
// See Vector128_1.cs on why these live under the canonical namespace, and
// Vector256_1.cs on when this width is accelerated at all.

using System.Runtime.CompilerServices;

namespace System.Runtime.Intrinsics
{
    public static class Vector256
    {
        internal const int Size = 32;

        private const int CompareEqual = 0;
        private const int CompareLess = 1;
        private const int CompareLessOrEqual = 2;
        private const int CompareGreater = 3;
        private const int CompareGreaterOrEqual = 4;

        /// <summary>
        /// Whether vector operations become hardware instructions.
        /// </summary>
        /// <remarks>
        /// Upstream writes this as a self-referencing property, which ILC always
        /// replaces. Here it returns false: if recognition ever failed,
        /// upstream's shape would recurse until the stack ran out, and the
        /// honest answer is "not accelerated" so a consumer takes its scalar
        /// path.
        /// </remarks>
        public static bool IsHardwareAccelerated
        {
            [Intrinsic]
            get => false;
        }

        [Intrinsic]
        public static Vector256<TTo> As<TFrom, TTo>(this Vector256<TFrom> vector)
            where TFrom : struct
            where TTo : struct
            => Unsafe.As<Vector256<TFrom>, Vector256<TTo>>(ref vector);

        [Intrinsic]
        public static Vector256<T> Create<T>(T value) where T : struct
        {
            Vector256<T> result = default;
            ref T slot = ref Unsafe.As<Vector256<T>, T>(ref result);

            for (int i = 0; i < Vector256<T>.Count; i++)
                Unsafe.Add(ref slot, i) = value;

            return result;
        }

        [Intrinsic]
        public static Vector256<T> Equals<T>(Vector256<T> left, Vector256<T> right) where T : struct
            => Compare(left, right, CompareEqual);

        [Intrinsic]
        public static Vector256<T> LessThan<T>(Vector256<T> left, Vector256<T> right) where T : struct
            => Compare(left, right, CompareLess);

        [Intrinsic]
        public static Vector256<T> LessThanOrEqual<T>(Vector256<T> left, Vector256<T> right) where T : struct
            => Compare(left, right, CompareLessOrEqual);

        [Intrinsic]
        public static Vector256<T> GreaterThan<T>(Vector256<T> left, Vector256<T> right) where T : struct
            => Compare(left, right, CompareGreater);

        [Intrinsic]
        public static Vector256<T> GreaterThanOrEqual<T>(Vector256<T> left, Vector256<T> right) where T : struct
            => Compare(left, right, CompareGreaterOrEqual);

        /// <summary>Reads a vector out of memory; alignment is not required.</summary>
        [Intrinsic]
        public static unsafe Vector256<T> Load<T>(T* source) where T : unmanaged
            => Unsafe.ReadUnaligned<Vector256<T>>(source);

        /// <summary>Writes a vector to memory; alignment is not required.</summary>
        [Intrinsic]
        public static unsafe void Store<T>(this Vector256<T> source, T* destination) where T : unmanaged
            => Unsafe.WriteUnaligned(destination, source);

        /// <summary>
        /// Stands in for a comparison ILC did not take over.
        /// </summary>
        /// <remarks>
        /// Deliberately not a software implementation. Comparing lanes without
        /// knowing whether the element type is signed, unsigned or floating
        /// point means guessing, and a guess here reads as working SIMD right up
        /// until something downstream disagrees. A consumer that checks
        /// IsHardwareAccelerated — false in exactly this case — takes its scalar
        /// path and never arrives here.
        /// </remarks>
        private static Vector256<T> Compare<T>(Vector256<T> left, Vector256<T> right, int comparison)
            where T : struct
        {
            throw new NotSupportedException(
                "Vector256 comparisons need ILC to recognise them; check IsHardwareAccelerated first.");
        }
    }
}
