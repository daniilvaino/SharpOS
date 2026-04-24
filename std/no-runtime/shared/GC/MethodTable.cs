// NativeAOT MethodTable (EEType) layout + flags parser.
//
// Source of truth: dotnet/runtime release/7.0 branch:
//   src/coreclr/nativeaot/Runtime/inc/MethodTable.h  (MIT licensed)
//
// Layout (verified via SUPER-2 phase 0 reconnaissance):
//   offset 0  — m_usComponentSize (uint16): element size for arrays/strings, else 0
//   offset 2  — m_usFlags (uint16): Kind (bits 0-1) + attributes + ElementType (bits 11-15)
//   offset 4  — m_uBaseSize (uint32): size of fixed part
//   offset 8  — m_RelatedType (union): ParentType for CanonicalEEType,
//                                       CanonicalType for ClonedEEType,
//                                       ElementType for ParameterizedEEType (arrays etc.)
//   offset 16 — m_usNumVtableSlots (uint16)
//   offset 18 — m_usNumInterfaces (uint16)
//   offset 20 — m_uHashCode (uint32)
//   offset 24 — m_VTable[] ...
//
// Constants below are copied verbatim from MethodTable.h Flags enum (MIT).

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    public enum GcEETypeKind
    {
        Canonical = 0,
        Cloned = 1,
        Parameterized = 2,   // arrays, byrefs, pointers
        GenericTypeDef = 3,
    }

    public enum GcEETypeElementType
    {
        Unknown = 0x00,
        Void = 0x01,
        Boolean = 0x02,
        Char = 0x03,
        SByte = 0x04,
        Byte = 0x05,
        Int16 = 0x06,
        UInt16 = 0x07,
        Int32 = 0x08,
        UInt32 = 0x09,
        Int64 = 0x0A,
        UInt64 = 0x0B,
        IntPtr = 0x0C,
        UIntPtr = 0x0D,
        Single = 0x0E,
        Double = 0x0F,
        ValueType = 0x10,
        Nullable = 0x12,
        Class = 0x14,
        Interface = 0x15,
        SystemArray = 0x16,
        Array = 0x17,
        SzArray = 0x18,
        ByRef = 0x19,
        Pointer = 0x1A,
    }

    [StructLayout(LayoutKind.Explicit)]
    public unsafe struct GcMethodTable
    {
        // --- Flag bits (from MethodTable.h, release/7.0) ---

        private const ushort EETypeKindMask = 0x0003;
        private const ushort RelatedTypeViaIATFlag = 0x0004;
        private const ushort IsDynamicTypeFlag = 0x0008;
        private const ushort HasFinalizerFlag = 0x0010;
        private const ushort HasPointersFlag = 0x0020;
        private const ushort GenericVarianceFlag = 0x0080;
        private const ushort OptionalFieldsFlag = 0x0100;
        private const ushort IsGenericFlag = 0x0400;
        private const ushort ElementTypeMask = 0xF800;
        private const int ElementTypeShift = 11;

        // --- Layout ---

        [FieldOffset(0)]
        public ushort ComponentSize;

        [FieldOffset(2)]
        public ushort Flags;

        [FieldOffset(4)]
        public uint BaseSize;

        [FieldOffset(8)]
        public GcMethodTable* RelatedType;

        // NativeAOT layout past the first 16 bytes. Verified via AsmOffsets.h
        // (`ASM_OFFSET(14,18, MethodTable, m_VTable)` → 0x18 = 24 on AMD64)
        // and MethodTable.h fields m_usNumVtableSlots / m_usNumInterfaces /
        // m_uHashCode. VTable starts at offset 24, InterfaceMap after it
        // (see GetInterfaceMap below).
        [FieldOffset(16)]
        public ushort NumVtableSlots;

        [FieldOffset(18)]
        public ushort NumInterfaces;

        [FieldOffset(20)]
        public uint HashCode;

        // VTable slots array starts here and runs for NumVtableSlots * 8 bytes.
        // Access via pointer arithmetic since size is variable.
        public const int VTableOffset = 24;

        // --- Derived queries ---

        // Universal: EEType has variable-size tail iff ComponentSize > 0.
        // This covers strings (char elements) and SzArrays (T[] elements).
        public bool HasComponentSize => ComponentSize != 0;

        public GcEETypeKind Kind => (GcEETypeKind)(Flags & EETypeKindMask);

        public GcEETypeElementType ElementType =>
            (GcEETypeElementType)((Flags & ElementTypeMask) >> ElementTypeShift);

        public bool HasPointers => (Flags & HasPointersFlag) != 0;

        public bool HasFinalizer => (Flags & HasFinalizerFlag) != 0;

        public bool IsArray
        {
            get
            {
                GcEETypeElementType et = ElementType;
                return et == GcEETypeElementType.Array
                    || et == GcEETypeElementType.SzArray;
            }
        }

        public bool IsSzArray => ElementType == GcEETypeElementType.SzArray;

        public bool IsValueType
        {
            get
            {
                GcEETypeElementType et = ElementType;
                // Primitives (0x02..0x0F), ValueType (0x10), Nullable (0x12).
                return (et >= GcEETypeElementType.Boolean && et <= GcEETypeElementType.Double)
                    || et == GcEETypeElementType.ValueType
                    || et == GcEETypeElementType.Nullable;
            }
        }

        public bool IsInterface => ElementType == GcEETypeElementType.Interface;

        public bool IsGeneric => (Flags & IsGenericFlag) != 0;

        public bool IsRelatedTypeViaIAT => (Flags & RelatedTypeViaIATFlag) != 0;

        public bool IsDynamicType => (Flags & IsDynamicTypeFlag) != 0;

        public bool HasOptionalFields => (Flags & OptionalFieldsFlag) != 0;

        // VTable slot read — returns the code pointer for the given slot.
        // Matches MethodTable.h `get_Slot(uint16_t slotNumber)`.
        public void* GetSlot(int slotNumber)
        {
            fixed (GcMethodTable* self = &this)
            {
                void** vtable = (void**)((byte*)self + VTableOffset);
                return vtable[slotNumber];
            }
        }

        // Interface map starts immediately after the variable-sized vtable.
        // Matches MethodTable.inl:107-118 GetFieldOffset(ETF_InterfaceMap):
        //   cbOffset = offsetof(m_VTable) + sizeof(UIntTarget) * m_usNumVtableSlots
        public EEInterfaceInfo* GetInterfaceMap()
        {
            fixed (GcMethodTable* self = &this)
            {
                byte* p = (byte*)self + VTableOffset + (int)NumVtableSlots * 8;
                return (EEInterfaceInfo*)p;
            }
        }

        // Direct base type in non-array case. Matches MethodTable.h
        // `NonArrayBaseType` for Canonical/Cloned kinds. For our resolver we
        // only walk `class` hierarchies (ClonedEEType path is rare; not yet
        // handled). IAT indirection (RelatedTypeViaIATFlag) handled.
        public GcMethodTable* GetBaseType()
        {
            GcMethodTable* related = RelatedType;
            if (related == null) return null;
            if (IsRelatedTypeViaIAT)
                return *(GcMethodTable**)related;
            return related;
        }

        // Layout of the "extras" region after interface map. On AMD64 Release
        // with SupportsRelativePointers=true (our baseline, non-dynamic types)
        // every pointer here is a 4-byte RelativePointer whose Value is
        // (&field + (int32)field).
        //
        // Order (from MethodTable.cs::GetFieldOffset):
        //   1. TypeManagerIndirection   — always present
        //   2. WritableData             — present iff SupportsWritableData (= SupportsRelativePointers = true here)
        //   3. Finalizer                — iff HasFinalizer
        //   4. OptionalFieldsPtr        — iff HasOptionalFields
        //
        // For the interface-dispatch resolver we only need (1) and (4). Skip
        // (2) unconditionally since it's always present in our build, and (3)
        // only when HasFinalizer.
        private byte* ExtrasStart
        {
            get
            {
                fixed (GcMethodTable* self = &this)
                {
                    return (byte*)self + VTableOffset + (int)NumVtableSlots * 8
                        + (int)NumInterfaces * sizeof(EEInterfaceInfo);
                }
            }
        }

        // Reads a RelativePointer<T> at the given byte pointer and returns the
        // pointed-at address. Matches `RelativePointer<T>.Value`.
        private static byte* ReadRelativePointer(byte* at)
        {
            int disp = *(int*)at;
            return at + disp;
        }

        public GcMethodTable* GetTypeManager()
        {
            byte* pTypeManagerIndirection = ExtrasStart;
            // TypeManagerIndirection is a RelativePointer to a TypeManagerSlot.
            // TypeManagerSlot.TypeManager (offset 0) is the actual TypeManager*.
            byte* slot = ReadRelativePointer(pTypeManagerIndirection);
            return *(GcMethodTable**)slot;
        }

        // Returns null if this MT has no OptionalFields blob.
        public byte* GetOptionalFieldsPtr()
        {
            if (!HasOptionalFields) return null;

            byte* p = ExtrasStart;
            p += 4;                             // skip TypeManagerIndirection (rel32)
            p += 4;                             // skip WritableData (always present in our build)
            if (HasFinalizer) p += 4;           // skip Finalizer if any
            return ReadRelativePointer(p);
        }

        // Returns pointer to TypeManager's DispatchMap-table (m_pDispatchMapTable
        // at TypeManager offset 16: see Runtime/TypeManager.h layout).
        public byte* GetTypeManagerDispatchMapTable()
        {
            GetTypeManagerDispatchMapTableDiag(out _, out _, out _, out byte* table);
            return table;
        }

        // Same as GetTypeManagerDispatchMapTable but also exposes the intermediates
        // for diagnostics. Not called on the hot path.
        public void GetTypeManagerDispatchMapTableDiag(
            out byte* pIndirection,
            out byte* slot,
            out byte* typeManager,
            out byte* dispatchMapTable)
        {
            pIndirection = ExtrasStart;
            slot = ReadRelativePointer(pIndirection);
            typeManager = *(byte**)slot;
            if (typeManager == null) { dispatchMapTable = null; return; }
            dispatchMapTable = *(byte**)(typeManager + 16);   // DispatchMap**
        }

        // Returns true iff the MT has a DispatchMap (interface method resolution
        // table) — checks the DispatchMap optional field tag. Dynamic types not
        // handled yet. Arrays handled via element type's map (not implemented).
        public bool HasDispatchMap
        {
            get
            {
                byte* optFields = GetOptionalFieldsPtr();
                if (optFields == null) return false;

                uint idx = OptionalFieldsReader.GetInlineField(
                    optFields, EETypeOptionalFieldTag.DispatchMap, 0xFFFFFFFFu);
                return idx != 0xFFFFFFFFu;
            }
        }

        // Returns the code pointer for a sealed-virtual slot.
        //
        // Sealed virtuals are interface methods that aren't stored in the
        // regular vtable — they live in a side table whose address is a
        // rel32 stored in MT at a specific offset. Each table entry is also
        // a rel32 relative to its own location, pointing at the method code.
        //
        // DispatchMap entries with impl slot >= NumVtableSlots refer to
        // this sealed table; the slotNumber passed here is
        //   implSlotFromDispatchMap - NumVtableSlots.
        //
        // Must only be called when the type's RareFlags has
        // HasSealedVTableEntriesFlag set (the DispatchMap pointing past
        // NumVtableSlots is the caller's implicit signal).
        public void* GetSealedVirtualSlot(int slotNumber)
        {
            // ExtrasStart + 4 (TypeManagerIndirection) + 4 (WritableData,
            // SupportsWritableData=true) + [Finalizer if HasFinalizer] +
            // [OptionalFieldsPtr if HasOptionalFields] → SealedVirtualSlots rel32.
            byte* p = ExtrasStart;
            p += 4;
            p += 4;
            if (HasFinalizer) p += 4;
            if (HasOptionalFields) p += 4;

            byte* tableAddr = ReadRelativePointer(p);
            // Table is rel32[]. Entry slotNumber is at tableAddr + slotNumber*4.
            byte* entryAddr = tableAddr + slotNumber * 4;
            int rel = *(int*)entryAddr;
            return entryAddr + rel;
        }

        // Returns the DispatchMap* for this type, or null if not present.
        public void* GetDispatchMap()
        {
            byte* optFields = GetOptionalFieldsPtr();
            if (optFields == null) return null;

            uint idx = OptionalFieldsReader.GetInlineField(
                optFields, EETypeOptionalFieldTag.DispatchMap, 0xFFFFFFFFu);
            if (idx == 0xFFFFFFFFu) return null;

            byte* table = GetTypeManagerDispatchMapTable();
            if (table == null) return null;

            // Table is DispatchMap*[].
            return ((void**)table)[idx];
        }
    }

    // MethodTable.h:24-40. Each entry in the interface map is one of these —
    // on AMD64 it's a union of two pointer-sized alternates (direct type
    // pointer vs indirection through the IAT). The IAT flag lives in the
    // low bit of the raw value.
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct EEInterfaceInfo
    {
        // Union: either `MethodTable* m_pInterfaceEEType` (direct) or
        // `MethodTable** m_ppInterfaceEETypeViaIAT` with low bit set.
        public void* RawInterfaceType;

        // Mirrors `GetInterfaceEEType()` from MethodTable.h:27.
        public GcMethodTable* GetInterfaceEEType()
        {
            nuint raw = (nuint)RawInterfaceType;
            if ((raw & 1) != 0)
            {
                // IAT indirection: read MethodTable* from the aligned slot.
                GcMethodTable** indirection = (GcMethodTable**)(raw & ~(nuint)1);
                return *indirection;
            }
            return (GcMethodTable*)raw;
        }
    }
}
