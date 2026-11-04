// Ported from dotnet/runtime release/8.0
// src/coreclr/nativeaot/System.Private.CoreLib/src/Internal/Runtime/CompilerHelpers/DelegateHelpers.cs
// (MIT). No cuts. ILC's delegate object-array thunk (DelegateInvokeObjectArrayThunk,
// DynamicInvoke) asks the system module for it by name; it builds those thunks
// once the system module is a real System.Private.CoreLib.

using System;

namespace Internal.Runtime.CompilerHelpers
{
    /// <summary>
    /// Delegate helpers for generated code.
    /// </summary>
    internal static class DelegateHelpers
    {
        private static object[] s_emptyObjectArray = Array.Empty<object>();

        internal static object[] GetEmptyObjectArray()
        {
            return s_emptyObjectArray;
        }
    }
}
