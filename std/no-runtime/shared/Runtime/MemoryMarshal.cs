// System.Runtime.InteropServices.MemoryMarshal — subset ported from
// dotnet/runtime. BCL version is extensive (60+ methods); we expose
// only what BCL-verbatim StringBuilder / Split / Join / Concat calls:
//
//   GetArrayDataReference<T>(T[])
//   GetReference<T>(Span<T>)
//   GetReference<T>(ReadOnlySpan<T>)
//   CreateSpan<T>(ref T, int)
//   CreateReadOnlySpan<T>(ref T, int)
//
// Extend as new consumers show up.

using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    public static class MemoryMarshal
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetArrayDataReference<T>(T[] array)
            => ref SpanHelpers.GetArrayDataReference(array);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetReference<T>(Span<T> span) => ref span._reference;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T GetReference<T>(ReadOnlySpan<T> span) => ref span._reference;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Span<T> CreateSpan<T>(ref T reference, int length)
            => new Span<T>(ref reference, length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ReadOnlySpan<T> CreateReadOnlySpan<T>(ref T reference, int length)
            => new ReadOnlySpan<T>(ref reference, length);
    }
}
