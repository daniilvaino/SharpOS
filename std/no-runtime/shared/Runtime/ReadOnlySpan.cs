// System.ReadOnlySpan<T> — ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/ReadOnlySpan.cs
//
// Cuts mirror Span<T> — see that file's header for full list.

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace System
{
    [NonVersionable]
    public readonly ref struct ReadOnlySpan<T>
    {
        internal readonly ref T _reference;
        private readonly int _length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan(T[] array)
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
        public ReadOnlySpan(T[] array, int start, int length)
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
        public unsafe ReadOnlySpan(void* pointer, int length)
        {
            if (length < 0) SpanHelpers.Halt();
            _reference = ref Unsafe.As<byte, T>(ref *(byte*)pointer);
            _length = length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan(in T reference)
        {
            _reference = ref Unsafe.AsRef(in reference);
            _length = 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan(ref T reference, int length)
        {
            _reference = ref reference;
            _length = length;
        }

        public ref readonly T this[int index]
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

        public static bool operator !=(ReadOnlySpan<T> left, ReadOnlySpan<T> right) => !(left == right);

        public override bool Equals(object obj) => throw new NotSupportedException();
        public override int GetHashCode() => throw new NotSupportedException();

        public static implicit operator ReadOnlySpan<T>(T[] array) => new ReadOnlySpan<T>(array);

        public static ReadOnlySpan<T> Empty => default;

        public Enumerator GetEnumerator() => new Enumerator(this);

        public ref struct Enumerator
        {
            private readonly ReadOnlySpan<T> _span;
            private int _index;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal Enumerator(ReadOnlySpan<T> span)
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

            public ref readonly T Current
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get => ref _span[_index];
            }
        }

        public ref readonly T GetPinnableReference()
        {
            ref T ret = ref Unsafe.NullRef<T>();
            if (_length != 0) ret = ref _reference;
            return ref ret;
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

        public static bool operator ==(ReadOnlySpan<T> left, ReadOnlySpan<T> right) =>
            left._length == right._length &&
            Unsafe.AreSame<T>(ref left._reference, ref right._reference);

        public override string ToString() => null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> Slice(int start)
        {
            if ((uint)start > (uint)_length) SpanHelpers.Halt();
            return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), _length - start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ReadOnlySpan<T> Slice(int start, int length)
        {
            if ((uint)start > (uint)_length || (uint)length > (uint)(_length - start))
                SpanHelpers.Halt();
            return new ReadOnlySpan<T>(ref Unsafe.Add(ref _reference, (nint)(uint)start), length);
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
}
