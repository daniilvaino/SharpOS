// Runtime exception ID enum — used by GetRuntimeException helper.
//
// Ported from
//   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionIDs.cs
// verbatim. Values must match exactly — the runtime passes raw enum
// values and expects classlib's GetRuntimeException to return concrete
// exception instances of the matching type.

namespace System.Runtime
{
    public enum ExceptionIDs
    {
        OutOfMemory = 1,
        Arithmetic = 2,
        ArrayTypeMismatch = 3,
        DivideByZero = 4,
        IndexOutOfRange = 5,
        InvalidCast = 6,
        Overflow = 7,
        NullReference = 8,
        AccessViolation = 9,
        DataMisaligned = 10,
        EntrypointNotFound = 11,
        AmbiguousImplementation = 12,
    }
}
