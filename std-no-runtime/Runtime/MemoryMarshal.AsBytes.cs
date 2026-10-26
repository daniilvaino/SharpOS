// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/MemoryMarshal.cs
//   (AsBytes<T>(Span<T>), AsBytes<T>(ReadOnlySpan<T>))
// Cut: the runtime check that T holds no references — there is no
// RuntimeHelpers.IsReferenceOrContainsReferences<T> here (marked "SharpOS:").

using System.Runtime.CompilerServices;

namespace System.Runtime.InteropServices
{
    public static partial class MemoryMarshal
    {
        /// <summary>
        /// Casts a Span of one primitive type <typeparamref name="T"/> to Span of bytes.
        /// That type may not contain pointers or references.
        /// </summary>
        public static Span<byte> AsBytes<T>(Span<T> span)
            where T : struct
        {
            // SharpOS: no IsReferenceOrContainsReferences<T>() check.
            return new Span<byte>(
                ref Unsafe.As<T, byte>(ref GetReference(span)),
                checked(span.Length * Unsafe.SizeOf<T>()));
        }

        /// <summary>
        /// Casts a ReadOnlySpan of one primitive type <typeparamref name="T"/> to ReadOnlySpan of bytes.
        /// That type may not contain pointers or references.
        /// </summary>
        public static ReadOnlySpan<byte> AsBytes<T>(ReadOnlySpan<T> span)
            where T : struct
        {
            // SharpOS: no IsReferenceOrContainsReferences<T>() check.
            return new ReadOnlySpan<byte>(
                ref Unsafe.As<T, byte>(ref GetReference(span)),
                checked(span.Length * Unsafe.SizeOf<T>()));
        }
    }
}
