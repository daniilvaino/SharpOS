// NativeAOT runtime export symbols backed by GcHeap.
//
// NativeAOT's codegen emits calls to RhpNewFast / RhpNewArray / RhNewString
// for `new T()`, `new T[n]`, `new string(...)`. These are normally provided
// by the NativeAOT runtime (AllocFast.asm etc.). In our freestanding setup
// we define them as [RuntimeExport] C# methods that allocate from GcHeap.
//
// Layout (matches dotnet/runtime/src/coreclr/nativeaot/Runtime/portable.cpp):
//   Object : [MethodTable*](8)     — BaseSize bytes total
//   Array  : [MethodTable*](8)[Length(4)]    — BaseSize + Length*ComponentSize
//   String : same as Array (NativeAOT treats string as an array in this path)
//
// Signatures copied from portable.cpp. Linked into OS.csproj (kernel) only
// for phase 3; apps keep C-stub RhNewString until phase 4 when we migrate
// them off.

using System;
using System.Runtime;
using System.Runtime.CompilerServices;

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe partial class GcRuntimeExports
    {
        // The allocation helpers throw on failure, as the real runtime's do.
        // The code ILC generates around them never checks for null: a null
        // returned here became a constructor storing fields at null+8,
        // silently into physical page 0 until the pager unmaps it.
        [RuntimeExport("RhpNewFast")]
        internal static void* RhpNewFast(GcMethodTable* mt)
        {
            if (mt == null)
                return null;

            uint size = mt->BaseSize;
            void* obj = GcHeap.AllocateObject(size, mt);
            if (obj == null)
                throw GcHeap.OutOfMemory();

            return obj;
        }

        [RuntimeExport("RhpNewArray")]
        private static void* RhpNewArray(GcMethodTable* mt, int numElements)
        {
            if (mt == null)
                return null;
            if (numElements < 0)
                throw new OverflowException();

            // size = BaseSize + numElements * ComponentSize, pointer-aligned
            ulong size64 = (ulong)mt->BaseSize + ((ulong)(uint)numElements * (ulong)mt->ComponentSize);
            size64 = (size64 + 7UL) & ~7UL;
            if (size64 > GcHeap.MaxAllocationSize)
            {
                // Say what was asked for before refusing. This path never
                // reaches AllocateRaw, so without this the refusal report
                // prints whatever the PREVIOUS allocation happened to be —
                // and it did: a probe asking for an impossible array produced
                // "request=192 ... largest=51936", which reads as an
                // allocator that refused a block 270 times too large. It
                // refused nothing; the number belonged to something else.
                GcHeap.NoteRefusedRequest(size64);
                throw GcHeap.OutOfMemory();
            }

            // Length field lives at offset 8 (sizeof(MethodTable*) on x64).
            void* obj = GcHeap.AllocateArray((uint)size64, mt, numElements);
            if (obj == null)
                throw GcHeap.OutOfMemory();

            return obj;
        }

        [RuntimeExport("RhNewString")]
        private static void* RhNewString(GcMethodTable* mt, int numElements)
        {
            // NativeAOT's portable.cpp delegates RhNewString to RhpNewArray.
            // string's MT has ComponentSize = 2 (one char) and BaseSize covers
            // the header + null-terminator slot.
            return RhpNewArray(mt, numElements);
        }

        // Write barrier for managed reference assignment (e.g. `s_field = obj`).
        // In NativeAOT's generational GC this marks the containing card dirty.
        // Our GC is single-threaded non-generational mark-sweep — no write
        // barrier needed, just plain pointer store.
        //
        // One exception: a store into the exchange heap (a received region).
        // A region may refer only to null or to objects of its own block — a
        // reference out of it would not keep its target alive and would mean
        // nothing in the next image. The check costs one subtraction and one
        // compare on every other store.
        [RuntimeExport("RhpAssignRef")]
        private static void RhpAssignRef(void** dst, void* src)
        {
            if ((ulong)dst - SharpOS.Std.Exchange.ExchangeArena.Bounds.Low < SharpOS.Std.Exchange.ExchangeArena.Bounds.Span
                && !SharpOS.Std.Exchange.ExchangeArena.StoreAllowed((ulong)dst, (ulong)src))
                ThrowRegionStore();
            *dst = src;
        }

        // A fault, not a throw: see ExchangeArena.StoreFaultAddress.
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ThrowRegionStore()
            => *(byte*)SharpOS.Std.Exchange.ExchangeArena.StoreFaultAddress = 0;

        // Same as RhpAssignRef with a null-pointer check before write. We
        // don't care about the check (CLR uses it to protect against bad
        // targets during lazy init) — just perform the store.
        [RuntimeExport("RhpCheckedAssignRef")]
        private static void RhpCheckedAssignRef(void** dst, void* src)
        {
            if ((ulong)dst - SharpOS.Std.Exchange.ExchangeArena.Bounds.Low < SharpOS.Std.Exchange.ExchangeArena.Bounds.Span
                && !SharpOS.Std.Exchange.ExchangeArena.StoreAllowed((ulong)dst, (ulong)src))
                ThrowRegionStore();
            *dst = src;
        }

        // Box a value type into a fresh object. ILC emits a call to RhBox for
        // implicit `object o = valueType` and for constrained virtual dispatch
        // on a value type where the target method is inherited from Object
        // (e.g., x.Equals(y) inside a generic `where T : anything`).
        //
        // Real NativeAOT RhBox handles Nullable<T> unwrapping, GC write barriers,
        // and RequiresAlign8 misaligned alloc. We skip all of that — our GC is
        // non-generational (no barrier) and Nullable<T> is a stub empty struct.
        //
        // Copy size: real runtime uses mt->ValueTypeSize; we don't have that
        // field on GcMethodTable, so we use mt->BaseSize - 8 (BaseSize includes
        // the MT* header plus padding). That may copy a few bytes of padding
        // slack, harmless because readers only look at the declared value bytes.
        [RuntimeExport("RhBox")]
        public static object RhBox(Internal.Runtime.MethodTable* mt, ref byte data)
        {
            if (mt == null) return null;

            // Our GcMethodTable shares layout with Internal.Runtime.MethodTable;
            // cast via nint/pointer-reinterpret to read BaseSize.
            GcMethodTable* gcMt = (GcMethodTable*)mt;
            void* obj = GcHeap.AllocateObject(gcMt->BaseSize, mt);
            if (obj == null)
                throw GcHeap.OutOfMemory();

            uint payload = gcMt->BaseSize - 8;
            byte* dst = (byte*)obj + 8;
            fixed (byte* pData = &data)
            {
                for (uint i = 0; i < payload; i++)
                    dst[i] = pData[i];
            }

            object result = null;
            *(void**)&result = obj;
            return result;
        }

        // Reference-array element store (ILC generates a call to this for
        // `arr[i] = obj` where arr is object[] or any other reference-type
        // array). Signature MUST match the NativeAOT contract exactly
        // (Array, nint, object) — ILC matches [RuntimeExport] targets by
        // signature, not just name. See dotnet/runtime TypeCast.cs:745.
        //
        // Ported from TypeCast.StelemRef (non-INPLACE_RUNTIME branch): the
        // helper itself owns the null and bounds checks — ILC emits no range
        // check before calling it, so without them `arr[i] = x` past the end
        // wrote into whatever followed the array. No write barrier: the
        // collector is non-generational.
        //
        // The element-type check throws ArrayTypeMismatchException, as the
        // original does. It was counted first, to see whether kernel arrays
        // ever receive objects whose MethodTable belongs to another image; a
        // launcher + AotTests run counted none. Stores where the element type
        // or the value is itself an array are not checked yet: array casting
        // is not implemented (pipe_plan.md, item 6).
        //
        // Array layout (NativeAOT x64):
        //   +0:  MethodTable*
        //   +8:  Length (int32 + 4-byte pad)
        //   +16: element[0], element[1], ...   (8 bytes each for refs)
        // Interface dispatch — the "first call" trampoline that the real
        // runtime installs into each interface-dispatch cell. In the BCL this
        // is hand-written asm (StubDispatch.asm) that resolves the concrete
        // target the first time an interface method is called, caches it,
        // and tail-calls. portable.cpp's C++ fallback is `ASSERT_UNCONDITIONALLY("NYI")`
        // — so we mirror that with an infinite loop. Linker requires the
        // symbol to exist even for methods that never dispatch through it.
        // Interface dispatch — the "first call" trampoline. Moved into
        // OS/src/Boot/InterfaceDispatchStub.cs so the kernel variant can
        // Panic.Fail with a descriptive message instead of spinning silently.
        // Apps don't currently hit this path; if they do, add a halt stub
        // in apps_native/sdk/ the same way.

        [RuntimeExport("RhpStelemRef")]
        public static unsafe void RhpStelemRef(System.Array array, nint index, object value)
        {
            if (array == null)
                throw new NullReferenceException();
            nint arrayAddr = *(nint*)&array;
            if ((ulong)index >= *(uint*)((byte*)arrayAddr + 8))
                throw new IndexOutOfRangeException();

            nint valueAddr = 0;
            if (value != null)
            {
                valueAddr = *(nint*)&value;
                GcMethodTable* elementType = (*(GcMethodTable**)arrayAddr)->RelatedType;
                if (*(GcMethodTable**)valueAddr != elementType)
                    StelemRef_Helper(elementType, value);
            }
            byte* slot = (byte*)arrayAddr + 16 + ((long)index * 8);
            if ((ulong)slot - SharpOS.Std.Exchange.ExchangeArena.Bounds.Low < SharpOS.Std.Exchange.ExchangeArena.Bounds.Span
                && !SharpOS.Std.Exchange.ExchangeArena.StoreAllowed((ulong)slot, (ulong)valueAddr))
                ThrowRegionStore();
            *(nint*)slot = valueAddr;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static unsafe void StelemRef_Helper(GcMethodTable* elementType, object value)
        {
            // object[] takes anything — including an object of another image,
            // whose base chain ends at THAT image's Object and would never meet
            // ours (the original's INPLACE_RUNTIME shortcut, for the same reason).
            if (IsSystemObject(elementType))
                return;

            GcMethodTable* valueType = *(GcMethodTable**)*(nint*)&value;
            if (!AreTypesAssignable(valueType, elementType, boxedSource: true, allowSizeEquivalence: false))
                throw new ArrayTypeMismatchException();
        }

        // ---- Assignability, ported from TypeCast.AreTypesAssignableInternalUncached
        // and WellKnownEETypes (dotnet/runtime release/8.0, NativeAOT
        // Runtime.Base). Cut: the cast cache, generic variance (our `is` never
        // had it), pointer / byref / function-pointer types (no such objects
        // exist), IDynamicInterfaceCastable.
        //
        // Before this, every cast walked GetBaseType(), which for an array is
        // its ELEMENT type: string[] answered "is string", int[] "is ValueType",
        // and no array was ever an Array; int[] -> uint[] and Derived[] ->
        // Base[] failed (pipe_plan.md item 6).

        // WellKnownEETypes.IsSystemObject / IsValidArrayBaseType: by the type's
        // own shape, not by pointer — so Object of ANY image answers, which a
        // pointer compare against this image's Object could not.
        private static unsafe bool IsSystemObject(GcMethodTable* type)
        {
            if (type->IsArray)
                return false;
            return type->GetBaseType() == null && !type->IsInterface;
        }

        private static unsafe bool IsValidArrayBaseType(GcMethodTable* type)
        {
            GcEETypeElementType elementType = type->ElementType;
            return elementType == GcEETypeElementType.SystemArray
                || (elementType == GcEETypeElementType.Class && type->GetBaseType() == null);
        }

        internal static unsafe bool AreTypesAssignable(GcMethodTable* pSourceType, GcMethodTable* pTargetType,
                                                       bool boxedSource, bool allowSizeEquivalence)
        {
            if (pSourceType == pTargetType)
                return true;

            if (pTargetType->IsInterface)
            {
                // Value types can only be cast to interfaces if they're boxed.
                if (!boxedSource && pSourceType->IsValueType)
                    return false;
                return ImplementsInterface(pSourceType, pTargetType);
            }

            if (pSourceType->IsInterface)
                return IsSystemObject(pTargetType);

            // Array to array: same shape, then the element types — reference
            // elements covariantly, value elements only when identical or
            // integers of one size (int[] <-> uint[], enum[] <-> int[]).
            if (pTargetType->IsArray)
            {
                if (pSourceType->IsArray
                    && pSourceType->ElementType == pTargetType->ElementType
                    && pSourceType->BaseSize == pTargetType->BaseSize)
                {
                    return AreTypesAssignable(pSourceType->RelatedType, pTargetType->RelatedType,
                                              boxedSource: false, allowSizeEquivalence: true);
                }
                return false;
            }

            // Target type is not an array. But we can still cast arrays to Object or System.Array.
            if (pSourceType->IsArray)
                return IsValidArrayBaseType(pTargetType);

            if (pSourceType->IsValueType)
            {
                if (allowSizeEquivalence && IsPrimitiveElement(pTargetType))
                    return IsPrimitiveElement(pSourceType)
                        && NormalizedIntegral(pSourceType) == NormalizedIntegral(pTargetType);

                if (!boxedSource)
                    return false;
            }

            const int MaxDepth = 64;
            GcMethodTable* cur = pSourceType;
            for (int i = 0; i < MaxDepth; i++)
            {
                cur = cur->GetBaseType();
                if (cur == null) return false;
                if (cur == pTargetType) return true;
            }
            return false;
        }

        private static unsafe bool ImplementsInterface(GcMethodTable* pType, GcMethodTable* pInterface)
        {
            int count = pType->NumInterfaces;
            if (count == 0) return false;
            EEInterfaceInfo* map = pType->GetInterfaceMap();
            for (int i = 0; i < count; i++)
                if (map[i].GetInterfaceEEType() == pInterface)
                    return true;
            return false;
        }

        // Primitives and enums: an element type code in Boolean..Double.
        private static unsafe bool IsPrimitiveElement(GcMethodTable* type)
        {
            GcEETypeElementType et = type->ElementType;
            return et >= GcEETypeElementType.Boolean && et <= GcEETypeElementType.Double;
        }

        // TypeCast.GetNormalizedIntegralArrayElementType: unsigned folds onto
        // its signed twin, so int and uint (and an int-based enum) compare equal.
        private static unsafe GcEETypeElementType NormalizedIntegral(GcMethodTable* type)
        {
            GcEETypeElementType et = type->ElementType;
            switch (et)
            {
                case GcEETypeElementType.Byte:
                case GcEETypeElementType.UInt16:
                case GcEETypeElementType.UInt32:
                case GcEETypeElementType.UInt64:
                case GcEETypeElementType.UIntPtr:
                    return et - 1;
            }
            return et;
        }

        // Emitted by ILC for `obj is SomeInterface` and the `is`-pattern
        // variant. BCL's version in Runtime.Base/TypeCast.cs walks the
        // cast cache + handles variance + IDynamicInterfaceCastable — all
        // of which we skip. The MVP: simple InterfaceMap lookup.
        //
        // Parameter order matches the BCL export: pTargetType first, obj
        // second. Returns obj on match, null otherwise.
        // Class-cast type check. ILC emits a call to this for `obj is Class`,
        // for catch-clause type matching (`catch (InvalidOperationException)`),
        // and for `(Class)obj` style explicit casts via CheckCastClass.
        //
        // Algorithm: identity check first (fast path for exact-type), then
        // walk the class hierarchy via RawBaseType. We skip the BCL's
        // generic-variance / cloned-type / array-to-object special cases —
        // they don't apply to our exception hierarchy or anything in our
        // current kernel-tier code. Will need to extend when hosted-tier
        // (Phase 6+) ships managed apps with variance.
        [RuntimeExport("RhTypeCast_IsInstanceOfClass")]
        public static unsafe object RhTypeCast_IsInstanceOfClass(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;

            nint objAddr = *(nint*)&obj;
            GcMethodTable* pObjType = *(GcMethodTable**)objAddr;

            if (pObjType == pTargetType)
                return obj;

            return AreTypesAssignable(pObjType, pTargetType, boxedSource: true, allowSizeEquivalence: false)
                ? obj : null;
        }

        // Boolean variant of IsInstanceOfClass specifically for catch-clause
        // matching. ILC's EH dispatcher (DispatchEx -> ShouldTypedClauseCatchThisException)
        // calls this when it needs `bool` rather than `object`. Same algorithm
        // as IsInstanceOfClass minus the object return.
        [RuntimeExport("RhTypeCast_IsInstanceOfException")]
        public static unsafe bool RhTypeCast_IsInstanceOfException(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return false;

            nint objAddr = *(nint*)&obj;
            GcMethodTable* pObjType = *(GcMethodTable**)objAddr;

            if (pObjType == pTargetType)
                return true;

            const int MaxDepth = 32;
            for (int i = 0; i < MaxDepth; i++)
            {
                pObjType = pObjType->GetBaseType();
                if (pObjType == null)
                    return false;
                if (pObjType == pTargetType)
                    return true;
            }
            return false;
        }

        [RuntimeExport("RhTypeCast_IsInstanceOfInterface")]
        public static unsafe object RhTypeCast_IsInstanceOfInterface(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;

            // `obj` is a managed ref — its in-memory representation is a
            // pointer to the object; the first 8 bytes of the object are
            // the MethodTable pointer.
            nint objAddr = *(nint*)&obj;
            GcMethodTable* pObjType = *(GcMethodTable**)objAddr;

            int count = pObjType->NumInterfaces;
            if (count == 0) return null;

            EEInterfaceInfo* map = pObjType->GetInterfaceMap();
            for (int i = 0; i < count; i++)
            {
                if (map[i].GetInterfaceEEType() == pTargetType)
                    return obj;
            }

            return null;
        }

        // ---- General cast helpers (step131). ILC emits these for the vendored
        // delegate code's (Delegate[]) / (MulticastDelegate) casts, `as`, and
        // `ref array[i]`. Minimal trusted-kernel versions: no cast cache, no
        // generic variance. The real NativeAOT TypeCast.cs adds a cast cache +
        // variance + array-covariance we don't need here.

        // isinst(any): null->null; exact MT; interface map; else class-hierarchy
        // walk (also lets an array/object reach a matching base). obj or null.
        [RuntimeExport("RhTypeCast_IsInstanceOfAny")]
        public static unsafe object RhTypeCast_IsInstanceOfAny(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;

            nint objAddr = *(nint*)&obj;
            GcMethodTable* mt = *(GcMethodTable**)objAddr;
            if (mt == pTargetType) return obj;

            return AreTypesAssignable(mt, pTargetType, boxedSource: true, allowSizeEquivalence: false)
                ? obj : null;
        }

        // RhTypeCast_CheckCastAny / CheckCastClassSpecial / RhpLdelemaRef lived
        // in a kernel-only partial (step137) because the app tier then had no
        // Exception/InvalidCastException/Unsafe. Since step140/141 apps share
        // the kernel EH engine and compile the full std (delegates included),
        // that split's reason is gone — moved back here (step142) so app links
        // resolve them (DropResilient surfaced the missing symbols).

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

        // TypeCast.CheckCastInterface / CheckCastClass (release/8.0). ILC calls
        // them for a cast it could not settle at compile time — e.g.
        // `(ISpanFormattable)value` on a shared-generic reference T in
        // DefaultInterpolatedStringHandler. SharpOS cut: the unrolled interface
        // scan and the cast cache; the same answer through AreTypesAssignable.
        [RuntimeExport("RhTypeCast_CheckCastInterface")]
        public static unsafe object RhTypeCast_CheckCastInterface(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;
            GcMethodTable* mt = *(GcMethodTable**)*(nint*)&obj;
            if (AreTypesAssignable(mt, pTargetType, boxedSource: true, allowSizeEquivalence: false))
                return obj;
            throw new InvalidCastException();
        }

        [RuntimeExport("RhTypeCast_CheckCastClass")]
        public static unsafe object RhTypeCast_CheckCastClass(GcMethodTable* pTargetType, object obj)
        {
            if (obj == null) return null;
            GcMethodTable* mt = *(GcMethodTable**)*(nint*)&obj;
            if (mt == pTargetType ||
                AreTypesAssignable(mt, pTargetType, boxedSource: true, allowSizeEquivalence: false))
                return obj;
            throw new InvalidCastException();
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

            if (mt == pTargetType ||
                AreTypesAssignable(mt, pTargetType, boxedSource: true, allowSizeEquivalence: false))
                return obj;
            throw new InvalidCastException();
        }

        // ref array[index] for reference-element arrays. ILC emits this for
        // `ref a[i]` (e.g. Interlocked.CompareExchange(ref list[i], ...) in
        // MulticastDelegate.TrySetSlot). Ported from TypeCast.LdelemaRef: a
        // `ref` into a covariant array must be to exactly the element type,
        // or a later store through it would bypass the store check. Array
        // layout: MT@0, Length@8, element[0]@16, 8 bytes each.
        [RuntimeExport("RhpLdelemaRef")]
        public static unsafe ref object RhpLdelemaRef(System.Array array, nint index, System.IntPtr elementType)
        {
            if (array == null)
                throw new NullReferenceException();
            nint arrayAddr = *(nint*)&array;
            if ((ulong)index >= *(uint*)((byte*)arrayAddr + 8))
                throw new IndexOutOfRangeException();
            if ((GcMethodTable*)elementType != (*(GcMethodTable**)arrayAddr)->RelatedType)
                throw new ArrayTypeMismatchException();
            byte* slot = (byte*)arrayAddr + 16 + index * 8;
            // Same pattern Buffer.cs uses (proven to compile in this project):
            // reinterpret the element slot as `ref object`.
            return ref System.Runtime.CompilerServices.Unsafe.As<byte, object>(ref *slot);
        }
    }
}
