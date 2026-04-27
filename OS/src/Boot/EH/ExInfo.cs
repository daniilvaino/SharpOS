using System;
using System.Runtime.InteropServices;

namespace OS.Boot.EH
{
    // ExInfo — per-throw EH state. Layout matches stock NativeAOT
    // (sage 2 step 5 sub-breakdown). Total size 0x260 bytes.
    //
    //   0x00  m_pPrevExInfo        (8)   — chain pointer
    //   0x08  m_pExContext         (8)   — PAL_LIMITED_CONTEXT*
    //   0x10  m_exception          (8)   — managed object reference
    //   0x18  m_kind               (1)   — ExKind enum
    //   0x19  m_passNumber         (1)   — 1 = first pass, 2 = second pass
    //   0x1A  pad                  (2)
    //   0x1C  m_idxCurClause       (4)   — handler-active state during walk
    //   0x20  m_frameIter          (...) — embedded StackFrameIterator (0x230)
    //   0x250 m_notifyDebuggerSP   (8)   — debugger sync point (unused in SharpOS)
    //
    // Asm thunk fills: m_pPrevExInfo, m_pExContext, m_kind, m_passNumber,
    // m_idxCurClause on entry to RhpThrowEx.
    // Managed dispatcher updates: m_exception, m_kind (rethrow/HwFault flags),
    // m_frameIter (via StackFrameIterator init), m_idxCurClause as walk
    // progresses, m_notifyDebuggerSP (left null in single-thread mode).
    [StructLayout(LayoutKind.Explicit, Size = 0x260)]
    internal unsafe struct ExInfo
    {
        [FieldOffset(0x000)] public ExInfo* PrevExInfo;
        [FieldOffset(0x008)] public PalLimitedContext* ExContext;
        [FieldOffset(0x010)] public ulong Exception;        // GcObject* as ulong for shellcode interop
        [FieldOffset(0x018)] public byte Kind;
        [FieldOffset(0x019)] public byte PassNumber;
        [FieldOffset(0x01C)] public uint IdxCurClause;
        [FieldOffset(0x020)] public StackFrameIterator FrameIter;
        [FieldOffset(0x250)] public ulong NotifyDebuggerSP;

        // ExInfo.m_kind enum.
        public const byte KindNone = 0;
        public const byte KindThrow = 1;
        public const byte KindHardwareFault = 2;
        public const byte KindRethrow = 4;       // bit flag combined with KindThrow

        // m_idxCurClause sentinel for "no handler yet".
        public const uint MaxTryRegionIdx = 0xFFFFFFFFu;

        // Field offsets exposed for shellcode emitter.
        public const int OffsetPrevExInfo    = 0x000;
        public const int OffsetExContext     = 0x008;
        public const int OffsetException     = 0x010;
        public const int OffsetKind          = 0x018;
        public const int OffsetPassNumber    = 0x019;
        public const int OffsetIdxCurClause  = 0x01C;
        public const int OffsetFrameIter     = 0x020;
        public const int OffsetNotifyDebuggerSP = 0x250;

        public const int Size                = 0x260;
    }

    // Single-thread linked-list head for active ExInfo chain. Stock
    // NativeAOT puts this in Thread.m_pExInfoStackHead (per-thread). Our
    // kernel runs single-thread so a static IntPtr is sufficient. Will
    // need to migrate to per-thread storage in Phase 3 (scheduler).
    //
    // Stored as IntPtr (rather than ExInfo*) because Unsafe.AsPointer<T>
    // forbids pointer-type T. Consumers cast as needed.
    internal static unsafe class ExInfoHead
    {
        public static IntPtr s_head;

        public static ExInfo* Current => (ExInfo*)s_head;

        // Address of the static field — used by shellcode emitter to
        // patch a `mov r8, imm64` instruction with the head's address.
        public static byte** GetHeadAddress()
        {
            return (byte**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_head);
        }
    }
}
