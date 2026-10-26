// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Memory.cs
// Replaces the array-only Memory<T>/ReadOnlyMemory<T> std had before: the
// owner is now an object, so ReadOnlyMemory<char> over a string works
// (string.AsMemory()).
// Cuts (each marked "SharpOS cut:" / "SharpOS:"):
//   - MemoryManager<T> owners (ctors, GetSpan branch) — no MemoryManager here;
//   - Pin() — no GCHandle/MemoryHandle; the collector does not move objects;
//   - DebuggerTypeProxy(MemoryDebugView<>);
//   - typeof(T).IsValueType → default(T) is null (Type is identity only);
//   - ThrowHelper overloads taking ExceptionArgument → direct throws.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using EditorBrowsableAttribute = System.ComponentModel.EditorBrowsableAttribute;
using EditorBrowsableState = System.ComponentModel.EditorBrowsableState;

namespace System
{
    /// <summary>
    /// Memory represents a contiguous region of arbitrary memory similar to <see cref="Span{T}"/>.
    /// Unlike <see cref="Span{T}"/>, it is not a byref-like type.
    /// </summary>
    // SharpOS cut: [DebuggerTypeProxy(typeof(MemoryDebugView<>))] — no MemoryDebugView here
    [DebuggerDisplay("{ToString(),raw}")]
    public readonly struct Memory<T> : IEquatable<Memory<T>>
    {
        // NOTE: With the current implementation, Memory<T> and ReadOnlyMemory<T> must have the same layout,
        // as code uses Unsafe.As to cast between them.

        // The highest order bit of _index is used to discern whether _object is a pre-pinned array.
        // (_index < 0) => _object is a pre-pinned array, so Pin() will not allocate a new GCHandle
        //       (else) => Pin() needs to allocate a new GCHandle to pin the object.
        private readonly object? _object;
        private readonly int _index;
        private readonly int _length;

        /// <summary>
        /// Creates a new memory over the entirety of the target array.
        /// </summary>
        /// <param name="array">The target array.</param>
        /// <remarks>Returns default when <paramref name="array"/> is null.</remarks>
        /// <exception cref="System.ArrayTypeMismatchException">Thrown when <paramref name="array"/> is covariant and array's type is not exactly T[].</exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory(T[]? array)
        {
            if (array == null)
            {
                this = default;
                return; // returns default
            }
            if (default(T) is null && array.GetType() != typeof(T[])) // SharpOS cut: typeof(T).IsValueType — our Type is identity only
                throw new ArrayTypeMismatchException(); // SharpOS: was ThrowHelper.ThrowArrayTypeMismatchException()

            _object = array;
            _index = 0;
            _length = array.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Memory(T[]? array, int start)
        {
            if (array == null)
            {
                if (start != 0)
                    ThrowHelper.ThrowArgumentOutOfRangeException();
                this = default;
                return; // returns default
            }
            if (default(T) is null && array.GetType() != typeof(T[])) // SharpOS cut: typeof(T).IsValueType — our Type is identity only
                throw new ArrayTypeMismatchException(); // SharpOS: was ThrowHelper.ThrowArrayTypeMismatchException()
            if ((uint)start > (uint)array.Length)
                ThrowHelper.ThrowArgumentOutOfRangeException();

            _object = array;
            _index = start;
            _length = array.Length - start;
        }

        /// <summary>
        /// Creates a new memory over the portion of the target array beginning
        /// at 'start' index and ending at 'end' index (exclusive).
        /// </summary>
        /// <param name="array">The target array.</param>
        /// <param name="start">The index at which to begin the memory.</param>
        /// <param name="length">The number of items in the memory.</param>
        /// <remarks>Returns default when <paramref name="array"/> is null.</remarks>
        /// <exception cref="System.ArrayTypeMismatchException">Thrown when <paramref name="array"/> is covariant and array's type is not exactly T[].</exception>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="start"/> or end index is not in the range (&lt;0 or &gt;Length).
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory(T[]? array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0)
                    ThrowHelper.ThrowArgumentOutOfRangeException();
                this = default;
                return; // returns default
            }
            if (default(T) is null && array.GetType() != typeof(T[])) // SharpOS cut: typeof(T).IsValueType — our Type is identity only
                throw new ArrayTypeMismatchException(); // SharpOS: was ThrowHelper.ThrowArrayTypeMismatchException()
#if TARGET_64BIT
            // See comment in Span<T>.Slice for how this works.
            if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)array.Length)
                ThrowHelper.ThrowArgumentOutOfRangeException();
#else
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                ThrowHelper.ThrowArgumentOutOfRangeException();
#endif

            _object = array;
            _index = start;
            _length = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Memory(object? obj, int start, int length)
        {
            // No validation performed in release builds; caller must provide any necessary validation.

            // 'obj is T[]' below also handles things like int[] <-> uint[] being convertible
            Debug.Assert((obj == null)
                || (typeof(T) == typeof(char) && obj is string)
                || (obj is T[])); // SharpOS cut: || (obj is MemoryManager<T>)

            _object = obj;
            _index = start;
            _length = length;
        }

        /// <summary>
        /// Defines an implicit conversion of an array to a <see cref="Memory{T}"/>
        /// </summary>
        public static implicit operator Memory<T>(T[]? array) => new Memory<T>(array);

        /// <summary>
        /// Defines an implicit conversion of a <see cref="ArraySegment{T}"/> to a <see cref="Memory{T}"/>
        /// </summary>
        public static implicit operator Memory<T>(ArraySegment<T> segment) => new Memory<T>(segment.Array, segment.Offset, segment.Count);

        /// <summary>
        /// Defines an implicit conversion of a <see cref="Memory{T}"/> to a <see cref="ReadOnlyMemory{T}"/>
        /// </summary>
        public static implicit operator ReadOnlyMemory<T>(Memory<T> memory) =>
            Unsafe.As<Memory<T>, ReadOnlyMemory<T>>(ref memory);

        /// <summary>
        /// Returns an empty <see cref="Memory{T}"/>
        /// </summary>
        public static Memory<T> Empty => default;

        /// <summary>
        /// The number of items in the memory.
        /// </summary>
        public int Length => _length;

        /// <summary>
        /// Returns true if Length is 0.
        /// </summary>
        public bool IsEmpty => _length == 0;

        /// <summary>
        /// For <see cref="Memory{Char}"/>, returns a new instance of string that represents the characters pointed to by the memory.
        /// Otherwise, returns a <see cref="string"/> with the name of the type and the number of elements.
        /// </summary>
        public override string ToString()
        {
            if (typeof(T) == typeof(char))
            {
                return (_object is string str) ? str.Substring(_index, _length) : Span.ToString();
            }
            return $"System.Memory<{typeof(T).Name}>[{_length}]";
        }

        /// <summary>
        /// Forms a slice out of the given memory, beginning at 'start'.
        /// </summary>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="start"/> index is not in range (&lt;0 or &gt;Length).
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory<T> Slice(int start)
        {
            if ((uint)start > (uint)_length)
            {
                throw new ArgumentOutOfRangeException("start"); // SharpOS: was ThrowHelper.ThrowArgumentOutOfRangeException(ExceptionArgument.start)
            }

            // It is expected for _index + start to be negative if the memory is already pre-pinned.
            return new Memory<T>(_object, _index + start, _length - start);
        }

        /// <summary>
        /// Forms a slice out of the given memory, beginning at 'start', of given length
        /// </summary>
        /// <param name="start">The index at which to begin this slice.</param>
        /// <param name="length">The desired length for the slice (exclusive).</param>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// Thrown when the specified <paramref name="start"/> or end index is not in range (&lt;0 or &gt;Length).
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Memory<T> Slice(int start, int length)
        {
#if TARGET_64BIT
            // See comment in Span<T>.Slice for how this works.
            if ((ulong)(uint)start + (ulong)(uint)length > (ulong)(uint)_length)
                ThrowHelper.ThrowArgumentOutOfRangeException();
#else
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                ThrowHelper.ThrowArgumentOutOfRangeException();
#endif

            // It is expected for _index + start to be negative if the memory is already pre-pinned.
            return new Memory<T>(_object, _index + start, length);
        }

        /// <summary>
        /// Returns a span from the memory.
        /// </summary>
        public unsafe Span<T> Span
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // This property getter has special support for returning a mutable Span<char> that wraps
                // an immutable String instance. This is obviously a dangerous feature and breaks type safety.
                // However, we need to handle the case where a ReadOnlyMemory<char> was created from a string
                // and then cast to a Memory<T>. Such a cast can only be done with unsafe or marshaling code,
                // in which case that's the dangerous operation performed by the dev, and we're just following
                // suit here to make it work as best as possible.

                ref T refToReturn = ref Unsafe.NullRef<T>();
                int lengthOfUnderlyingSpan = 0;

                // Copy this field into a local so that it can't change out from under us mid-operation.

                object? tmpObject = _object;
                if (tmpObject != null)
                {
                    if (typeof(T) == typeof(char) && tmpObject.GetType() == typeof(string))
                    {
                        // Special-case string since it's the most common for ROM<char>.

                        refToReturn = ref Unsafe.As<char, T>(ref Unsafe.As<string>(tmpObject).GetRawStringData());
                        lengthOfUnderlyingSpan = Unsafe.As<string>(tmpObject).Length;
                    }
                    else // SharpOS cut: RuntimeHelpers.ObjectHasComponentSize — without MemoryManager<T> the only other owner is T[]
                    {
                        // We know the object is not null, it's not a string, and it is variable-length. The only
                        // remaining option is for it to be a T[] (or a U[] which is blittable to T[], like int[]
                        // and uint[]). As a special case of this, ROM<T> allows some amount of array variance
                        // that Memory<T> disallows. For example, an array of actual type string[] cannot be turned
                        // into a Memory<object> or a Span<object>, but it can be turned into a ROM/ROS<object>.
                        // We'll assume these checks succeeded because they're performed during Memory<T> construction.
                        // It's always possible for somebody to use private reflection to bypass these checks, but
                        // preventing type safety violations due to misuse of reflection is out of scope of this logic.

                        // 'tmpObject is T[]' below also handles things like int[] <-> uint[] being convertible
                        Debug.Assert(tmpObject is T[]);

                        refToReturn = ref MemoryMarshal.GetArrayDataReference(Unsafe.As<T[]>(tmpObject));
                        lengthOfUnderlyingSpan = Unsafe.As<T[]>(tmpObject).Length;
                    }
                    // SharpOS cut: the MemoryManager<T> branch (GetSpan)

                    // If the Memory<T> or ReadOnlyMemory<T> instance is torn, this property getter has undefined behavior.
                    // We try to detect this condition and throw an exception, but it's possible that a torn struct might
                    // appear to us to be valid, and we'll return an undesired span. Such a span is always guaranteed at
                    // least to be in-bounds when compared with the original Memory<T> instance, so using the span won't
                    // AV the process.

                    nuint desiredStartIndex = (uint)_index & (uint)ReadOnlyMemory<T>.RemoveFlagsBitMask;
                    int desiredLength = _length;

#if TARGET_64BIT
                    // See comment in Span<T>.Slice for how this works.
                    if ((ulong)desiredStartIndex + (ulong)(uint)desiredLength > (ulong)(uint)lengthOfUnderlyingSpan)
                    {
                        ThrowHelper.ThrowArgumentOutOfRangeException();
                    }
#else
                    if ((uint)desiredStartIndex > (uint)lengthOfUnderlyingSpan || (uint)desiredLength > (uint)lengthOfUnderlyingSpan - (uint)desiredStartIndex)
                    {
                        ThrowHelper.ThrowArgumentOutOfRangeException();
                    }
#endif

                    refToReturn = ref Unsafe.Add(ref refToReturn, (IntPtr)(void*)desiredStartIndex);
                    lengthOfUnderlyingSpan = desiredLength;
                }

                return new Span<T>(ref refToReturn, lengthOfUnderlyingSpan);
            }
        }

        /// <summary>
        /// Copies the contents of the memory into the destination. If the source
        /// and destination overlap, this method behaves as if the original values are in
        /// a temporary location before the destination is overwritten.
        ///
        /// <param name="destination">The Memory to copy items into.</param>
        /// <exception cref="System.ArgumentException">
        /// Thrown when the destination is shorter than the source.
        /// </exception>
        /// </summary>
        public void CopyTo(Memory<T> destination) => Span.CopyTo(destination.Span);

        /// <summary>
        /// Copies the contents of the memory into the destination. If the source
        /// and destination overlap, this method behaves as if the original values are in
        /// a temporary location before the destination is overwritten.
        ///
        /// <returns>If the destination is shorter than the source, this method
        /// return false and no data is written to the destination.</returns>
        /// </summary>
        /// <param name="destination">The span to copy items into.</param>
        public bool TryCopyTo(Memory<T> destination) => Span.TryCopyTo(destination.Span);

        // SharpOS cut: Pin() — there is no GCHandle/MemoryHandle here (and the collector does not move objects).

        /// <summary>
        /// Copies the contents from the memory into a new array.  This heap
        /// allocates, so should generally be avoided, however it is sometimes
        /// necessary to bridge the gap with APIs written in terms of arrays.
        /// </summary>
        public T[] ToArray() => Span.ToArray();

        /// <summary>
        /// Determines whether the specified object is equal to the current object.
        /// Returns true if the object is Memory or ReadOnlyMemory and if both objects point to the same array and have the same length.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override bool Equals([NotNullWhen(true)] object? obj)
        {
            if (obj is ReadOnlyMemory<T>)
            {
                return ((ReadOnlyMemory<T>)obj).Equals(this);
            }
            else if (obj is Memory<T> memory)
            {
                return Equals(memory);
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// Returns true if the memory points to the same array and has the same length.  Note that
        /// this does *not* check to see if the *contents* are equal.
        /// </summary>
        public bool Equals(Memory<T> other)
        {
            return
                _object == other._object &&
                _index == other._index &&
                _length == other._length;
        }

        /// <summary>
        /// Serves as the default hash function.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public override int GetHashCode()
        {
            // We use RuntimeHelpers.GetHashCode instead of Object.GetHashCode because the hash
            // code is based on object identity and referential equality, not deep equality (as common with string).
            return (_object != null) ? HashCode.Combine(RuntimeHelpers.GetHashCode(_object), _index, _length) : 0;
        }
    }
}
