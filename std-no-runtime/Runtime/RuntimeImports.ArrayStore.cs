// RuntimeImports.RhCheckArrayStore — what ILC calls from a multidimensional
// array's Set for a reference element type (step223: T[,] in SharpCompress).
// NativeAOT's is an import of the runtime's covariance check; ours is the
// one RhpStelemRef already does (GC/GcRuntimeExports.cs).

namespace System.Runtime
{
    internal static unsafe partial class RuntimeImports
    {
        internal static void RhCheckArrayStore(object array, object? obj) =>
            SharpOS.Std.NoRuntime.GcRuntimeExports.CheckArrayStore(array, obj!);
    }
}
