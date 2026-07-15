// Minimal RuntimeImports surface needed by the vendored Delegate /
// MulticastDelegate (dotnet/runtime v8.0.27). NativeAOT's real RuntimeImports
// is a large [RuntimeImport] extern table; we provide only RhNewObject, backed
// by our GcHeap. Partial so other subsystems can add their own imports.
//
// RhNewObject allocates a zeroed instance of a given type and returns a managed
// reference — the same shape as RhpNewFast (GC/GcRuntimeExports.cs), plus the
// pointer->object reinterpret pattern used by RhBox there.

using SharpOS.Std.NoRuntime;

namespace System.Runtime
{
    internal static unsafe partial class RuntimeImports
    {
        internal static object RhNewObject(EETypePtr ee)
        {
            Internal.Runtime.MethodTable* mt = ee.ToPointer();
            if (mt == null)
                return null;

            // GcMethodTable shares layout with Internal.Runtime.MethodTable
            // (see GcRuntimeExports.RhBox for the same cast).
            GcMethodTable* gcMt = (GcMethodTable*)mt;
            void* obj = GcHeap.AllocateRaw(gcMt->BaseSize);
            if (obj == null)
                return null;

            *(GcMethodTable**)obj = gcMt;

            object result = null;
            *(void**)&result = obj;
            return result;
        }
    }
}
