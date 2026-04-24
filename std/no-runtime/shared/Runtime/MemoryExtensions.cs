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
