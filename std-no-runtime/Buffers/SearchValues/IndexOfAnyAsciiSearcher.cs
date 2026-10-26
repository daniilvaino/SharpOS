// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/SearchValues/IndexOfAnyAsciiSearcher.cs
// (.NET 10, MIT) - the scalar part only.
//
// SharpOS cut: every vector path (Ssse3/Avx2/Avx512/AdvSimd/PackedSimd nibble-lookup kernels,
// AsciiState, the UniqueLowNibble search, the result mappers). They need Vector256/Vector512 and
// the Ssse3/Avx2 shuffle intrinsics, which ILC does not target here (AVX is not in the ILC
// instruction set, Vector256 is declared but not accelerated). IsVectorizationSupported is
// therefore false - exactly what the BCL reports on hardware without those ISAs - and what
// remains is the BCL's own scalar fallback: the AnyByteState lookup (BitVector256) loops and the
// INegator scalar members used by BitmapCharSearchValues.

using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal static class IndexOfAnyAsciiSearcher
    {
        // SharpOS cut: Bitmap0/Bitmap1 (the Vector256 nibble bitmaps) - only the scalar Lookup is kept.
        public readonly struct AnyByteState(BitVector256 lookup)
        {
            public readonly BitVector256 Lookup = lookup;
        }

        // SharpOS cut: Ssse3.IsSupported || AdvSimd.Arm64.IsSupported || PackedSimd.IsSupported - no vector kernels ported.
        internal static bool IsVectorizationSupported => false;

        internal static void ComputeAnyByteState(ReadOnlySpan<byte> values, out AnyByteState state)
        {
            // SharpOS cut: the two nibble bitmaps for the vectorized [0, 255] algorithm.
            BitVector256 lookupLocal = default;

            foreach (byte b in values)
            {
                lookupLocal.Set(b);
            }

            state = new AnyByteState(lookupLocal);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsAny<TNegator>(ref byte searchSpace, int searchSpaceLength, ref AnyByteState state)
            where TNegator : struct, INegator =>
            IndexOfAny<TNegator>(ref searchSpace, searchSpaceLength, ref state) >= 0;

        // The `!IsVectorizationSupported || searchSpaceLength < sizeof(ulong)` branch of upstream IndexOfAnyCore.
        public static int IndexOfAny<TNegator>(ref byte searchSpace, int searchSpaceLength, ref AnyByteState state)
            where TNegator : struct, INegator
        {
            ref byte currentSearchSpace = ref searchSpace;
            ref byte searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);

            while (!Unsafe.AreSame(ref currentSearchSpace, ref searchSpaceEnd))
            {
                byte b = currentSearchSpace;
                if (TNegator.NegateIfNeeded(state.Lookup.Contains(b)))
                {
                    return (int)Unsafe.ByteOffset(ref searchSpace, ref currentSearchSpace);
                }

                currentSearchSpace = ref Unsafe.Add(ref currentSearchSpace, 1);
            }

            return -1;
        }

        // The scalar branch of upstream LastIndexOfAny(ref byte, int, ref AnyByteState).
        public static int LastIndexOfAny<TNegator>(ref byte searchSpace, int searchSpaceLength, ref AnyByteState state)
            where TNegator : struct, INegator
        {
            for (int i = searchSpaceLength - 1; i >= 0; i--)
            {
                byte b = Unsafe.Add(ref searchSpace, i);
                if (TNegator.NegateIfNeeded(state.Lookup.Contains(b)))
                {
                    return i;
                }
            }

            return -1;
        }

        internal interface INegator
        {
            static abstract bool NegateIfNeeded(bool result);
            // SharpOS cut: NegateIfNeeded(Vector128/256<byte>) and ExtractMask(Vector128/256<byte>).
        }

        internal readonly struct DontNegate : INegator
        {
            public static bool NegateIfNeeded(bool result) => result;
        }

        internal readonly struct Negate : INegator
        {
            public static bool NegateIfNeeded(bool result) => !result;
        }
    }
}
