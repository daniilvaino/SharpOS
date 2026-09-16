using System;

namespace Internal.Runtime.CompilerHelpers
{
    // ILC's `ldtoken` lowering resolves these by fully-qualified name in the
    // system module — which, for a freestanding app, is the app itself. ILC 7
    // intrinsified the pattern without needing the type; ILC 8+ fails codegen
    // with "Expected type 'Internal.Runtime.CompilerHelpers.LdTokenHelpers'
    // not found" unless it exists.
    //
    // Same shape as the kernel's copy in OS/src/Boot/LdTokenHelpers.cs: a
    // pointer store into the handle's single slot. Usually intrinsified away,
    // so these bodies run only if a token escapes to runtime.
    //
    // GetRuntimeType — the BCL's fourth method, which returns a Type rather
    // than a handle — is deliberately absent. Add it only if codegen asks:
    // `typeof` reaches System.Type through GetTypeFromHandle, and a second
    // path to the same place is a second thing to keep honest.
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
