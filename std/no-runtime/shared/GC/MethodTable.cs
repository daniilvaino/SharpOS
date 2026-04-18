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
    }
}
