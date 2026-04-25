// System.Text.StringBuilder — ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs
//
// This is an honest BCL port: the full linked-chunk internal structure
// (m_ChunkChars + m_ChunkPrevious + m_ChunkOffset + m_ChunkLength +
// m_MaxCapacity) and all the core algorithms (ExpandByABlock, MakeRoom,
// ReplaceInPlaceAtChunk, FindChunkForIndex, Append(ref char, int) with
// AppendWithExpansion, CopyTo chunk-walk, Length setter chunk-trim,
// Remove-with-chunk-split) are preserved verbatim. Consumers see the
// same asymptotic cost model BCL offers.
//
// Cuts vs BCL — **public API drops**, each listed with replacement:
//
//  - GetChunks() + ChunkEnumerator + ManyChunkInfo — uses ReadOnlyMemory
//    <char> which we haven't ported yet. Re-add once ReadOnlyMemory
//    lands.
//  - Append(ReadOnlyMemory<char>) — same reason.
//  - Append(bool)/Append(sbyte/short/...) — BCL routes through
//    ISpanFormattable.TryFormat. We route to NumberFormatting.* (managed,
//    one heap alloc per call instead of in-place span write). Full zero-
//    alloc port needs ISpanFormattable + Number.Format.cs first.
//  - AppendFormat overloads + AppendFormatHelper (format strings
//    like "{0:X8}") — skip. Callers explicitly format via
//    NumberFormatting.UIntToHex(...) etc., then Append.
//  - AppendInterpolatedStringHandler + the overloads that take it
//    ([InterpolatedStringHandlerArgument("")]) — interpolated strings
//    `$"..."`. Skip; callers chain Append calls manually.
//  - AppendJoin — uses IEnumerable<T>/AppendFormat; skip. `string.Join`
//    lives on System.String separately.
//  - InsertSpanFormattable<T> + Insert(sbyte/short/...) — skip, same
//    reason as Append(sbyte/...).
//  - Replace(string oldValue, string newValue[, int, int]) — uses
//    Span<char>.IndexOf (search) + ArgumentException.ThrowIfNullOrEmpty.
//    Skip for now, keep Replace(char, char).
//  - Equals(ReadOnlySpan<char>) — uses SequenceEqual / EqualsOrdinal,
//    which we haven't ported.
//  - Serialization (ISerializable, ctor(SerializationInfo, ...),
//    GetObjectData) — standard cut.
//
// Throws in BCL become Halt() here (no exception engine). Debug.Assert
// calls stay (no-op via Conditional).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SharpOS.Std.NoRuntime;

namespace System.Text
{
    public sealed partial class StringBuilder
    {
        /// <summary>The character buffer for this chunk.</summary>
        internal char[] m_ChunkChars;

        /// <summary>The chunk that logically precedes this chunk.</summary>
        internal StringBuilder m_ChunkPrevious;

        /// <summary>Number of characters in this chunk in use, from the start of the buffer.</summary>
        internal int m_ChunkLength;

        /// <summary>Logical offset of this chunk's characters in the string it is a part of.</summary>
        internal int m_ChunkOffset;

        /// <summary>Maximum capacity this builder is allowed to have.</summary>
        internal int m_MaxCapacity;

        internal const int DefaultCapacity = 16;

        // Keep chunk arrays under LOH (40k chars ~ 80k bytes). Big chunk
        // → fewer allocations + faster copies; small chunk → less waste.
        internal const int MaxChunkSize = 8000;

        public StringBuilder()
        {
            m_MaxCapacity = int.MaxValue;
            m_ChunkChars = new char[DefaultCapacity];
        }

        public StringBuilder(int capacity) : this(capacity, int.MaxValue) { }

        public StringBuilder(string value) : this(value, DefaultCapacity) { }

        public StringBuilder(string value, int capacity) : this(value, 0, value == null ? 0 : value.Length, capacity) { }

        public StringBuilder(string value, int startIndex, int length, int capacity)
        {
            if (capacity < 0) Halt();
            if (length < 0) Halt();
            if (startIndex < 0) Halt();

            if (value == null) value = "";
            if (startIndex > value.Length - length) Halt();

            m_MaxCapacity = int.MaxValue;
            if (capacity == 0) capacity = DefaultCapacity;
            capacity = Math.Max(capacity, length);

            m_ChunkChars = GC.AllocateUninitializedArray<char>(capacity);
            m_ChunkLength = length;

            for (int i = 0; i < length; i++)
                m_ChunkChars[i] = value[startIndex + i];
        }

        public StringBuilder(int capacity, int maxCapacity)
        {
            if (capacity > maxCapacity) Halt();
            if (maxCapacity < 1) Halt();
            if (capacity < 0) Halt();

            if (capacity == 0) capacity = Math.Min(DefaultCapacity, maxCapacity);

            m_MaxCapacity = maxCapacity;
            m_ChunkChars = GC.AllocateUninitializedArray<char>(capacity);
        }

        // Private ctor — fork a chunk for the predecessor slot when
        // ExpandByABlock installs a new head chunk.
        private StringBuilder(StringBuilder from)
        {
            m_ChunkLength = from.m_ChunkLength;
            m_ChunkOffset = from.m_ChunkOffset;
            m_ChunkChars = from.m_ChunkChars;
            m_ChunkPrevious = from.m_ChunkPrevious;
            m_MaxCapacity = from.m_MaxCapacity;
        }

        // Private ctor — new middle chunk allocated by MakeRoom.
        private StringBuilder(int size, int maxCapacity, StringBuilder previousBlock)
        {
            m_ChunkChars = GC.AllocateUninitializedArray<char>(size);
            m_MaxCapacity = maxCapacity;
            m_ChunkPrevious = previousBlock;
            if (previousBlock != null)
                m_ChunkOffset = previousBlock.m_ChunkOffset + previousBlock.m_ChunkLength;
        }

        public int Capacity
        {
            get => m_ChunkChars.Length + m_ChunkOffset;
            set
            {
                if (value < 0) Halt();
                if (value > MaxCapacity) Halt();
                if (value < Length) Halt();

                if (Capacity != value)
                {
                    int newLen = value - m_ChunkOffset;
                    char[] newArray = GC.AllocateUninitializedArray<char>(newLen);
                    Array.Copy(m_ChunkChars, newArray, m_ChunkLength);
                    m_ChunkChars = newArray;
                }
            }
        }

        public int MaxCapacity => m_MaxCapacity;

        public int EnsureCapacity(int capacity)
        {
            if (capacity < 0) Halt();
            if (Capacity < capacity) Capacity = capacity;
            return Capacity;
        }

        public override string ToString()
        {
            if (Length == 0) return "";

            string result = string.FastAllocateString(Length);
            StringBuilder chunk = this;
            do
            {
                if (chunk.m_ChunkLength > 0)
                {
                    char[] sourceArray = chunk.m_ChunkChars;
                    int chunkOffset = chunk.m_ChunkOffset;
                    int chunkLength = chunk.m_ChunkLength;

                    if ((uint)(chunkLength + chunkOffset) > (uint)result.Length || (uint)chunkLength > (uint)sourceArray.Length)
                        Halt();

                    Buffer.Memmove(
                        ref Unsafe.Add(ref result.GetRawStringData(), chunkOffset),
                        ref MemoryMarshal.GetArrayDataReference(sourceArray),
                        (nuint)chunkLength);
                }
                chunk = chunk.m_ChunkPrevious;
            }
            while (chunk != null);

            return result;
        }

        public string ToString(int startIndex, int length)
        {
            int currentLength = Length;
            if (startIndex < 0) Halt();
            if (startIndex > currentLength) Halt();
            if (length < 0) Halt();
            if (startIndex > currentLength - length) Halt();

            string result = string.FastAllocateString(length);
            CopyTo(startIndex, new Span<char>(ref result.GetRawStringData(), result.Length), result.Length);
            return result;
        }

        public StringBuilder Clear()
        {
            Length = 0;
            return this;
        }

        public int Length
        {
            get => m_ChunkOffset + m_ChunkLength;
            set
            {
                if (value < 0) Halt();
                if (value > MaxCapacity) Halt();

                if (value == 0 && m_ChunkPrevious == null)
                {
                    m_ChunkLength = 0;
                    m_ChunkOffset = 0;
                    return;
                }

                int delta = value - Length;
                if (delta > 0)
                {
                    Append('\0', delta);
                }
                else
                {
                    StringBuilder chunk = FindChunkForIndex(value);
                    if (chunk != this)
                    {
                        // Avoid capacity explosion (see coreclr#16926).
                        int capacityToPreserve = Math.Min(Capacity, Math.Max(Length * 6 / 5, m_ChunkChars.Length));
                        int newLen = capacityToPreserve - chunk.m_ChunkOffset;
                        if (newLen > chunk.m_ChunkChars.Length)
                        {
                            char[] newArray = GC.AllocateUninitializedArray<char>(newLen);
                            Array.Copy(chunk.m_ChunkChars, newArray, chunk.m_ChunkLength);
                            m_ChunkChars = newArray;
                        }
                        else
                        {
                            m_ChunkChars = chunk.m_ChunkChars;
                        }

                        m_ChunkPrevious = chunk.m_ChunkPrevious;
                        m_ChunkOffset = chunk.m_ChunkOffset;
                    }
                    m_ChunkLength = value - chunk.m_ChunkOffset;
                }
            }
        }

        [IndexerName("Chars")]
        public char this[int index]
        {
            get
            {
                StringBuilder chunk = this;
                while (true)
                {
                    int indexInBlock = index - chunk.m_ChunkOffset;
                    if (indexInBlock >= 0)
                    {
                        if (indexInBlock >= chunk.m_ChunkLength) Halt();
                        return chunk.m_ChunkChars[indexInBlock];
                    }
                    chunk = chunk.m_ChunkPrevious;
                    if (chunk == null) Halt();
                }
            }
            set
            {
                StringBuilder chunk = this;
                while (true)
                {
                    int indexInBlock = index - chunk.m_ChunkOffset;
                    if (indexInBlock >= 0)
                    {
                        if (indexInBlock >= chunk.m_ChunkLength) Halt();
                        chunk.m_ChunkChars[indexInBlock] = value;
                        return;
                    }
                    chunk = chunk.m_ChunkPrevious;
                    if (chunk == null) Halt();
                }
            }
        }

        public StringBuilder Append(char value, int repeatCount)
        {
            if (repeatCount < 0) Halt();
            if (repeatCount == 0) return this;

            int newLength = Length + repeatCount;
            if (newLength > m_MaxCapacity || newLength < repeatCount) Halt();

            int index = m_ChunkLength;
            while (repeatCount > 0)
            {
                if (index < m_ChunkChars.Length)
                {
                    m_ChunkChars[index++] = value;
                    --repeatCount;
                }
                else
                {
                    m_ChunkLength = index;
                    ExpandByABlock(repeatCount);
                    index = 0;
                }
            }
            m_ChunkLength = index;
            return this;
        }

        public StringBuilder Append(char[] value, int startIndex, int charCount)
        {
            if (startIndex < 0) Halt();
            if (charCount < 0) Halt();

            if (value == null)
            {
                if (startIndex == 0 && charCount == 0) return this;
                Halt();
            }
            if (charCount > value.Length - startIndex) Halt();

            if (charCount != 0)
                Append(ref value[startIndex], charCount);
            return this;
        }

        public StringBuilder Append(string value)
        {
            if (value is not null)
                Append(ref value.GetRawStringData(), value.Length);
            return this;
        }

        public StringBuilder Append(string value, int startIndex, int count)
        {
            if (startIndex < 0) Halt();
            if (count < 0) Halt();

            if (value == null)
            {
                if (startIndex == 0 && count == 0) return this;
                Halt();
            }

            if (count != 0)
            {
                if (startIndex > value.Length - count) Halt();
                Append(ref Unsafe.Add(ref value.GetRawStringData(), startIndex), count);
            }
            return this;
        }

        public StringBuilder Append(StringBuilder value)
        {
            if (value != null && value.Length != 0)
                return AppendCore(value, 0, value.Length);
            return this;
        }

        public StringBuilder Append(StringBuilder value, int startIndex, int count)
        {
            if (startIndex < 0) Halt();
            if (count < 0) Halt();

            if (value == null)
            {
                if (startIndex == 0 && count == 0) return this;
                Halt();
            }
            if (count == 0) return this;
            if (count > value.Length - startIndex) Halt();

            return AppendCore(value, startIndex, count);
        }

        private StringBuilder AppendCore(StringBuilder value, int startIndex, int count)
        {
            if (value == this) return Append(value.ToString(startIndex, count));

            int newLength = Length + count;
            if ((uint)newLength > (uint)m_MaxCapacity) Halt();

            while (count > 0)
            {
                int length = Math.Min(m_ChunkChars.Length - m_ChunkLength, count);
                if (length == 0)
                {
                    ExpandByABlock(count);
                    length = Math.Min(m_ChunkChars.Length - m_ChunkLength, count);
                }
                value.CopyTo(startIndex, new Span<char>(m_ChunkChars, m_ChunkLength, length), length);

                m_ChunkLength += length;
                startIndex += length;
                count -= length;
            }
            return this;
        }

        public StringBuilder AppendLine() => Append(Environment.NewLine);

        public StringBuilder AppendLine(string value)
        {
            Append(value);
            return Append(Environment.NewLine);
        }

        public void CopyTo(int sourceIndex, char[] destination, int destinationIndex, int count)
        {
            if (destination == null) Halt();
            if (destinationIndex < 0) Halt();
            if (destinationIndex > destination.Length - count) Halt();

            CopyTo(sourceIndex, new Span<char>(destination).Slice(destinationIndex), count);
        }

        public void CopyTo(int sourceIndex, Span<char> destination, int count)
        {
            if (count < 0) Halt();
            if ((uint)sourceIndex > (uint)Length) Halt();
            if (sourceIndex > Length - count) Halt();

            StringBuilder chunk = this;
            int sourceEndIndex = sourceIndex + count;
            int curDestIndex = count;
            while (count > 0)
            {
                int chunkEndIndex = sourceEndIndex - chunk.m_ChunkOffset;
                if (chunkEndIndex >= 0)
                {
                    chunkEndIndex = Math.Min(chunkEndIndex, chunk.m_ChunkLength);

                    int chunkCount = count;
                    int chunkStartIndex = chunkEndIndex - count;
                    if (chunkStartIndex < 0)
                    {
                        chunkCount += chunkStartIndex;
                        chunkStartIndex = 0;
                    }
                    curDestIndex -= chunkCount;
                    count -= chunkCount;

                    new ReadOnlySpan<char>(chunk.m_ChunkChars, chunkStartIndex, chunkCount).CopyTo(destination.Slice(curDestIndex));
                }
                chunk = chunk.m_ChunkPrevious;
            }
        }

        // ---- Insert ----

        public StringBuilder Insert(int index, string value, int count)
            => Insert(index, value.AsSpan(), count);

        public StringBuilder Insert(int index, string value)
        {
            if ((uint)index > (uint)Length) Halt();
            if (value != null)
                Insert(index, ref value.GetRawStringData(), value.Length);
            return this;
        }

        public StringBuilder Insert(int index, char value)
        {
            if ((uint)index > (uint)Length) Halt();
            Insert(index, ref value, 1);
            return this;
        }

        public StringBuilder Insert(int index, char[] value)
        {
            if ((uint)index > (uint)Length) Halt();
            if (value != null)
                Insert(index, ref MemoryMarshal.GetArrayDataReference(value), value.Length);
            return this;
        }

        public StringBuilder Insert(int index, char[] value, int startIndex, int charCount)
        {
            int currentLength = Length;
            if ((uint)index > (uint)currentLength) Halt();

            if (value == null)
            {
                if (startIndex == 0 && charCount == 0) return this;
                Halt();
            }
            if (startIndex < 0) Halt();
            if (charCount < 0) Halt();
            if (startIndex > value.Length - charCount) Halt();

            if (charCount > 0)
                Insert(index, ref value[startIndex], charCount);
            return this;
        }

        public StringBuilder Insert(int index, ReadOnlySpan<char> value)
        {
            if ((uint)index > (uint)Length) Halt();
            if (value.Length != 0)
                Insert(index, ref MemoryMarshal.GetReference(value), value.Length);
            return this;
        }

        private StringBuilder Insert(int index, ReadOnlySpan<char> value, int count)
        {
            if (count < 0) Halt();

            int currentLength = Length;
            if ((uint)index > (uint)currentLength) Halt();

            if (value.IsEmpty || count == 0) return this;

            long insertingChars = (long)value.Length * count;
            if (insertingChars > MaxCapacity - Length) Halt();

            MakeRoom(index, (int)insertingChars, out StringBuilder chunk, out int indexInChunk, false);

            while (count > 0)
            {
                ReplaceInPlaceAtChunk(ref chunk, ref indexInChunk, ref MemoryMarshal.GetReference(value), value.Length);
                --count;
            }
            return this;
        }

        private void Insert(int index, ref char value, int valueCount)
        {
            if (valueCount > 0)
            {
                MakeRoom(index, valueCount, out StringBuilder chunk, out int indexInChunk, false);
                ReplaceInPlaceAtChunk(ref chunk, ref indexInChunk, ref value, valueCount);
            }
        }

        // ---- Remove ----

        public StringBuilder Remove(int startIndex, int length)
        {
            if (length < 0) Halt();
            if (startIndex < 0) Halt();
            if (length > Length - startIndex) Halt();

            if (Length == length && startIndex == 0)
            {
                Length = 0;
                return this;
            }

            if (length > 0)
                Remove(startIndex, length, out _, out _);
            return this;
        }

        private void Remove(int startIndex, int count, out StringBuilder chunk, out int indexInChunk)
        {
            int endIndex = startIndex + count;

            chunk = this;
            StringBuilder endChunk = null;
            int endIndexInChunk = 0;
            while (true)
            {
                if (endIndex - chunk.m_ChunkOffset >= 0)
                {
                    if (endChunk == null)
                    {
                        endChunk = chunk;
                        endIndexInChunk = endIndex - endChunk.m_ChunkOffset;
                    }
                    if (startIndex - chunk.m_ChunkOffset >= 0)
                    {
                        indexInChunk = startIndex - chunk.m_ChunkOffset;
                        break;
                    }
                }
                else
                {
                    chunk.m_ChunkOffset -= count;
                }
                chunk = chunk.m_ChunkPrevious;
            }

            int copyTargetIndexInChunk = indexInChunk;
            int copyCount = endChunk.m_ChunkLength - endIndexInChunk;
            if (endChunk != chunk)
            {
                copyTargetIndexInChunk = 0;
                chunk.m_ChunkLength = indexInChunk;

                endChunk.m_ChunkPrevious = chunk;
                endChunk.m_ChunkOffset = chunk.m_ChunkOffset + chunk.m_ChunkLength;

                if (indexInChunk == 0)
                {
                    endChunk.m_ChunkPrevious = chunk.m_ChunkPrevious;
                    chunk = endChunk;
                }
            }
            endChunk.m_ChunkLength -= (endIndexInChunk - copyTargetIndexInChunk);

            if (copyTargetIndexInChunk != endIndexInChunk)
            {
                new ReadOnlySpan<char>(endChunk.m_ChunkChars, endIndexInChunk, copyCount)
                    .CopyTo(endChunk.m_ChunkChars.AsSpan(copyTargetIndexInChunk));
            }
        }

        // ---- Append(primitive) — routed through NumberFormatting (managed
        // string allocation) instead of ISpanFormattable.TryFormat. One
        // heap alloc per call; BCL-compat API unchanged. ----

        public StringBuilder Append(bool value) => Append(value ? "True" : "False");

        public StringBuilder Append(char value)
        {
            int nextCharIndex = m_ChunkLength;
            char[] chars = m_ChunkChars;
            if ((uint)chars.Length > (uint)nextCharIndex)
            {
                chars[nextCharIndex] = value;
                m_ChunkLength++;
            }
            else
            {
                Append(value, 1);
            }
            return this;
        }

        [CLSCompliant(false)]
        public StringBuilder Append(sbyte value) => Append(NumberFormatting.IntToString(value));
        public StringBuilder Append(byte value) => Append(NumberFormatting.UIntToString(value));
        public StringBuilder Append(short value) => Append(NumberFormatting.IntToString(value));
        public StringBuilder Append(int value) => Append(NumberFormatting.IntToString(value));
        public StringBuilder Append(long value) => Append(NumberFormatting.LongToString(value));

        [CLSCompliant(false)]
        public StringBuilder Append(ushort value) => Append(NumberFormatting.UIntToString(value));
        [CLSCompliant(false)]
        public StringBuilder Append(uint value) => Append(NumberFormatting.UIntToString(value));
        [CLSCompliant(false)]
        public StringBuilder Append(ulong value) => Append(NumberFormatting.ULongToString(value));

        public StringBuilder Append(object value) => value == null ? this : Append(value.ToString());

        public StringBuilder Append(char[] value)
        {
            if (value is not null)
                Append(ref MemoryMarshal.GetArrayDataReference(value), value.Length);
            return this;
        }

        public StringBuilder Append(ReadOnlySpan<char> value)
        {
            Append(ref MemoryMarshal.GetReference(value), value.Length);
            return this;
        }

        [CLSCompliant(false)]
        public unsafe StringBuilder Append(char* value, int valueCount)
        {
            if (valueCount < 0) Halt();
            Append(ref *value, valueCount);
            return this;
        }

        // ---- Replace ----

        public StringBuilder Replace(char oldChar, char newChar) => Replace(oldChar, newChar, 0, Length);

        public StringBuilder Replace(char oldChar, char newChar, int startIndex, int count)
        {
            int currentLength = Length;
            if ((uint)startIndex > (uint)currentLength) Halt();
            if (count < 0 || startIndex > currentLength - count) Halt();

            int endIndex = startIndex + count;
            StringBuilder chunk = this;

            while (true)
            {
                int endIndexInChunk = endIndex - chunk.m_ChunkOffset;
                int startIndexInChunk = startIndex - chunk.m_ChunkOffset;
                if (endIndexInChunk >= 0)
                {
                    int curInChunk = Math.Max(startIndexInChunk, 0);
                    int endInChunk = Math.Min(chunk.m_ChunkLength, endIndexInChunk);
                    for (int i = curInChunk; i < endInChunk; i++)
                    {
                        if (chunk.m_ChunkChars[i] == oldChar)
                            chunk.m_ChunkChars[i] = newChar;
                    }
                }
                if (startIndexInChunk >= 0) break;
                chunk = chunk.m_ChunkPrevious;
            }
            return this;
        }

        // ---- Internal plumbing ----

        private Span<char> RemainingCurrentChunk
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => new Span<char>(m_ChunkChars, m_ChunkLength, m_ChunkChars.Length - m_ChunkLength);
        }

        private void Append(ref char value, int valueCount)
        {
            if (valueCount != 0)
            {
                char[] chunkChars = m_ChunkChars;
                int chunkLength = m_ChunkLength;

                if (((uint)chunkLength + (uint)valueCount) <= (uint)chunkChars.Length)
                {
                    ref char destination = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(chunkChars), chunkLength);
                    if (valueCount <= 2)
                    {
                        destination = value;
                        if (valueCount == 2)
                            Unsafe.Add(ref destination, 1) = Unsafe.Add(ref value, 1);
                    }
                    else
                    {
                        Buffer.Memmove(ref destination, ref value, (nuint)valueCount);
                    }
                    m_ChunkLength = chunkLength + valueCount;
                }
                else
                {
                    AppendWithExpansion(ref value, valueCount);
                }
            }
        }

        private void AppendWithExpansion(ref char value, int valueCount)
        {
            int newLength = Length + valueCount;
            if (newLength > m_MaxCapacity || newLength < valueCount) Halt();

            int firstLength = m_ChunkChars.Length - m_ChunkLength;
            if (firstLength > 0)
            {
                new ReadOnlySpan<char>(ref value, firstLength).CopyTo(m_ChunkChars.AsSpan(m_ChunkLength));
                m_ChunkLength = m_ChunkChars.Length;
            }

            int restLength = valueCount - firstLength;
            ExpandByABlock(restLength);

            new ReadOnlySpan<char>(ref Unsafe.Add(ref value, firstLength), restLength).CopyTo(m_ChunkChars);
            m_ChunkLength = restLength;
        }

        private void ReplaceInPlaceAtChunk(ref StringBuilder chunk, ref int indexInChunk, ref char value, int count)
        {
            if (count != 0)
            {
                while (true)
                {
                    int lengthInChunk = chunk.m_ChunkLength - indexInChunk;
                    int lengthToCopy = Math.Min(lengthInChunk, count);
                    new ReadOnlySpan<char>(ref value, lengthToCopy).CopyTo(chunk.m_ChunkChars.AsSpan(indexInChunk));

                    indexInChunk += lengthToCopy;
                    if (indexInChunk >= chunk.m_ChunkLength)
                    {
                        chunk = Next(chunk);
                        indexInChunk = 0;
                    }
                    count -= lengthToCopy;
                    if (count == 0) break;
                    value = ref Unsafe.Add(ref value, lengthToCopy);
                }
            }
        }

        private StringBuilder FindChunkForIndex(int index)
        {
            StringBuilder result = this;
            while (result.m_ChunkOffset > index)
                result = result.m_ChunkPrevious;
            return result;
        }

        private StringBuilder Next(StringBuilder chunk)
            => chunk == this ? null : FindChunkForIndex(chunk.m_ChunkOffset + chunk.m_ChunkLength);

        private void ExpandByABlock(int minBlockCharCount)
        {
            if ((minBlockCharCount + Length) > m_MaxCapacity || minBlockCharCount + Length < minBlockCharCount)
                Halt();

            int newBlockLength = Math.Max(minBlockCharCount, Math.Min(Length, MaxChunkSize));

            if (m_ChunkOffset + m_ChunkLength + newBlockLength < newBlockLength) Halt();

            char[] chunkChars = GC.AllocateUninitializedArray<char>(newBlockLength);

            m_ChunkPrevious = new StringBuilder(this);
            m_ChunkOffset += m_ChunkLength;
            m_ChunkLength = 0;
            m_ChunkChars = chunkChars;
        }

        private void MakeRoom(int index, int count, out StringBuilder chunk, out int indexInChunk, bool doNotMoveFollowingChars)
        {
            if (count + Length > m_MaxCapacity || count + Length < count) Halt();

            chunk = this;
            while (chunk.m_ChunkOffset > index)
            {
                chunk.m_ChunkOffset += count;
                chunk = chunk.m_ChunkPrevious;
            }
            indexInChunk = index - chunk.m_ChunkOffset;

            if (!doNotMoveFollowingChars && chunk.m_ChunkLength <= DefaultCapacity * 2 && chunk.m_ChunkChars.Length - chunk.m_ChunkLength >= count)
            {
                for (int i = chunk.m_ChunkLength; i > indexInChunk;)
                {
                    --i;
                    chunk.m_ChunkChars[i + count] = chunk.m_ChunkChars[i];
                }
                chunk.m_ChunkLength += count;
                return;
            }

            StringBuilder newChunk = new StringBuilder(Math.Max(count, DefaultCapacity), chunk.m_MaxCapacity, chunk.m_ChunkPrevious);
            newChunk.m_ChunkLength = count;

            int copyCount1 = Math.Min(count, indexInChunk);
            if (copyCount1 > 0)
            {
                new ReadOnlySpan<char>(chunk.m_ChunkChars, 0, copyCount1).CopyTo(newChunk.m_ChunkChars);

                int copyCount2 = indexInChunk - copyCount1;
                if (copyCount2 >= 0)
                {
                    new ReadOnlySpan<char>(chunk.m_ChunkChars, copyCount1, copyCount2).CopyTo(chunk.m_ChunkChars);
                    indexInChunk = copyCount2;
                }
            }

            chunk.m_ChunkPrevious = newChunk;
            chunk.m_ChunkOffset += count;
            if (copyCount1 < count)
            {
                chunk = newChunk;
                indexInChunk = copyCount1;
            }
        }

        private static void Halt() { while (true) ; }
    }
}
