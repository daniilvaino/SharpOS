// Verbatim port from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/ByReference.cs
//
// ByReference is an internal ref-struct wrapper that holds a `ref byte`.
// Span<T> / ReadOnlySpan<T> use it to carry a managed reference to
// storage around without exposing ref semantics to callers.
//
// Requires System.Runtime.CompilerServices.Unsafe (for As<T, byte>).

using System.Runtime.CompilerServices;
using System.Runtime.Versioning;

namespace System
{
    [NonVersionable]
    internal readonly ref struct ByReference
    {
        public readonly ref byte Value;
        public ByReference(ref byte value) => Value = ref value;

        public static ByReference Create<T>(ref T p) => new ByReference(ref Unsafe.As<T, byte>(ref p));
    }
}
