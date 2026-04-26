// System.MemoryExtensions — subset ported from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/MemoryExtensions.cs
//
// BCL version is 3200+ lines with IndexOf / SequenceEqual / SearchValues /
// Vector-backed search — all SIMD-optimised, all depending on SpanHelpers
// primitives we'd rather not lift right now. This file covers only what
// StringBuilder + Split/Join/Concat actually call:
//
//   AsSpan(this string)
//   AsSpan(this string, int start)
//   AsSpan(this string, int start, int length)
//   AsSpan<T>(this T[] array)
//   AsSpan<T>(this T[] array, int start)
//   AsSpan<T>(this T[] array, int start, int length)
//   AsSpan(this Span<T>) / AsSpan(this ReadOnlySpan<T>) (no-ops)
//   CopyTo — already on Span<T> / ReadOnlySpan<T>
//
// Add more overloads when a consumer needs them.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System
{
    public static class MemoryExtensions
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<char> AsSpan(this string text)
        {
            if (text == null) return default;
            return new ReadOnlySpan<char>(ref StringHelpers.GetFirstCharRef(text), text.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<char> AsSpan(this string text, int start)
        {
            if (text == null)
            {
                if (start != 0) SpanHelpers.Halt();
                return default;
            }
            if ((uint)start > (uint)text.Length) SpanHelpers.Halt();
            return new ReadOnlySpan<char>(
                ref Unsafe.Add(ref StringHelpers.GetFirstCharRef(text), (nint)(uint)start),
                text.Length - start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<char> AsSpan(this string text, int start, int length)
        {
            if (text == null)
            {
                if (start != 0 || length != 0) SpanHelpers.Halt();
                return default;
            }
            if ((uint)start > (uint)text.Length || (uint)length > (uint)(text.Length - start))
                SpanHelpers.Halt();
            return new ReadOnlySpan<char>(
                ref Unsafe.Add(ref StringHelpers.GetFirstCharRef(text), (nint)(uint)start),
                length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> AsSpan<T>(this T[] array)
        {
            return new Span<T>(array);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> AsSpan<T>(this T[] array, int start)
        {
            if (array == null)
            {
                if (start != 0) SpanHelpers.Halt();
                return default;
            }
            if ((uint)start > (uint)array.Length) SpanHelpers.Halt();
            return new Span<T>(
                ref Unsafe.Add(ref SpanHelpers.GetArrayDataReference(array), (nint)(uint)start),
                array.Length - start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> AsSpan<T>(this T[] array, int start, int length)
        {
            return new Span<T>(array, start, length);
        }

        // ---- IndexOf / LastIndexOf / Contains ----
        // BCL uses SIMD-vectorized SpanHelpers; we use scalar loops backed
        // by IEquatable<T> interface dispatch (works through our shared-
        // generic resolver since step 32). For T without IEquatable<T>,
        // reference equality fallback.

        public static int IndexOf<T>(this Span<T> span, T value) where T : IEquatable<T>
        {
            return IndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value);
        }

        public static int IndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
        {
            return IndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value);
        }

        public static int LastIndexOf<T>(this Span<T> span, T value) where T : IEquatable<T>
        {
            return LastIndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value);
        }

        public static int LastIndexOf<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
        {
            return LastIndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value);
        }

        public static bool Contains<T>(this Span<T> span, T value) where T : IEquatable<T>
            => IndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value) >= 0;

        public static bool Contains<T>(this ReadOnlySpan<T> span, T value) where T : IEquatable<T>
            => IndexOfCore(ref MemoryMarshal.GetReference(span), span.Length, value) >= 0;

        private static int IndexOfCore<T>(ref T head, int length, T value) where T : IEquatable<T>
        {
            for (int i = 0; i < length; i++)
            {
                if (value.Equals(Unsafe.Add(ref head, i))) return i;
            }
            return -1;
        }

        private static int LastIndexOfCore<T>(ref T head, int length, T value) where T : IEquatable<T>
        {
            for (int i = length - 1; i >= 0; i--)
            {
                if (value.Equals(Unsafe.Add(ref head, i))) return i;
            }
            return -1;
        }

        // ---- SequenceEqual ----

        public static bool SequenceEqual<T>(this Span<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>
            => SequenceEqualCore(ref MemoryMarshal.GetReference(span), span.Length, ref MemoryMarshal.GetReference(other), other.Length);

        public static bool SequenceEqual<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> other) where T : IEquatable<T>
            => SequenceEqualCore(ref MemoryMarshal.GetReference(span), span.Length, ref MemoryMarshal.GetReference(other), other.Length);

        private static bool SequenceEqualCore<T>(ref T headA, int lengthA, ref T headB, int lengthB) where T : IEquatable<T>
        {
            if (lengthA != lengthB) return false;
            for (int i = 0; i < lengthA; i++)
            {
                T a = Unsafe.Add(ref headA, i);
                T b = Unsafe.Add(ref headB, i);
                if (!a.Equals(b)) return false;
            }
            return true;
        }

        // ---- Reverse ----

        public static void Reverse<T>(this Span<T> span)
        {
            int i = 0;
            int j = span.Length - 1;
            while (i < j)
            {
                T tmp = span[i];
                span[i] = span[j];
                span[j] = tmp;
                i++; j--;
            }
        }

        // ---- Fill ----

        public static void Fill<T>(this Span<T> span, T value)
        {
            for (int i = 0; i < span.Length; i++) span[i] = value;
        }

        // ---- Clear ----

        public static void Clear<T>(this Span<T> span)
        {
            for (int i = 0; i < span.Length; i++) span[i] = default;
        }

        // ---- StartsWith / EndsWith ----

        public static bool StartsWith<T>(this Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
            => value.Length <= span.Length
               && SequenceEqualCore(ref MemoryMarshal.GetReference(span), value.Length, ref MemoryMarshal.GetReference(value), value.Length);

        public static bool StartsWith<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
            => value.Length <= span.Length
               && SequenceEqualCore(ref MemoryMarshal.GetReference(span), value.Length, ref MemoryMarshal.GetReference(value), value.Length);

        public static bool EndsWith<T>(this Span<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
        {
            int extra = span.Length - value.Length;
            if (extra < 0) return false;
            return SequenceEqualCore(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(span), extra), value.Length,
                ref MemoryMarshal.GetReference(value), value.Length);
        }

        public static bool EndsWith<T>(this ReadOnlySpan<T> span, ReadOnlySpan<T> value) where T : IEquatable<T>
        {
            int extra = span.Length - value.Length;
            if (extra < 0) return false;
            return SequenceEqualCore(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(span), extra), value.Length,
                ref MemoryMarshal.GetReference(value), value.Length);
        }
    }

    // Helpers specific to string storage. Kept separate from SpanHelpers
    // so that Span.cs has no circular dep on our string layout.
    internal static class StringHelpers
    {
        // String object layout (NativeAOT / our MinimalRuntime.String):
        //   [MethodTable*](8) [Length(4)] [first char(2)] ...
        // First char at offset 12. Matches BCL's
        // `RuntimeHelpers.OffsetToStringData` via
        // `sizeof(IntPtr) + sizeof(int)` = 12.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static unsafe ref char GetFirstCharRef(string s)
        {
            string local = s;
            nint objAddr = *(nint*)Unsafe.AsPointer(ref local);
            return ref *(char*)(objAddr + 12);
        }
    }
}
