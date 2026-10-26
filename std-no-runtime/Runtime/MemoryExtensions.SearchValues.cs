// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// System.MemoryExtensions - the SearchValues<T> overloads, ported from dotnet/runtime
// src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs (.NET 10, MIT).
// Bodies are upstream's: argument check, then the virtual on SearchValues<T>.
//
//   IndexOfAny / IndexOfAnyExcept / LastIndexOfAny / LastIndexOfAnyExcept   Span + ReadOnlySpan
//   ContainsAny / ContainsAnyExcept                                         Span + ReadOnlySpan
//   ReplaceAny / ReplaceAnyExcept, CountAny
//
// SharpOS cut: the SearchValues<string> overloads (IndexOfAny/ContainsAny over
// SearchValues<string>) - SearchValues.Create(ReadOnlySpan<string>, ...) is not ported.
// SharpOS cut: SplitAny(SearchValues<T>) - SpanSplitEnumerator<T> is not in std.
// SharpOS cut: [OverloadResolutionPriority(-1)] on the Span<T> forwarders - attribute not in std;
// the ReadOnlySpan<T>/Span<T> pairs resolve without it here because no array-typed overloads exist.

using System.Buffers;
using System.Runtime.CompilerServices;

namespace System
{
    public static partial class MemoryExtensions
    {
        /// <inheritdoc cref="ContainsAny{T}(ReadOnlySpan{T}, SearchValues{T})"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsAny<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            ContainsAny((ReadOnlySpan<T>)span, values);

        /// <inheritdoc cref="ContainsAnyExcept{T}(ReadOnlySpan{T}, SearchValues{T})"/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsAnyExcept<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            ContainsAnyExcept((ReadOnlySpan<T>)span, values);

        /// <summary>
        /// Searches for any occurrence of any of the specified <paramref name="values"/> and returns true if found. If not found, returns false.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsAny<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.ContainsAny(span);
        }

        /// <summary>
        /// Searches for any value other than the specified <paramref name="values"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool ContainsAnyExcept<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.ContainsAnyExcept(span);
        }

        /// <summary>Searches for the first index of any value other than the specified <paramref name="values"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfAnyExcept<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            IndexOfAnyExcept((ReadOnlySpan<T>)span, values);

        /// <summary>Searches for the first index of any value other than the specified <paramref name="values"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfAnyExcept<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.IndexOfAnyExcept(span);
        }

        /// <summary>Searches for the last index of any value other than the specified <paramref name="values"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastIndexOfAnyExcept<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            LastIndexOfAnyExcept((ReadOnlySpan<T>)span, values);

        /// <summary>Searches for the last index of any value other than the specified <paramref name="values"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastIndexOfAnyExcept<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.LastIndexOfAnyExcept(span);
        }

        /// <summary>
        /// Searches for the first index of any of the specified values similar to calling IndexOf several times with the logical OR operator. If not found, returns -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfAny<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            IndexOfAny((ReadOnlySpan<T>)span, values);

        /// <summary>
        /// Searches for the first index of any of the specified values similar to calling IndexOf several times with the logical OR operator. If not found, returns -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int IndexOfAny<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.IndexOfAny(span);
        }

        /// <summary>
        /// Searches for the last index of any of the specified values similar to calling LastIndexOf several times with the logical OR operator. If not found, returns -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastIndexOfAny<T>(this Span<T> span, SearchValues<T> values) where T : IEquatable<T>? =>
            LastIndexOfAny((ReadOnlySpan<T>)span, values);

        /// <summary>
        /// Searches for the last index of any of the specified values similar to calling LastIndexOf several times with the logical OR operator. If not found, returns -1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int LastIndexOfAny<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            if (values is null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.values);
            }

            return values.LastIndexOfAny(span);
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/>, replacing all occurrences of any of the
        /// elements in <paramref name="values"/> with <paramref name="newValue"/>.
        /// </summary>
        public static void ReplaceAny<T>(this ReadOnlySpan<T> source, Span<T> destination, SearchValues<T> values, T newValue) where T : IEquatable<T>?
        {
            if (source.Length > destination.Length)
            {
                throw new ArgumentException(SR.Argument_DestinationTooShort, nameof(destination));
            }

            // SharpOS: upstream compares source._reference/destination._reference (internal fields);
            // MemoryMarshal.GetReference is the public spelling of the same check.
            if (!Unsafe.AreSame(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source), ref System.Runtime.InteropServices.MemoryMarshal.GetReference((ReadOnlySpan<T>)destination)) &&
                source.Overlaps(destination))
            {
                throw new ArgumentException(SR.InvalidOperation_SpanOverlappedOperation);
            }

            source.CopyTo(destination);
            ReplaceAny(destination.Slice(0, source.Length), values, newValue);
        }

        /// <summary>
        /// Replaces in <paramref name="span"/> all occurrences of any of the
        /// elements in <paramref name="values"/> with <paramref name="newValue"/>.
        /// </summary>
        public static void ReplaceAny<T>(this Span<T> span, SearchValues<T> values, T newValue) where T : IEquatable<T>?
        {
            int pos;
            while ((pos = span.IndexOfAny(values)) >= 0)
            {
                span[pos] = newValue;
                span = span.Slice(pos + 1);
            }
        }

        /// <summary>
        /// Copies <paramref name="source"/> to <paramref name="destination"/>, replacing all occurrences of any of the
        /// elements other than those in <paramref name="values"/> with <paramref name="newValue"/>.
        /// </summary>
        public static void ReplaceAnyExcept<T>(this ReadOnlySpan<T> source, Span<T> destination, SearchValues<T> values, T newValue) where T : IEquatable<T>?
        {
            if (source.Length > destination.Length)
            {
                throw new ArgumentException(SR.Argument_DestinationTooShort, nameof(destination));
            }

            // SharpOS: see ReplaceAny above.
            if (!Unsafe.AreSame(ref System.Runtime.InteropServices.MemoryMarshal.GetReference(source), ref System.Runtime.InteropServices.MemoryMarshal.GetReference((ReadOnlySpan<T>)destination)) &&
                source.Overlaps(destination))
            {
                throw new ArgumentException(SR.InvalidOperation_SpanOverlappedOperation);
            }

            source.CopyTo(destination);
            ReplaceAnyExcept(destination.Slice(0, source.Length), values, newValue);
        }

        /// <summary>
        /// Replaces in <paramref name="span"/> all elements, other than those in <paramref name="values"/>, with <paramref name="newValue"/>.
        /// </summary>
        public static void ReplaceAnyExcept<T>(this Span<T> span, SearchValues<T> values, T newValue) where T : IEquatable<T>?
        {
            int pos;
            while ((pos = span.IndexOfAnyExcept(values)) >= 0)
            {
                span[pos] = newValue;
                span = span.Slice(pos + 1);
            }
        }

        /// <summary>Counts the number of times any of the specified <paramref name="values"/> occurs in the <paramref name="span"/>.</summary>
        public static int CountAny<T>(this ReadOnlySpan<T> span, SearchValues<T> values) where T : IEquatable<T>?
        {
            int count = 0;

            int pos;
            while ((pos = span.IndexOfAny(values)) >= 0)
            {
                count++;
                span = span.Slice(pos + 1);
            }

            return count;
        }
    }
}
