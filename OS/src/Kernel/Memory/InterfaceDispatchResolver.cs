using System.Runtime.InteropServices;
using OS.Hal;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // Managed-side of the interface-dispatch bridge.
    //
    // The x64 shellcode in InterfaceDispatchBridge spills ABI registers and
    // calls Resolve with:
    //    rcx = thisPtr    (raw pointer to the object instance)
    //    rdx = cellPtr    (InterfaceDispatchCell*)
    //    returns: resolved target-method pointer in rax (0 → fail handler).
    //
    // Flow (port of NativeAOT's RhpCidResolve_Worker + DispatchResolve):
    //   1. Read DispatchCellInfo from cell (walks forward to terminator).
    //   2. For CellType == InterfaceAndSlot: walk target type's inheritance
    //      chain, look up (interfaceIndex, interfaceSlot) → implSlot in each
    //      type's DispatchMap, return thisMT.GetSlot(implSlot).
    //   3. For CellType == VTableOffset: direct read at thisMT + offset.
    //
    // Not yet handled: variance, default interface methods, sealed virtuals
    // (GetSealedVirtualSlot), IDynamicInterfaceCastable, metadata tokens,
    // cache-writing CAS. Logs the first failure with full context then panics.
    internal static unsafe class InterfaceDispatchResolver
    {
        [UnmanagedCallersOnly]
        public static nint Resolve(nint thisPtr, nint cellPtr)
        {
            if (thisPtr == 0)
            {
                OS.Kernel.Panic.Fail("iface-resolve: null this");
                return 0;
            }

            GcMethodTable* thisMT = *(GcMethodTable**)thisPtr;
            InterfaceDispatchCell* cell = (InterfaceDispatchCell*)cellPtr;

            cell->GetDispatchCellInfo(out DispatchCellInfo info);

            if (info.CellType == DispatchCellType.VTableOffset)
            {
                return *(nint*)((byte*)thisMT + info.VTableOffset);
            }

            if (info.CellType != DispatchCellType.InterfaceAndSlot)
            {
                ReportDecodeFailure(thisPtr, thisMT, cellPtr, cell, in info,
                    "cell type not InterfaceAndSlot");
                OS.Kernel.Panic.Fail("iface-resolve: unsupported cell type");
                return 0;
            }

            if (info.InterfaceType == null)
            {
                ReportDecodeFailure(thisPtr, thisMT, cellPtr, cell, in info,
                    "interface type null");
                OS.Kernel.Panic.Fail("iface-resolve: null interface type");
                return 0;
            }

            // First-call lazy init: populate TypeManagerIndirection slots so
            // MT.DispatchMap works. thisMT is in .rdata and serves as the
            // scan anchor for the ReadyToRunHeader signature lookup.
            if (!NativeAotModuleInit.IsInitialized)
            {
                NativeAotModuleInit.TryInitialize(thisMT);
            }

            ushort implSlot;
            if (!FindImplSlot(thisMT, info.InterfaceType, info.InterfaceSlot, out implSlot))
            {
                ReportResolveFailure(thisPtr, thisMT, cellPtr, in info);
                OS.Kernel.Panic.Fail("iface-resolve: no impl slot");
                return 0;
            }

            void* target;
            if (implSlot >= thisMT->NumVtableSlots)
            {
                // Sealed virtual: lookup in the side table. We don't yet
                // handle Reabstraction / Diamond sentinel values — ILC
                // emits those as special high slot numbers; if we ever hit
                // them we'll see garbage addresses and diagnose.
                int sealedIndex = implSlot - thisMT->NumVtableSlots;
                target = thisMT->GetSealedVirtualSlot(sealedIndex);
            }
            else
            {
                target = thisMT->GetSlot(implSlot);
            }

            PublishCache(cell, thisMT, target, in info);
            return (nint)target;
        }

        // Allocates a single-slot InterfaceDispatchCache from KernelHeap,
        // populates it with { thisMT, target }, and writes a non-tagged
        // pointer to it into cell.m_pCache. Subsequent calls for the same
        // call site with the same instance type skip the resolver via the
        // shellcode fast path (test r11,3 == 0 → cmp + jmp).
        //
        // Single-threaded boot — no cmpxchg16b needed. On alloc failure the
        // cell stays tagged; we'll just resolve again next call (slow but
        // correct).
        private static void PublishCache(
            InterfaceDispatchCell* cell,
            GcMethodTable* thisMT,
            void* target,
            in DispatchCellInfo info)
        {
            // Layout (matches shellcode fast-path offsets):
            //   +0  InterfaceType (from CacheHeader)
            //   +8  SlotIndexOrMetadataTokenEncoded (uint)
            //   +12 pad
            //   +16 NextFree (unused here)
            //   +24 Entries (uint) — always 1
            //   +28 pad
            //   +32 entries[0].InstanceType
            //   +40 entries[0].TargetCode
            const uint CacheSize = 48;

            byte* cache = (byte*)KernelHeap.Alloc(CacheSize);
            if (cache == null) return;
            OS.Kernel.Util.Memory.Zero(cache, CacheSize);

            // Low 2 bits of pointer must be 0 so shellcode's `test r11, 3`
            // sees this as a cache, not a tagged interface. KernelHeap
            // returns 8-byte-aligned pointers, so the tag bits are already 0.
            // Defensive check — abandon cache publish if alignment is off.
            if (((nuint)cache & InterfaceDispatchCellFlags.CachePointerMask) != 0) return;

            *(GcMethodTable**)(cache + 0) = info.InterfaceType;                 // CacheHeader.InterfaceType
            *(uint*)(cache + 8) = (uint)info.InterfaceSlot << 2;                // slot << 2 | flag=TypeAndSlotIndex(0)
            *(uint*)(cache + 24) = 1;                                           // Entries = 1
            *(GcMethodTable**)(cache + 32) = thisMT;                            // entries[0].InstanceType
            *(void**)(cache + 40) = target;                                     // entries[0].TargetCode

            cell->Cache = (nuint)cache;
        }

        [UnmanagedCallersOnly]
        public static void Fail()
        {
            OS.Kernel.Panic.Fail("InterfaceDispatchResolver fail-path reached");
        }

        // Walks the target type's inheritance chain for a matching DispatchMap
        // entry. Match = (interfaceSlot, interfaceType-at-that-map-index).
        // Returns the impl slot (may be == interface slot, may differ — ILC
        // decides based on the vtable layout of the impl type).
        private static bool FindImplSlot(
            GcMethodTable* tgtType,
            GcMethodTable* itfType,
            ushort itfSlot,
            out ushort implSlot)
        {
            implSlot = 0;
            GcMethodTable* cur = tgtType;
            int walkCap = 16; // guard against bad chains

            while (cur != null && walkCap-- > 0)
            {
                if (cur->HasDispatchMap)
                {
                    DispatchMap* map = (DispatchMap*)cur->GetDispatchMap();
                    if (map != null)
                    {
                        DispatchMapEntry* entries = (DispatchMapEntry*)((byte*)map + 8);  // after 4 ushorts
                        uint count = map->StandardEntryCount;
                        EEInterfaceInfo* ifaceMap = cur->GetInterfaceMap();

                        for (uint i = 0; i < count; i++)
                        {
                            DispatchMapEntry* e = entries + i;
                            if (e->InterfaceMethodSlot != itfSlot) continue;

                            GcMethodTable* mapItf = ifaceMap[e->InterfaceIndex].GetInterfaceEEType();
                            if (mapItf == itfType || InterfaceMatchesWithVariance(mapItf, itfType))
                            {
                                implSlot = e->ImplMethodSlot;
                                return true;
                            }
                        }
                    }
                }

                if (cur->IsArray) break;   // array element-type walk not supported yet
                cur = cur->GetBaseType();
            }

            return false;
        }

        // Variance byte values, from MethodTable.Constants.cs (GenericVariance).
        private const byte VarianceNonVariant = 0;
        private const byte VarianceCovariant = 1;
        private const byte VarianceContravariant = 2;
        private const byte VarianceArrayCovariant = 0x20;

        /// <summary>
        /// True when <paramref name="mapItf"/> and <paramref name="wantItf"/> are
        /// the same generic interface differing only in type arguments that
        /// variance permits. Ported from TypeParametersAreCompatible
        /// (nativeaot/Runtime.Base/src/System/Runtime/TypeCast.cs).
        ///
        /// Deliberately conservative: anything not positively proven compatible
        /// returns false, which leaves the old behaviour (resolve failure) rather
        /// than dispatching to a wrong slot. A wrong slot would not panic — it
        /// would silently call the wrong method.
        /// </summary>
        private static bool InterfaceMatchesWithVariance(GcMethodTable* mapItf, GcMethodTable* wantItf)
        {
            if (mapItf == null || wantItf == null) return false;
            if (!mapItf->IsGeneric || !wantItf->IsGeneric) return false;

            GcMethodTable* def = mapItf->GetGenericDefinition();
            if (def == null || def != wantItf->GetGenericDefinition()) return false;

            // Variance lives on the definition; without it the instantiations
            // would have had to match exactly, which they did not.
            byte* variance = def->GetGenericVariance();
            if (variance == null) return false;

            int arity = def->GenericParameterCount;
            if (arity <= 0 || arity > 8) return false;   // implausible: refuse

            for (int i = 0; i < arity; i++)
            {
                GcMethodTable* src = mapItf->GetGenericArgument(i, arity);
                GcMethodTable* dst = wantItf->GetGenericArgument(i, arity);
                if (src == null || dst == null) return false;
                if (src == dst) continue;

                switch (variance[i])
                {
                    case VarianceCovariant:
                    case VarianceArrayCovariant:
                        if (!IsAssignableTo(src, dst)) return false;
                        break;

                    case VarianceContravariant:
                        if (!IsAssignableTo(dst, src)) return false;
                        break;

                    case VarianceNonVariant:
                    default:
                        return false;    // must have been identical
                }
            }

            return true;
        }

        /// <summary>
        /// Conservative reference-type assignability: identity, a base class of
        /// <paramref name="src"/>, or an interface <paramref name="src"/> lists.
        /// Value types are refused outright — variance never applies to them, and
        /// treating them as assignable would be a soundness hole.
        /// </summary>
        private static bool IsAssignableTo(GcMethodTable* src, GcMethodTable* dst)
        {
            if (src == null || dst == null) return false;
            if (src == dst) return true;
            if (src->IsValueType || dst->IsValueType) return false;

            // Base class chain.
            GcMethodTable* cur = src;
            int walkCap = 16;
            while (cur != null && walkCap-- > 0)
            {
                if (cur == dst) return true;
                cur = cur->GetBaseType();
            }

            // Interfaces the source implements. Direct pointer match only: a
            // recursive variance check here would need cycle detection, and
            // nothing on the paths we support requires it.
            EEInterfaceInfo* map = src->GetInterfaceMap();
            if (map != null)
            {
                int n = src->NumInterfaces;
                for (int i = 0; i < n; i++)
                {
                    if (map[i].GetInterfaceEEType() == dst) return true;
                }
            }

            return false;
        }

        private static void ReportDecodeFailure(
            nint thisPtr, GcMethodTable* thisMT,
            nint cellPtr, InterfaceDispatchCell* cell,
            in DispatchCellInfo info, string reason)
        {
            Log.Begin(LogLevel.Warn);
            Console.Write("iface-resolve fail (decode): ");
            Console.Write(reason);
            Log.EndLine();

            DumpResolveState(thisPtr, thisMT, cellPtr, cell, in info);
        }

        private static void ReportResolveFailure(
            nint thisPtr, GcMethodTable* thisMT,
            nint cellPtr, in DispatchCellInfo info)
        {
            Log.Write(LogLevel.Warn, "iface-resolve fail (no match in inheritance chain)");
            DumpResolveState(thisPtr, thisMT, cellPtr, null, in info);
            DumpTypeMaps(thisMT);
        }

        private static void DumpTypeMaps(GcMethodTable* mt)
        {
            if (mt == null) return;

            EEInterfaceInfo* ifaceMap = mt->GetInterfaceMap();
            for (int i = 0; i < (int)mt->NumInterfaces; i++)
            {
                GcMethodTable* itf = ifaceMap[i].GetInterfaceEEType();
                Log.Begin(LogLevel.Warn);
                Console.Write("  ifaceMap[");
                Console.WriteULongRaw((ulong)i);
                Console.Write("]=0x");
                Console.WriteHexRaw((ulong)itf, 16);
                Console.Write(" raw=0x");
                Console.WriteHexRaw((ulong)ifaceMap[i].RawInterfaceType, 16);
                Log.EndLine();
            }

            // net8/major-9 diag: HasDispatchMap reads 0 (section 203 gone,
            // the map likely moved to a trailing relative pointer in the
            // EEType). Dump flags + the raw tail after the interface map so we
            // can locate the new dispatch-map pointer. REMOVE once decoded.
            {
                byte* mb = (byte*)mt;
                byte* optF = mt->GetOptionalFieldsPtr();
                Log.Begin(LogLevel.Warn);
                Console.Write("  [tail] flags=0x"); Console.WriteHexRaw((ulong)mt->Flags, 4);
                Console.Write(" hasOptF="); Console.WriteULongRaw(mt->HasOptionalFields ? 1ul : 0ul);
                Console.Write(" optF=0x"); Console.WriteHexRaw((ulong)optF, 16);
                if (optF != null)
                {
                    Console.Write(" optF[0..8]=0x"); Console.WriteHexRaw(*(ulong*)optF, 16);
                }
                Log.EndLine();
                int ifSize = 8; // EEInterfaceInfo assumed 8B; adjust if tail misaligns
                int tail = 24 + (int)mt->NumVtableSlots * 8 + (int)mt->NumInterfaces * ifSize;
                Log.Begin(LogLevel.Warn);
                Console.Write("  [tail] ifaceMapEnd=+0x"); Console.WriteHexRaw((ulong)tail, 3);
                Log.EndLine();
                for (int off = tail - 8; off < tail + 0x40; off += 8)
                {
                    Log.Begin(LogLevel.Warn);
                    Console.Write("    mt+0x"); Console.WriteHexRaw((ulong)off, 3);
                    Console.Write(" = 0x"); Console.WriteHexRaw(*(ulong*)(mb + off), 16);
                    // also show low int32 as a self-relative target from here
                    int rel = *(int*)(mb + off);
                    Console.Write("  rel->0x"); Console.WriteHexRaw((ulong)(nint)(mb + off + rel), 16);
                    Log.EndLine();
                }
            }

            if (mt->HasDispatchMap)
            {
                byte* optFields = mt->GetOptionalFieldsPtr();
                uint idx = OptionalFieldsReader.GetInlineField(
                    optFields, EETypeOptionalFieldTag.DispatchMap, 0xFFFFFFFFu);
                mt->GetTypeManagerDispatchMapTableDiag(
                    out byte* pIndirection, out byte* slot, out byte* tm, out byte* table);

                Log.Begin(LogLevel.Warn);
                Console.Write("  optFields=0x");
                Console.WriteHexRaw((ulong)optFields, 16);
                Console.Write(" dmIdx=");
                Console.WriteULongRaw(idx);
                Log.EndLine();

                Log.Begin(LogLevel.Warn);
                Console.Write("  pIndir=0x");
                Console.WriteHexRaw((ulong)pIndirection, 16);
                Console.Write(" rel32=0x");
                Console.WriteHexRaw((ulong)(uint)(*(int*)pIndirection), 8);
                Console.Write(" slot=0x");
                Console.WriteHexRaw((ulong)slot, 16);
                Log.EndLine();

                Log.Begin(LogLevel.Warn);
                Console.Write("  slot[0..8]=0x");
                Console.WriteHexRaw((ulong)(*(ulong*)slot), 16);
                Console.Write(" tm=0x");
                Console.WriteHexRaw((ulong)tm, 16);
                Console.Write(" dmTable=0x");
                Console.WriteHexRaw((ulong)table, 16);
                Log.EndLine();

                DispatchMap* map = (DispatchMap*)mt->GetDispatchMap();
                if (map == null)
                {
                    Log.Write(LogLevel.Warn, "  dispatchMap=null");
                    return;
                }

                Log.Begin(LogLevel.Warn);
                Console.Write("  dispatchMap=0x");
                Console.WriteHexRaw((ulong)map, 16);
                Console.Write(" std=");
                Console.WriteULongRaw(map->StandardEntryCount);
                Console.Write(" def=");
                Console.WriteULongRaw(map->DefaultEntryCount);
                Console.Write(" stdStatic=");
                Console.WriteULongRaw(map->StandardStaticEntryCount);
                Console.Write(" defStatic=");
                Console.WriteULongRaw(map->DefaultStaticEntryCount);
                Log.EndLine();

                DispatchMapEntry* entries = (DispatchMapEntry*)((byte*)map + 8);
                uint total = (uint)(map->StandardEntryCount + map->DefaultEntryCount);
                if (total > 16) total = 16;
                for (uint i = 0; i < total; i++)
                {
                    DispatchMapEntry* e = entries + i;
                    Log.Begin(LogLevel.Warn);
                    Console.Write("  dm[");
                    Console.WriteULongRaw(i);
                    Console.Write("] iface=");
                    Console.WriteULongRaw(e->InterfaceIndex);
                    Console.Write(" itfSlot=");
                    Console.WriteULongRaw(e->InterfaceMethodSlot);
                    Console.Write(" implSlot=");
                    Console.WriteULongRaw(e->ImplMethodSlot);
                    Log.EndLine();
                }
            }
        }

        private static void DumpResolveState(
            nint thisPtr, GcMethodTable* thisMT,
            nint cellPtr, InterfaceDispatchCell* cell,
            in DispatchCellInfo info)
        {
            Log.Begin(LogLevel.Warn);
            Console.Write("  this=0x");
            Console.WriteHexRaw((ulong)thisPtr, 16);
            Console.Write(" mt=0x");
            Console.WriteHexRaw((ulong)thisMT, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Warn);
            Console.Write("  cell=0x");
            Console.WriteHexRaw((ulong)cellPtr, 16);
            if (cell != null)
            {
                Console.Write(" stub=0x");
                Console.WriteHexRaw((ulong)cell->Stub, 16);
                Console.Write(" cache=0x");
                Console.WriteHexRaw((ulong)cell->Cache, 16);
            }
            Log.EndLine();

            Log.Begin(LogLevel.Warn);
            Console.Write("  cellType=");
            Console.WriteULongRaw((ulong)info.CellType);
            Console.Write(" itf=0x");
            Console.WriteHexRaw((ulong)info.InterfaceType, 16);
            Console.Write(" slot=");
            Console.WriteULongRaw((ulong)info.InterfaceSlot);
            Log.EndLine();

            if (thisMT != null)
            {
                Log.Begin(LogLevel.Warn);
                Console.Write("  mt.NumVtableSlots=");
                Console.WriteULongRaw((ulong)thisMT->NumVtableSlots);
                Console.Write(" NumInterfaces=");
                Console.WriteULongRaw((ulong)thisMT->NumInterfaces);
                Console.Write(" HasDispatchMap=");
                Console.WriteULongRaw(thisMT->HasDispatchMap ? 1ul : 0ul);
                Log.EndLine();
            }
        }
    }
}
