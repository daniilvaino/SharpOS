using System.Runtime.InteropServices;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // ---------------------------------------------------------------------
    // Port of interface-dispatch data structures from NativeAOT runtime.
    // All layouts must match the originals byte-for-byte — ILC emits code
    // that hard-codes offsets into these fields, and the dispatch-cell
    // table in the image binary is laid out by ILC according to the same
    // contract. Do not reorder fields without updating the binder side.
    //
    // Sources (verbatim):
    //   src/coreclr/nativeaot/Runtime/inc/rhbinder.h
    //   src/coreclr/nativeaot/Runtime/CachedInterfaceDispatch.h
    // ---------------------------------------------------------------------

    // rhbinder.h:15
    internal enum DispatchCellType : uint
    {
        InterfaceAndSlot = 0x0,
        MetadataToken = 0x1,
        VTableOffset = 0x2,
    }

    // rhbinder.h:22
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DispatchCellInfo
    {
        public DispatchCellType CellType;       // +0  (4 bytes)
        public GcMethodTable* InterfaceType;    // +8  (8 bytes on x64, padding on +4)
        public ushort InterfaceSlot;            // +16 (2 bytes)
        public byte HasCache;                   // +18 (1 byte)
        public uint MetadataToken;              // +20 (4 bytes, after 1-byte hole)
        public uint VTableOffset;               // +24 (4 bytes)
    }

    // rhbinder.h:32
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct InterfaceDispatchCacheHeader
    {
        // m_pInterfaceType: MethodTable of the interface whose dispatch goes through this cache.
        public GcMethodTable* InterfaceType;            // +0  (8 bytes)
        // m_slotIndexOrMetadataTokenEncoded: low 2 bits = Flags (CH_TypeAndSlotIndex=0 / CH_MetadataToken=1),
        // remaining bits (shift 2) = interface slot index OR metadata token.
        public uint SlotIndexOrMetadataTokenEncoded;    // +8  (4 bytes)
        // +12: 4 bytes tail padding so the next struct element is 8-aligned.
    }

    // rhbinder.h:104. One allocated per interface call site in the image.
    // ILC emits the dispatch-cell table directly into the binary; each cell
    // initially holds m_pStub = &RhpInitialDynamicInterfaceDispatch and
    // m_pCache = one of the IDC_CachePointerIs* tagged values that encodes
    // the interface type + slot.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct InterfaceDispatchCell
    {
        public nuint Stub;              // m_pStub        — code to call for the first dispatch
        public nuint Cache;             // m_pCache       — tagged cache ptr / vtable offset / raw interface ref

        // Decodes the cell's cache field + walks forward to the terminator
        // cell (m_pStub == 0) for slot/cellType, producing a complete
        // DispatchCellInfo. Direct port of rhbinder.h:140-208
        // `InterfaceDispatchCell::GetDispatchCellInfo`.
        public void GetDispatchCellInfo(out DispatchCellInfo info)
        {
            info = default;
            nuint cachePointerValue = Cache;

            // Case 1: cache is a real cache pointer (not a VTableOffset sentinel).
            if ((cachePointerValue & InterfaceDispatchCellFlags.CachePointerMask)
                == InterfaceDispatchCellFlags.CachePointerPointsAtCache
                && cachePointerValue >= InterfaceDispatchCellFlags.MaxVTableOffsetPlusOne)
            {
                // cachePointerValue points at an InterfaceDispatchCacheHeader.
                InterfaceDispatchCacheHeader* header = (InterfaceDispatchCacheHeader*)cachePointerValue;
                info.InterfaceType = header->InterfaceType;
                uint encoded = header->SlotIndexOrMetadataTokenEncoded;
                if ((encoded & 0x3) == 0)
                {
                    info.CellType = DispatchCellType.InterfaceAndSlot;
                    info.InterfaceSlot = (ushort)(encoded >> 2);
                }
                else
                {
                    info.CellType = DispatchCellType.MetadataToken;
                    info.MetadataToken = encoded >> 2;
                }
                info.HasCache = 1;
                return;
            }

            // Case 2: VTableOffset inline (low bits 0, value < 0x1000).
            if (cachePointerValue < InterfaceDispatchCellFlags.MaxVTableOffsetPlusOne
                && (cachePointerValue & InterfaceDispatchCellFlags.CachePointerMask)
                    == InterfaceDispatchCellFlags.CachePointerPointsAtCache)
            {
                info.CellType = DispatchCellType.VTableOffset;
                info.VTableOffset = (uint)cachePointerValue;
                info.HasCache = 1;
                return;
            }

            // Case 3: tagged Interface+Slot. Walk forward to terminator.
            fixed (InterfaceDispatchCell* thisCell = &this)
            {
                InterfaceDispatchCell* walker = thisCell;
                while (walker->Stub != 0) walker++;

                nuint termCache = walker->Cache;
                // term cache: bits 0-15 = slot, bits 16-31 = flags/cellType
                DispatchCellType cellType = (DispatchCellType)(uint)(termCache >> 16);
                info.CellType = cellType;

                if (cellType == DispatchCellType.InterfaceAndSlot)
                {
                    info.InterfaceSlot = (ushort)termCache;

                    nuint tag = cachePointerValue & InterfaceDispatchCellFlags.CachePointerMask;
                    if (tag == InterfaceDispatchCellFlags.CachePointerIsInterfacePointerOrMetadataToken)
                    {
                        info.InterfaceType = (SharpOS.Std.NoRuntime.GcMethodTable*)
                            (cachePointerValue & ~InterfaceDispatchCellFlags.CachePointerMask);
                    }
                    else if (tag == InterfaceDispatchCellFlags.CachePointerIsInterfaceRelativePointer
                          || tag == InterfaceDispatchCellFlags.CachePointerIsIndirectedInterfaceRelativePointer)
                    {
                        // Relative-to-&m_pCache with low 2 bits in tag; sign-extend int32.
                        byte* pCacheField = (byte*)thisCell + sizeof(nuint);   // &m_pCache
                        long ext = (int)(uint)cachePointerValue;               // sign-extend
                        nuint ifacePtrRaw = (nuint)((long)pCacheField + ext);
                        ifacePtrRaw &= ~InterfaceDispatchCellFlags.CachePointerMask;

                        if (tag == InterfaceDispatchCellFlags.CachePointerIsInterfaceRelativePointer)
                            info.InterfaceType = (SharpOS.Std.NoRuntime.GcMethodTable*)ifacePtrRaw;
                        else
                            info.InterfaceType = *(SharpOS.Std.NoRuntime.GcMethodTable**)ifacePtrRaw;
                    }
                }
                else
                {
                    info.MetadataToken = (uint)(cachePointerValue >> InterfaceDispatchCellFlags.CachePointerMaskShift);
                }
            }
        }
    }

    // DispatchMap — table of (interfaceIndex, interfaceSlot) → implSlot entries.
    // Port of Common/src/Internal/Runtime/MethodTable.cs::DispatchMap layout.
    //
    //   ushort StandardEntryCount        — instance-method entries
    //   ushort DefaultEntryCount         — default-interface-method entries
    //   ushort StandardStaticEntryCount  — static virtuals (standard)
    //   ushort DefaultStaticEntryCount   — static virtuals (default)
    //   DispatchMapEntry[StandardEntryCount + DefaultEntryCount]   (6 bytes each)
    //   StaticDispatchMapEntry[...] following the above
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DispatchMap
    {
        public ushort StandardEntryCount;
        public ushort DefaultEntryCount;
        public ushort StandardStaticEntryCount;
        public ushort DefaultStaticEntryCount;
        // Followed by DispatchMapEntry[]
    }

    [StructLayout(LayoutKind.Sequential, Size = 6)]
    internal struct DispatchMapEntry
    {
        public ushort InterfaceIndex;
        public ushort InterfaceMethodSlot;
        public ushort ImplMethodSlot;
    }

    internal static class InterfaceDispatchCellFlags
    {
        // rhbinder.h:128. Low 2 bits of m_pCache describe its contents.
        public const nuint CachePointerIsInterfaceRelativePointer          = 0x3;
        public const nuint CachePointerIsIndirectedInterfaceRelativePointer = 0x2;
        public const nuint CachePointerIsInterfacePointerOrMetadataToken   = 0x1;
        public const nuint CachePointerPointsAtCache                       = 0x0;
        public const nuint CachePointerMask                                = 0x3;
        public const int   CachePointerMaskShift                           = 0x2;

        // If m_pCache < 0x1000 AND the low 2 bits are CachePointerPointsAtCache,
        // the field actually encodes a vtable byte offset, not a pointer.
        public const nuint MaxVTableOffsetPlusOne                          = 0x1000;
    }

    // CachedInterfaceDispatch.h:18. Entries must be aligned at twice pointer
    // size — the 16-byte atomic update pairs (MT + target) together.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct InterfaceDispatchCacheEntry
    {
        public GcMethodTable* InstanceType;     // potential concrete type of `this`
        public void* TargetCode;                // method pointer if InstanceType matches
    }

    // CachedInterfaceDispatch.h:31. Variable-length trailer — entries array of
    // size Entries. On x64 the first entry sits at offset 32 from the cache
    // start (16 header + 8 union + 4 Entries + 4 pad). Reached via pointer
    // arithmetic, e.g. (InterfaceDispatchCacheEntry*)((byte*)cache + 32).
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct InterfaceDispatchCache
    {
        public InterfaceDispatchCacheHeader CacheHeader;  // 16 bytes
        // Union in the C++ source: { InterfaceDispatchCache* NextFree;
        //                            InterfaceDispatchCell* Cell (non-AMD64) }
        // AMD64-only: just NextFree.
        public InterfaceDispatchCache* NextFree;          // +16 (8 bytes)
        public uint Entries;                              // +24 (4 bytes)
        // +28: 4 bytes pad so first entry starts at +32 and is 16-aligned.
    }
}
