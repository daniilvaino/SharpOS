// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// StringBuilder.GetChunks, ChunkEnumerator and ManyChunkInfo, ported from
// dotnet/runtime src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs
// (.NET 10, MIT) as is, over the same chunk fields (StringBuilder.cs).
//   - SharpOS cut: [EditorBrowsable] on GetEnumerator.

using System.Diagnostics;

namespace System.Text
{
    public sealed partial class StringBuilder
    {
        /// <summary>
        /// GetChunks returns ChunkEnumerator that follows the IEnumerable pattern and
        /// thus can be used in a C# 'foreach' statements to retrieve the data in the StringBuilder
        /// as chunks (ReadOnlyMemory) of characters.  An example use is:
        ///
        ///      foreach (ReadOnlyMemory&lt;char&gt; chunk in sb.GetChunks())
        ///         foreach (char c in chunk.Span)
        ///             { /* operation on c }
        ///
        /// It is undefined what happens if the StringBuilder is modified while the chunk
        /// enumeration is incomplete.  StringBuilder is also not thread-safe, so operating
        /// on it with concurrent threads is illegal.  Finally the ReadOnlyMemory chunks returned
        /// are NOT guaranteed to remain unchanged if the StringBuilder is modified, so do
        /// not cache them for later use either.  This API's purpose is efficiently extracting
        /// the data of a CONSTANT StringBuilder.
        ///
        /// Creating a ReadOnlySpan from a ReadOnlyMemory  (the .Span property) is expensive
        /// compared to the fetching of the character, so create a local variable for the SPAN
        /// if you need to use it in a nested for statement.  For example
        ///
        ///    foreach (ReadOnlyMemory&lt;char&gt; chunk in sb.GetChunks())
        ///    {
        ///         var span = chunk.Span;
        ///         for (int i = 0; i &lt; span.Length; i++)
        ///             { /* operation on span[i] */ }
        ///    }
        /// </summary>
        public ChunkEnumerator GetChunks() => new ChunkEnumerator(this);

        /// <summary>
        /// ChunkEnumerator supports both the IEnumerable and IEnumerator pattern so foreach
        /// works (see GetChunks).  It needs to be public (so the compiler can use it
        /// when building a foreach statement) but users typically don't use it explicitly.
        /// (which is why it is a nested type).
        /// </summary>
        public struct ChunkEnumerator
        {
            private readonly StringBuilder _firstChunk; // The first Stringbuilder chunk (which is the end of the logical string)
            private StringBuilder? _currentChunk;        // The chunk that this enumerator is currently returning (Current).
            private readonly ManyChunkInfo? _manyChunks; // Only used for long string builders with many chunks (see constructor)

            /// <summary>
            /// Implement IEnumerable.GetEnumerator() to return  'this' as the IEnumerator
            /// </summary>
            public ChunkEnumerator GetEnumerator() { return this; }

            /// <summary>
            /// Implements the IEnumerator pattern.
            /// </summary>
            public bool MoveNext()
            {
                if (_currentChunk == _firstChunk)
                {
                    return false;
                }


                if (_manyChunks != null)
                {
                    return _manyChunks.MoveNext(ref _currentChunk);
                }

                StringBuilder next = _firstChunk;
                while (next.m_ChunkPrevious != _currentChunk)
                {
                    Debug.Assert(next.m_ChunkPrevious != null);
                    next = next.m_ChunkPrevious;
                }
                _currentChunk = next;
                return true;
            }

            /// <summary>
            /// Implements the IEnumerator pattern.
            /// </summary>
            public ReadOnlyMemory<char> Current
            {
                get
                {
                    if (_currentChunk == null)
                    {
                        ThrowHelper.ThrowInvalidOperationException_InvalidOperation_EnumOpCantHappen();
                    }

                    return new ReadOnlyMemory<char>(_currentChunk.m_ChunkChars, 0, _currentChunk.m_ChunkLength);
                }
            }

            #region private
            internal ChunkEnumerator(StringBuilder stringBuilder)
            {
                Debug.Assert(stringBuilder != null);
                _firstChunk = stringBuilder;
                _currentChunk = null;   // MoveNext will find the last chunk if we do this.
                _manyChunks = null;

                // There is a performance-vs-allocation tradeoff.   Because the chunks
                // are a linked list with each chunk pointing to its PREDECESSOR, walking
                // the list FORWARD is not efficient.   If there are few chunks (< 8) we
                // simply scan from the start each time, and tolerate the N*N behavior.
                // However above this size, we allocate an array to hold reference to all
                // the chunks and we can be efficient for large N.
                int chunkCount = ChunkCount(stringBuilder);
                if (8 < chunkCount)
                {
                    _manyChunks = new ManyChunkInfo(stringBuilder, chunkCount);
                }
            }

            private static int ChunkCount(StringBuilder? stringBuilder)
            {
                int ret = 0;
                while (stringBuilder != null)
                {
                    ret++;
                    stringBuilder = stringBuilder.m_ChunkPrevious;
                }
                return ret;
            }

            /// <summary>
            /// Used to hold all the chunks indexes when you have many chunks.
            /// </summary>
            private sealed class ManyChunkInfo
            {
                private readonly StringBuilder[] _chunks;    // These are in normal order (first chunk first)
                private int _chunkPos;

                public bool MoveNext(ref StringBuilder? current)
                {
                    int pos = ++_chunkPos;
                    if (_chunks.Length <= pos)
                    {
                        return false;
                    }
                    current = _chunks[pos];
                    return true;
                }

                public ManyChunkInfo(StringBuilder? stringBuilder, int chunkCount)
                {
                    _chunks = new StringBuilder[chunkCount];
                    while (0 <= --chunkCount)
                    {
                        Debug.Assert(stringBuilder != null);
                        _chunks[chunkCount] = stringBuilder;
                        stringBuilder = stringBuilder.m_ChunkPrevious;
                    }
                    _chunkPos = -1;
                }
            }
#endregion
        }
    }
}
