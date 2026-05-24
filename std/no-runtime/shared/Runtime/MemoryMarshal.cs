// System.Runtime.InteropServices.MemoryMarshal — subset ported from
// dotnet/runtime. BCL version is extensive (60+ methods); we expose
// only what BCL-verbatim StringBuilder / Split / Join / Concat calls:
//
//   GetArrayDataReference<T>(T[])
//   GetReference<T>(Span<T>)
//   GetReference<T>(ReadOnlySpan<T>)
//   CreateSpan<T>(ref T, int)
//   CreateReadOnlySpan<T>(ref T, int)
//   Read<T>(ReadOnlySpan<byte>)        ← used by BinaryPrimitives
//   Cast<TFrom, TTo>(ReadOnlySpan<TFrom>) / (Span<TFrom>)  ← bulk reverse helpers
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

        // Reads a structure of type T out of a read-only span of bytes.
        // Throws if the span is too small. T must be a value type without
        // managed references (BCL enforces via [Unmanaged]); we trust the
        // caller in kernel-tier (Halt on undersize is fine).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe T Read<T>(ReadOnlySpan<byte> source) where T : unmanaged
        {
            if (source.Length < sizeof(T))
                Halt();
            return Unsafe.ReadUnaligned<T>(ref GetReference(source));
        }

        // Try-pattern equivalent: returns false on short span instead of halt.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool TryRead<T>(ReadOnlySpan<byte> source, out T value) where T : unmanaged
        {
            if (source.Length < sizeof(T))
            {
                value = default;
                return false;
            }
            value = Unsafe.ReadUnaligned<T>(ref GetReference(source));
            return true;
        }

        // Writes a structure of type T into the start of a span of bytes.
        // Halt on undersize. Companion to Read above.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void Write<T>(Span<byte> destination, T value) where T : unmanaged
        {
            if (destination.Length < sizeof(T))
                Halt();
            Unsafe.WriteUnaligned<T>(ref GetReference(destination), value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe bool TryWrite<T>(Span<byte> destination, T value) where T : unmanaged
        {
            if (destination.Length < sizeof(T))
                return false;
            Unsafe.WriteUnaligned<T>(ref GetReference(destination), value);
            return true;
        }

        // Bulk-reinterpret a span of TFrom as a span of TTo. Length scales
        // by sizeof(TFrom)/sizeof(TTo). Only safe for unmanaged element
        // types (no managed refs being misaligned across element boundary).
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Span<TTo> Cast<TFrom, TTo>(Span<TFrom> span)
            where TFrom : unmanaged
            where TTo : unmanaged
        {
            int newLen = checked((int)((long)span.Length * sizeof(TFrom) / sizeof(TTo)));
            return new Span<TTo>(Unsafe.AsPointer(ref Unsafe.As<TFrom, TTo>(ref span._reference)), newLen);
        }

        // Same pattern as the rest of std (e.g. ArraySegment.Halt): bound-
        // check failure halts the kernel because there's no exception
        // engine reachable here without re-entering CoreCLR or NativeAOT
        // ThrowHelpers we don't surface in this assembly. Callers that
        // need a recoverable signal use TryRead/TryWrite.
        private static void Halt() { while (true) ; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe ReadOnlySpan<TTo> Cast<TFrom, TTo>(ReadOnlySpan<TFrom> span)
            where TFrom : unmanaged
            where TTo : unmanaged
        {
            int newLen = checked((int)((long)span.Length * sizeof(TFrom) / sizeof(TTo)));
            return new ReadOnlySpan<TTo>(Unsafe.AsPointer(ref Unsafe.As<TFrom, TTo>(ref Unsafe.AsRef(in span._reference))), newLen);
        }
    }
}
