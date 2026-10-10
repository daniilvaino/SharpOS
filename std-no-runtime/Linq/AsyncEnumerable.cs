// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/runtime (.NET 10) System.Linq.AsyncEnumerable:
// src/System/Linq/ToAsyncEnumerable.cs and Empty.cs, unchanged but for the
// cut below. System.Linq.AsyncEnumerable is BCL from .NET 10, so these keep
// its namespace; the rest of the class is not ported yet (step200: BabyKusto
// needs ToAsyncEnumerable).
//
// Cuts: IOrderedAsyncEnumerable<T> on the empty sequence and its two
// CreateOrderedAsyncEnumerable members — std has no ordered async operators
// to call them.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace System.Linq
{
    public static partial class AsyncEnumerable
    {
        /// <summary>Creates a new <see cref="IAsyncEnumerable{T}"/> that iterates through <paramref name="source"/>.</summary>
        /// <typeparam name="TSource">The type of the elements of source.</typeparam>
        /// <param name="source">An <see cref="IEnumerable{T}"/> of the elements to enumerate.</param>
        /// <returns>An <see cref="IAsyncEnumerable{T}"/> containing the sequence of elements from <paramref name="source"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// Each iteration through the resulting <see cref="IAsyncEnumerable{T}"/> will iterate through the <paramref name="source"/>.
        /// </remarks>
        public static IAsyncEnumerable<TSource> ToAsyncEnumerable<TSource>(
            this IEnumerable<TSource> source)
        {
            ArgumentNullException.ThrowIfNull(source);

            return source switch
            {
                TSource[] array => array.Length == 0 ? Empty<TSource>() : FromArray(array),
                List<TSource> list => FromList(list),
                IList<TSource> list => FromIList(list),
                _ when source == Enumerable.Empty<TSource>() => Empty<TSource>(),
                _ => FromIterator(source),
            };

#pragma warning disable CS1998 // as upstream: async iterators with nothing to await
            static async IAsyncEnumerable<TSource> FromArray(TSource[] source)
            {
                for (int i = 0; ; i++)
                {
                    int localI = i;
                    TSource[] localSource = source;
                    if ((uint)localI >= (uint)localSource.Length)
                    {
                        break;
                    }
                    yield return localSource[localI];
                }
            }

            static async IAsyncEnumerable<TSource> FromList(List<TSource> source)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    yield return source[i];
                }
            }

            static async IAsyncEnumerable<TSource> FromIList(IList<TSource> source)
            {
                int count = source.Count;
                for (int i = 0; i < count; i++)
                {
                    yield return source[i];
                }
            }

            static async IAsyncEnumerable<TSource> FromIterator(IEnumerable<TSource> source)
            {
                foreach (TSource element in source)
                {
                    yield return element;
                }
            }
#pragma warning restore CS1998
        }

        /// <summary>
        /// Returns an empty <see cref="IAsyncEnumerable{T}"/> that has the specified type argument.
        /// </summary>
        /// <typeparam name="TResult">The type of the elements of the sequence.</typeparam>
        /// <returns>An empty <see cref="IAsyncEnumerable{T}"/> whose type argument is <typeparamref name="TResult"/>.</returns>
        public static IAsyncEnumerable<TResult> Empty<TResult>() => EmptyAsyncEnumerable<TResult>.Instance;

        private sealed class EmptyAsyncEnumerable<TResult> :
            IAsyncEnumerable<TResult>, IAsyncEnumerator<TResult>
        {
            public static readonly EmptyAsyncEnumerable<TResult> Instance = new();

            public IAsyncEnumerator<TResult> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

            public ValueTask<bool> MoveNextAsync() => default;

            public TResult Current => default!;

            public ValueTask DisposeAsync() => default;
        }
    }
}
