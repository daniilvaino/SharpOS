using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.12 — kernel-side policy for runtime type equivalence
    // (FEATURE_TYPEEQUIVALENCE). When two assemblies independently import
    // the same COM TypeLib (e.g. Office.Interop), .NET treats the imported
    // types as equivalent if they have matching [TypeIdentifier] /
    // [ComImport] attributes. PowerShell's runtime type system may probe
    // for equivalence even when no COM is actually used.
    //
    // On unikernel COM type equivalence is meaningless (no real TypeLib
    // imports occur). We always report "not equivalent" — types are
    // identical only by reference equality. PowerShell's reflection paths
    // handle this gracefully — distinct types stay distinct.
    internal static unsafe class TypeEquivalence
    {
        [RuntimeExport("SharpOSHost_TypeIsEquivalentTo")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_TypeIsEquivalentTo")]
        public static int IsEquivalentTo() => 0;  // FALSE
    }
}
