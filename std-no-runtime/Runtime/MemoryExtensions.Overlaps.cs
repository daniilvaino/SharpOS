// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.MemoryExtensions - Overlaps, in-place Replace, ToLowerInvariant/ToUpperInvariant into
// a span and EnumerateRunes, ported from dotnet/runtime
// src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs and
// MemoryExtensions.Globalization.cs (.NET 10, MIT).
//
// SharpOS: Replace's body is upstream's scalar SpanHelpers.Replace loop (the vectorized
// ReplaceValueType path needs SpanHelpers' Vector kernels).
// SharpOS: ToLowerInvariant/ToUpperInvariant take upstream's GlobalizationMode.Invariant branch
// (InvariantModeCasing: simple case mapping, code point by code point across surrogate pairs)
// unconditionally - std is invariant-only - written over CharUnicodeInfo like TextInfo.cs here.
// SharpOS cut: [OverloadResolutionPriority(-1)] on the Span<T> forwarders - attribute not in std.

#pragma warning disable CS8500 // sizeof(T) of a managed T - CoreLib builds with NoWarn 8500 for exactly this

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System
{
    public static partial class MemoryExtensions
    {
        /// <summary>
        /// Determines whether two sequences overlap in memory.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Overlaps<T>(this Span<T> span, ReadOnlySpan<T> other) =>
            Overlaps((ReadOnlySpan<T>)span, other);

        /// <summary>
        /// Determines whether two sequences overlap in memory and outputs the element offset.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Overlaps<T>(this Span<T> span, ReadOnlySpan<T> other, out int elementOffset) =>
            Overlaps((ReadOnlySpan<T>)span, other, out elementOffset);

        /// <summary>
        /// Determines whether two sequences overlap in memory.
        /// </summary>
        public static unsafe bool Overlaps<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other)
        {
            if (span.IsEmpty || other.IsEmpty)
            {
                return false;
            }

            nint byteOffset = Unsafe.ByteOffset(
                ref MemoryMarshal.GetReference(span),
                ref MemoryMarshal.GetReference(other));

            return (nuint)byteOffset < (nuint)((nint)span.Length * sizeof(T)) ||
                    (nuint)byteOffset > (nuint)(-((nint)other.Length * sizeof(T)));
        }

        /// <summary>
        /// Determines whether two sequences overlap in memory and outputs the element offset.
        /// </summary>
        public static unsafe bool Overlaps<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other, out int elementOffset)
        {
            if (span.IsEmpty || other.IsEmpty)
            {
                elementOffset = 0;
                return false;
            }

            nint byteOffset = Unsafe.ByteOffset(
                ref MemoryMarshal.GetReference(span),
                ref MemoryMarshal.GetReference(other));

            if ((nuint)byteOffset < (nuint)((nint)span.Length * sizeof(T)) ||
                (nuint)byteOffset > (nuint)(-((nint)other.Length * sizeof(T))))
            {
                if (byteOffset % sizeof(T) != 0)
                    throw new ArgumentException(SR.Argument_OverlapAlignmentMismatch); // SharpOS: was ThrowHelper.ThrowArgumentException_OverlapAlignmentMismatch()

                elementOffset = (int)(byteOffset / sizeof(T));
                return true;
            }
            else
            {
                elementOffset = 0;
                return false;
            }
        }

        /// <summary>
        /// Replaces all occurrences of <paramref name="oldValue"/> with <paramref name="newValue"/>.
        /// </summary>
        public static void Replace<T>(this Span<T> span, T oldValue, T newValue) where T : IEquatable<T>?
        {
            // SharpOS: upstream's scalar SpanHelpers.Replace (non-vectorized path).
            for (int i = 0; i < span.Length; i++)
            {
                T original = span[i];
                if (oldValue is not null ? oldValue.Equals(original) : original is null)
                {
                    span[i] = newValue;
                }
            }
        }

        /// <summary>
        /// Copies the characters from the source span into the destination, converting each character to lowercase,
        /// using the casing rules of the invariant culture.
        /// </summary>
        /// <returns>The number of characters written into the destination span. If the destination is too small, returns -1.</returns>
        /// <exception cref="InvalidOperationException">The source and destination buffers overlap.</exception>
        public static int ToLowerInvariant(this ReadOnlySpan<char> source, Span<char> destination)
        {
            if (source.Overlaps(destination))
                throw new InvalidOperationException(SR.InvalidOperation_SpanOverlappedOperation);

            // Assuming that changing case does not affect length
            if (destination.Length < source.Length)
                return -1;

            ChangeCaseInvariant(source, destination, toUpper: false); // SharpOS: was InvariantModeCasing.ToLower / TextInfo.Invariant.ChangeCaseToLower
            return source.Length;
        }

        /// <summary>
        /// Copies the characters from the source span into the destination, converting each character to uppercase
        /// using the casing rules of the invariant culture.
        /// </summary>
        /// <returns>The number of characters written into the destination span. If the destination is too small, returns -1.</returns>
        /// <exception cref="InvalidOperationException">The source and destination buffers overlap.</exception>
        public static int ToUpperInvariant(this ReadOnlySpan<char> source, Span<char> destination)
        {
            if (source.Overlaps(destination))
                throw new InvalidOperationException(SR.InvalidOperation_SpanOverlappedOperation);

            // Assuming that changing case does not affect length
            if (destination.Length < source.Length)
                return -1;

            ChangeCaseInvariant(source, destination, toUpper: true); // SharpOS: was InvariantModeCasing.ToUpper / TextInfo.Invariant.ChangeCaseToUpper
            return source.Length;
        }

        // SharpOS: InvariantModeCasing.ToLower/ToUpper(ReadOnlySpan<char>, Span<char>) - simple case
        // mapping per code point; a surrogate pair maps to a pair (simple mappings stay in their plane).
        private static void ChangeCaseInvariant(ReadOnlySpan<char> source, Span<char> destination, bool toUpper)
        {
            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                {
                    uint codePoint = (uint)char.ConvertToUtf32(c, source[i + 1]);
                    codePoint = toUpper ? CharUnicodeInfo.ToUpper(codePoint) : CharUnicodeInfo.ToLower(codePoint);
                    destination[i] = (char)((codePoint - 0x10000) / 0x400 + 0xD800);
                    destination[i + 1] = (char)((codePoint - 0x10000) % 0x400 + 0xDC00);
                    i++;
                    continue;
                }

                destination[i] = toUpper ? CharUnicodeInfo.ToUpper(c) : CharUnicodeInfo.ToLower(c);
            }
        }

        /// <summary>
        /// Returns an enumeration of <see cref="System.Text.Rune"/> from the provided span.
        /// </summary>
        /// <remarks>
        /// Invalid sequences will be represented in the enumeration by <see cref="System.Text.Rune.ReplacementChar"/>.
        /// </remarks>
        public static SpanRuneEnumerator EnumerateRunes(this ReadOnlySpan<char> span)
        {
            return new SpanRuneEnumerator(span);
        }

        /// <summary>
        /// Returns an enumeration of <see cref="System.Text.Rune"/> from the provided span.
        /// </summary>
        /// <remarks>
        /// Invalid sequences will be represented in the enumeration by <see cref="System.Text.Rune.ReplacementChar"/>.
        /// </remarks>
        public static SpanRuneEnumerator EnumerateRunes(this Span<char> span)
        {
            return new SpanRuneEnumerator(span);
        }
    }
}
