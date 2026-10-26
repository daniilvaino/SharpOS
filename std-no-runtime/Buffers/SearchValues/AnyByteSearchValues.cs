// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/SearchValues/AnyByteSearchValues.cs
// (.NET 10, MIT). SharpOS cut: the [CompExactlyDependsOn(Ssse3/AdvSimd/PackedSimd)] attributes - the
// searcher it calls is the scalar-only port (see IndexOfAnyAsciiSearcher.cs).

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal sealed class AnyByteSearchValues : SearchValues<byte>
    {
        private IndexOfAnyAsciiSearcher.AnyByteState _state;

        public AnyByteSearchValues(ReadOnlySpan<byte> values) =>
            IndexOfAnyAsciiSearcher.ComputeAnyByteState(values, out _state);

        internal override byte[] GetValues() =>
            _state.Lookup.GetByteValues();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(byte value) =>
            _state.Lookup.Contains(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);
    }
}
