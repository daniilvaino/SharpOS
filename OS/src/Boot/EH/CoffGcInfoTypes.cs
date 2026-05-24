namespace OS.Boot.EH
{
    // GcInfo encoding constants for AMD64. Mirrors the AMD64 case of
    // ILCompiler.Reflection.ReadyToRun.GcInfoTypes ctor with the default
    // values inherited from the static field initialisers. AMD64-only
    // because our kernel target is x86_64; adding ARM64 / Arm32 / RV64
    // later means adding a sibling enum / struct and switching by
    // image's Machine field. No code copied — values cross-checked
    // against dotnet-runtime-sharpos GCInfoTypes.cs.
    internal static class CoffGcInfoTypes
    {
        // ---- header sizes ----
        public const int SizeOfReturnKindSlim = 2;
        public const int SizeOfReturnKindFat  = 4;   // AMD64 override

        // ---- ENCBASE constants for DecodeVarLengthUnsigned/Signed ----
        public const int CodeLengthEncBase                       = 8;
        public const int NormPrologSizeEncBase                   = 5;
        public const int NormEpilogSizeEncBase                   = 3;
        public const int SecurityObjectStackSlotEncBase          = 6;
        public const int GsCookieStackSlotEncBase                = 6;
        public const int PspSymStackSlotEncBase                  = 6;
        public const int GenericsInstContextStackSlotEncBase     = 6;
        public const int StackBaseRegisterEncBase                = 3;
        public const int SizeOfEditAndContinuePreservedAreaEncBase = 4;
        public const int ReversePInvokeFrameEncBase              = 6;
        public const int SizeOfStackAreaEncBase                  = 3;
        public const int NumSafePointsEncBase                    = 2;   // AMD64 override (default 3)
        public const int NumInterruptibleRangesEncBase           = 1;
        public const int InterruptibleRangeDelta1EncBase         = 6;
        public const int InterruptibleRangeDelta2EncBase         = 6;

        public const int NumRegistersEncBase                     = 2;
        public const int NumStackSlotsEncBase                    = 2;
        public const int NumUntrackedSlotsEncBase                = 1;
        public const int RegisterEncBase                         = 3;
        public const int RegisterDeltaEncBase                    = 2;
        public const int StackSlotEncBase                        = 6;
        public const int StackSlotDeltaEncBase                   = 4;
        public const int PointerSizeEncBase                      = 3;

        public const int LivestateRleRunEncBase                  = 2;
        public const int LivestateRleSkipEncBase                 = 4;

        // For AMD64 V2/V3 there's no code-offset normalization (offsets
        // are raw byte offsets). ARM/ARM64 shift right by 1 or 2 because
        // instruction alignment is fixed. Kept here for completeness and
        // for the version >= 4 case where x64 also normalizes.
        public static int DenormalizeCodeLength(int x) => x;       // AMD64 identity
        public static uint DenormalizeCodeOffset(uint x) => x;     // AMD64 identity
        public static uint NormalizeCodeOffset(uint x) => x;       // AMD64 identity

        // Stack-slot offsets are encoded as pointer-multiples (x*8 on AMD64).
        public static int DenormalizeStackSlot(int x) => x << 3;

        // Header-level StackBaseRegister field decode (XOR with 5 on AMD64):
        //   encoded 0 → 5 (RBP)
        //   encoded 1 → 4 (RSP)
        // Only present when fat header has GC_INFO_HAS_STACK_BASE_REGISTER.
        public static uint DenormalizeStackBaseRegister(uint x) => x ^ 5;

        // Ceil(log2(x)) — bits needed to express values [0..x). Used by
        // safepoint-offset reader (each offset is encoded with this many
        // bits relative to the method size).
        public static int CeilOfLog2(int x)
        {
            if (x <= 1) return 0;
            int bits = 0;
            int v = x - 1;
            while (v > 0) { v >>= 1; bits++; }
            return bits;
        }
    }

    // Bit flags packed into the fat header (10 bits in v2+, 9 in v1).
    // Slim header collapses these to just GC_INFO_HAS_STACK_BASE_REGISTER.
    internal enum GcInfoHeaderFlags : uint
    {
        None                                = 0,
        IsVararg                            = 0x001,
        HasSecurityObject                   = 0x002,
        HasGsCookie                         = 0x004,
        HasPspSym                           = 0x008,
        HasGenericsInstContextMask          = 0x030,
        HasGenericsInstContextNone          = 0x000,
        HasGenericsInstContextMt            = 0x010,
        HasGenericsInstContextMd            = 0x020,
        HasGenericsInstContextThis          = 0x030,
        HasStackBaseRegister                = 0x040,
        WantsReportOnlyLeaf                 = 0x080,
        HasEditAndContinuePreservedSlots    = 0x100,
        ReversePInvokeFrame                 = 0x200,
    }

    // Per-version bit-width of the fat header flags field.
    internal static class GcInfoFlagsBitSize
    {
        public const int V1 = 9;
        public const int CurrentVersion = 10;
    }
}
