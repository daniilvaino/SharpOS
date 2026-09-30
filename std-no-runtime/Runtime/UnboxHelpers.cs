// RhUnbox2 — taking a value type back out of an object.
//
// ILC emits a call to this for `(int)someObject` and for every `case int n:`
// pattern over an object. Nothing in the kernel had needed it, so it was
// missing on both tiers and surfaced as a link error the first time a real
// library did the ordinary thing.
//
// Ported in shape from dotnet/runtime's Runtime.Base (MIT). The type check is
// the whole substance: an unbox that returns the payload without confirming
// the type reads whatever bytes are there as the wrong type, which is silent
// memory corruption rather than an exception.

using System.Runtime;
using System.Runtime.CompilerServices;
using SharpOS.Std.NoRuntime;

namespace System.Runtime
{
    internal static unsafe class UnboxHelpers
    {
        /// <summary>
        /// Returns a reference to the boxed value's payload.
        /// </summary>
        [RuntimeExport("RhUnbox2")]
        public static ref byte RhUnbox2(GcMethodTable* pUnboxToEEType, object obj)
        {
            if (obj == null)
                throw new NullReferenceException();

            nint objAddr = *(nint*)&obj;
            GcMethodTable* pObjType = *(GcMethodTable**)objAddr;

            if (!TypesMatchForUnbox(pObjType, pUnboxToEEType))
                throw new InvalidCastException();

            // The payload starts immediately after the method-table pointer,
            // which is what RawData exists to name.
            return ref Unsafe.As<RawData>(obj).Data;
        }

        /// <summary>
        /// Whether a box of <paramref name="pObjType"/> may be unboxed to
        /// <paramref name="pTargetType"/>.
        /// </summary>
        /// <remarks>
        /// Identity is the ordinary case. The real BCL also allows an enum to
        /// unbox to its underlying type and vice versa, because a boxed enum
        /// and a boxed int are indistinguishable in memory; without that,
        /// `(int)(object)DayOfWeek.Monday` throws. Comparing the element type
        /// covers it — and covers nothing else, which is the point.
        /// </remarks>
        private static bool TypesMatchForUnbox(GcMethodTable* pObjType, GcMethodTable* pTargetType)
        {
            if (pObjType == pTargetType) return true;
            if (pObjType == null || pTargetType == null) return false;

            return pObjType->ElementType == pTargetType->ElementType
                && pObjType->ElementType != GcEETypeElementType.Unknown;
        }
    }
}
