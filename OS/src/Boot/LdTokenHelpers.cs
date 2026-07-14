using System;

namespace Internal.Runtime.CompilerHelpers
{
    // ILC's `ldtoken` lowering (RuntimeType/Method/Field handle
    // materialization) resolves these three well-known methods by
    // fully-qualified name in the system module. Emitted for e.g.
    // `stackalloc uint[N] { ... }` (RVA-blob field ldtoken → CreateSpan)
    // and `typeof`. ILC 7 intrinsified the pattern without requiring the
    // type; ILC 8+ demands the type exist or codegen fails with
    // "Expected type 'Internal.Runtime.CompilerHelpers.LdTokenHelpers'
    // not found in module 'OS'". Bodies match the NativeAOT BCL shape
    // (pointer-store into the handle's single slot); the result is usually
    // intrinsified away, so these run only if a token escapes to runtime.
    //
    // GetRuntimeType (BCL's fourth method) is intentionally omitted — it
    // needs EETypePtr + Type.GetTypeFromEETypePtr reflection infra we
    // don't carry. Add it only if codegen reports it missing.
    internal static unsafe class LdTokenHelpers
    {
        private static RuntimeFieldHandle GetRuntimeFieldHandle(IntPtr pHandleSignature)
        {
            RuntimeFieldHandle h = default;
            *(IntPtr*)&h = pHandleSignature;
            return h;
        }

        private static RuntimeMethodHandle GetRuntimeMethodHandle(IntPtr pHandleSignature)
        {
            RuntimeMethodHandle h = default;
            *(IntPtr*)&h = pHandleSignature;
            return h;
        }

        private static RuntimeTypeHandle GetRuntimeTypeHandle(IntPtr pEEType)
        {
            RuntimeTypeHandle h = default;
            *(IntPtr*)&h = pEEType;
            return h;
        }
    }
}
