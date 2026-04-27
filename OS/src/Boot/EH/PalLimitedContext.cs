using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // PAL_LIMITED_CONTEXT — minimal CPU context snapshot used by EH dispatch.
    //
    // Layout matches non-UNIX AMD64 NativeAOT (per sage 2's step 5
    // sub-breakdown). Total size 0x100 bytes:
    //
    //   0x00  IP                  return address / fault IP
    //   0x08  Rsp                 stack pointer at capture
    //   0x10  Rbp                 frame pointer
    //   0x18  Rdi                 callee-saved
    //   0x20  Rsi                 callee-saved
    //   0x28  Rax                 (reserved / not relied on by SFI)
    //   0x30  Rbx                 callee-saved
    //   0x38  R12                 callee-saved
    //   0x40  R13                 callee-saved
    //   0x48  R14                 callee-saved
    //   0x50  R15                 callee-saved
    //   0x58  pad
    //   0x60..0xFF  Xmm6..Xmm15   each 16 bytes (10 regs)
    //
    // Step 4 SfiInit/SfiNext only reads the GPR section. XMM section is
    // populated by the future RhpThrowEx shellcode (step 5+). For step 4
    // smoke probe RhpCaptureContext leaves XMM zeroed.
    [StructLayout(LayoutKind.Explicit, Size = 0x100)]
    internal unsafe struct PalLimitedContext
    {
        [FieldOffset(0x00)] public ulong IP;
        [FieldOffset(0x08)] public ulong Rsp;
        [FieldOffset(0x10)] public ulong Rbp;
        [FieldOffset(0x18)] public ulong Rdi;
        [FieldOffset(0x20)] public ulong Rsi;
        [FieldOffset(0x28)] public ulong Rax;
        [FieldOffset(0x30)] public ulong Rbx;
        [FieldOffset(0x38)] public ulong R12;
        [FieldOffset(0x40)] public ulong R13;
        [FieldOffset(0x48)] public ulong R14;
        [FieldOffset(0x50)] public ulong R15;
        // 0x58 pad
        // 0x60..0xFF  XMM6..XMM15 (each 16 bytes) — accessed via raw bytes
        //   when needed by step 5+ funclet thunks. Not referenced from
        //   managed step 4 code.

        // Field offsets exposed for shellcode emitters.
        public const int OffsetIP   = 0x00;
        public const int OffsetRsp  = 0x08;
        public const int OffsetRbp  = 0x10;
        public const int OffsetRdi  = 0x18;
        public const int OffsetRsi  = 0x20;
        public const int OffsetRax  = 0x28;
        public const int OffsetRbx  = 0x30;
        public const int OffsetR12  = 0x38;
        public const int OffsetR13  = 0x40;
        public const int OffsetR14  = 0x48;
        public const int OffsetR15  = 0x50;
        public const int OffsetXmm6 = 0x60;

        public const int Size       = 0x100;
    }
}
