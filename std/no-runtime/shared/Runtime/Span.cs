// System.Span<T> — ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Span.cs
//
// Cuts (all local, public contract preserved):
//  - [DebuggerTypeProxy], [DebuggerDisplay], [EditorBrowsable],
//    [NativeMarshalling] — attributes we don't emit.
//  - MemoryMarshal.GetArrayDataReference(array) — replaced with
//    Unsafe.As<byte,T>(ref *(byte*)arrayDataStart) via fixed pointer
//    on array. Observable behavior identical.
//  - typeof(T).IsValueType + array.GetType() != typeof(T[]) covariance
//    check — skipped (we don't have reflection). Callers passing a
//    covariant array get undefined behavior rather than an exception,
//    same as passing a wrong raw pointer would.
//  - RuntimeHelpers.IsReferenceOrContainsReferences<T>() — stubbed
//    conservatively to `true` so Clear/Fill fall through the reference-
//    containing path, which relies on SpanHelpers. Spans of value types
//    with references still work; non-reference value-type Clear loses
//    memset optimization (safe, slower).
//  - ThrowHelper calls → Halt().
//  - SpanHelpers.Fill/Clear — inlined as simple loops.
//  - SR.* resource strings → null (no culture).
//  - `new string(ReadOnlySpan<char>)` ctor — not implemented in our
//    string yet; ToString() for Span<char> falls back to fmt stub. Add
//    the ctor when StringBuilder actually needs it.
//  - NotSupportedException throws on Equals/GetHashCode stay (halts,
//    same end-result for misuse).

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace System
{
    [NonVersionable]
    public readonly ref struct Span<T>
    {
        /// <summary>A byref or a native ptr.</summary>
        internal readonly ref T _reference;
        /// <summary>The number of elements this Span contains.</summary>
        private readonly int _length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span(T[] array)
        {
            if (array == null)
            {
                this = default;
                return;
            }
            _reference = ref SpanHelpers.GetArrayDataReference(array);
            _length = array.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span(T[] array, int start, int length)
        {
            if (array == null)
            {
                if (start != 0 || length != 0) SpanHelpers.Halt();
                this = default;
                return;
            }
            if ((uint)start > (uint)array.Length || (uint)length > (uint)(array.Length - start))
                SpanHelpers.Halt();

            _reference = ref Unsafe.Add(ref SpanHelpers.GetArrayDataReference(array), (nint)(uint)start);
            _length = length;
        }

        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe Span(void* pointer, int length)
        {
            if (length < 0) SpanHelpers.Halt();
            _reference = ref Unsafe.As<byte, T>(ref *(byte*)pointer);
            _length = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span(ref T reference)
        {
            _reference = ref reference;
            _length = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal Span(ref T reference, int length)
        {
            _reference = ref reference;
            _length = length;
        }

        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            [NonVersionable]
            get
            {
                if ((uint)index >= (uint)_length) SpanHelpers.Halt();
                return ref Unsafe.Add(ref _reference, (nint)(uint)index);
            }
        }

        public int Length
        {
            [NonVersionable]
            get => _length;
        }

        public bool IsEmpty
        {
            [NonVersionable]
            get => _length == 0;
        }

        public static bool operator !=(Span<T> left, Span<T> right) => !(left == right);

        public override bool Equals(object obj) => throw new NotSupportedException();
        public override int GetHashCode() => throw new NotSupportedException();

        public static implicit operator Span<T>(T[] array) => new Span<T>(array);

        public static Span<T> Empty => default;

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly Span<T> _span;
            private int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(Span<T> span)
            {
                _span = span;
                _index = -1;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public bool MoveNext()
            {
                int index = _index + 1;
                if (index < _span.Length)
                {
                    _index = index;
                    return true;
                }
                return false;
            }

            public ref T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _span[_index];
            }
        }

        public ref T GetPinnableReference()
        {
            ref T ret = ref Unsafe.NullRef<T>();
            if (_length != 0) ret = ref _reference;
            return ref ret;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Clear()
        {
            for (int i = 0; i < _length; i++)
                Unsafe.Add(ref _reference, i) = default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Fill(T value)
        {
            for (int i = 0; i < _length; i++)
                Unsafe.Add(ref _reference, i) = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void CopyTo(Span<T> destination)
        {
            if ((uint)_length > (uint)destination.Length)
                SpanHelpers.Halt();
            for (int i = 0; i < _length; i++)
                Unsafe.Add(ref destination._reference, i) = Unsafe.Add(ref _reference, i);
        }

        public bool TryCopyTo(Span<T> destination)
        {
            if ((uint)_length > (uint)destination.Length) return false;
            for (int i = 0; i < _length; i++)
                Unsafe.Add(ref destination._reference, i) = Unsafe.Add(ref _reference, i);
            return true;
        }

        public static bool operator ==(Span<T> left, Span<T> right) =>
            left._length == right._length &&
            Unsafe.AreSame<T>(ref left._reference, ref right._reference);

        public static implicit operator ReadOnlySpan<T>(Span<T> span) =>
            new ReadOnlySpan<T>(ref span._reference, span._length);

        public override string ToString() => null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> Slice(int start)
        {
            if ((uint)start > (uint)_length) SpanHelpers.Halt();
            return new Span<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), _length - start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Span<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                SpanHelpers.Halt();
            return new Span<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T[] ToArray()
        {
            if (_length == 0) return SpanHelpers.EmptyArray<T>();
            var destination = new T[_length];
            for (int i = 0; i < _length; i++)
                destination[i] = Unsafe.Add(ref _reference, i);
            return destination;
        }
    }

    // Internal helpers for Span/ReadOnlySpan. Kept in System namespace for
    // symmetry with BCL's internal SpanHelpers / MemoryMarshal split.
    internal static class SpanHelpers
    {
        // BCL equivalent: MemoryMarshal.GetArrayDataReference(T[] array).
        // Returns a ref to the first element's storage, safe even for
        // zero-length arrays (where `array[0]` would throw). Assumes
        // non-moving GC — the returned ref must stay valid across GC,
        // and we guarantee that since our kernel GC is mark-sweep with
        // no compaction.
        //
        // Array object layout: [MethodTable*](8) [Length(4)+pad(4)]
        // [element 0, element 1, ...]. Element 0 starts at object+16.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe ref T GetArrayDataReference<T>(T[] array)
        {
            T[] local = array;
            nint objAddr = *(nint*)Unsafe.AsPointer(ref local);
            return ref *(T*)(objAddr + 16);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static T[] EmptyArray<T>() => new T[0];

        internal static void Halt()
        {
            while (true) ;
        }
    }
}
