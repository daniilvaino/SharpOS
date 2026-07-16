using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace SharpOS.Std.NoRuntime
{
    // Kernel-only half of GcRuntimeExports (step137). These three runtime
    // exports were added in step131 for the vendored delegate code (casts +
    // `ref array[i]`), but they need System.Exception / InvalidCastException
    // (throw on failed cast) and Unsafe.As -- none of which the app tier ships
    // (Tier-B, halt-on-throw, no Unsafe). Apps include the shared partial but
    // never reference these (no delegates/casts), so they live here, out of the
    // app compile set, and the app build stays green. See the shared
    // std/no-runtime/shared/GC/GcRuntimeExports.cs.
    internal static unsafe partial class GcRuntimeExports
    {
        // checkcast(any): like IsInstanceOfAny but throws on failure. null casts
        // to anything.
        [RuntimeExport("RhTypeCast_CheckCastAny")]
        public static unsafe object RhTypeCast_CheckCastAny(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;
            if (RhTypeCast_IsInstanceOfAny(pTargetType, obj) == null)
                throw new InvalidCastException();
            return obj;
        }

        // checkcast to a class (non-interface, non-array target). JIT inlines the
        // trivial obj==null / mt==target cases; this slow path walks the base
        // chain and throws on miss.
        [RuntimeExport("RhTypeCast_CheckCastClassSpecial")]
        public static unsafe object RhTypeCast_CheckCastClassSpecial(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;

            nint objAddr = *(nint*)&obj;
            GcMethodTable* mt = *(GcMethodTable**)objAddr;

            const int MaxDepth = 64;
            GcMethodTable* cur = mt;
            for (int i = 0; i < MaxDepth; i++)
            {
                cur = cur->GetBaseType();
                if (cur == pTargetType) return obj;
                if (cur == null) break;
            }
            throw new InvalidCastException();
        }

        // ref array[index] for reference-element arrays. ILC emits this for
        // `ref a[i]` (e.g. Interlocked.CompareExchange(ref list[i], ...) in
        // MulticastDelegate.TrySetSlot). Trusted: skip null/bounds/covariance
        // checks (same policy as RhpStelemRef). Array layout: MT@0, Length@8,
        // element[0]@16, 8 bytes each.
        [RuntimeExport("RhpLdelemaRef")]
        public static unsafe ref object RhpLdelemaRef(System.Array array, nint index, IntPtr elementType)
        {
            nint arrayAddr = *(nint*)&array;
            byte* slot = (byte*)arrayAddr + 16 + index * 8;
            // Same pattern Buffer.cs uses (proven to compile in this project):
            // reinterpret the element slot as `ref object`.
            return ref System.Runtime.CompilerServices.Unsafe.As<byte, object>(ref *slot);
        }
    }
}
