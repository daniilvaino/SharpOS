using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // REGDISPLAY — register-pointer table used during stack walk. Each
    // pointer-typed field points to where the corresponding register's
    // value is currently spilled (initially in PAL_LIMITED_CONTEXT, then
    // increasingly into stack slots as the iterator unwinds frames).
    //
    // Per sage 2's step 5 sub-breakdown (non-UNIX AMD64): total size
    // 0x130 bytes. Subset relevant to step 4 / step 5 typed catch:
    //
    //   0x00  reserved      (pRax/pRcx/pRdx — volatiles, not needed)
    //   0x18  pRbx
    //   0x20  pRbp
    //   0x28  pRsi
    //   0x30  pRdi
    //   0x38  reserved      (pR8..pR11 — volatiles)
    //   0x58  pR12
    //   0x60  pR13
    //   0x68  pR14
    //   0x70  pR15
    //   0x78  SP
    //   0x80  ControlPC
    //   0x88  reserved      (OriginalControlPC — copy in StackFrameIterator)
    //   0x90..0x12F  Xmm[10] snapshots (xmm6..xmm15, each 16 bytes)
    //
    // The "pointer-to-value" model lets funclet thunks restore nonvols
    // back to actual hardware regs by indirect load. Step 4 only needs
    // pRbx/pRbp/pRsi/pRdi/pR12-15 + SP + ControlPC.
    [StructLayout(LayoutKind.Explicit, Size = 0x130)]
    internal unsafe struct RegDisplay
    {
        [FieldOffset(0x18)] public ulong* pRbx;
        [FieldOffset(0x20)] public ulong* pRbp;
        [FieldOffset(0x28)] public ulong* pRsi;
        [FieldOffset(0x30)] public ulong* pRdi;
        [FieldOffset(0x58)] public ulong* pR12;
        [FieldOffset(0x60)] public ulong* pR13;
        [FieldOffset(0x68)] public ulong* pR14;
        [FieldOffset(0x70)] public ulong* pR15;
        [FieldOffset(0x78)] public ulong  SP;
        [FieldOffset(0x80)] public ulong  ControlPC;

        public const int OffsetPRbx     = 0x18;
        public const int OffsetPRbp     = 0x20;
        public const int OffsetPRsi     = 0x28;
        public const int OffsetPRdi     = 0x30;
        public const int OffsetPR12     = 0x58;
        public const int OffsetPR13     = 0x60;
        public const int OffsetPR14     = 0x68;
        public const int OffsetPR15     = 0x70;
        public const int OffsetSP       = 0x78;
        public const int OffsetControlPC = 0x80;
        public const int OffsetXmm      = 0x90;

        public const int Size           = 0x130;
    }
}
