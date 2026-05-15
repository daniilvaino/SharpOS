using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // Windows x64 SEH data structures, used by Rtl{Lookup,Virtual}Unwind,
    // __CxxFrameHandler3, _CxxThrowException и т.п. Layout must match
    // Microsoft's exactly — CoreCLR's MSVC-compiled .obj files emit and
    // consume these by raw offset.
    //
    // Sources: winnt.h (CONTEXT, EXCEPTION_RECORD, UNWIND_INFO opcodes),
    //          ntstatus.h (exception codes), MSVC ehdata.h (FuncInfo,
    //          UnwindMapEntry, TryBlockMapEntry, HandlerType, CatchableType).

    // EXCEPTION_RECORD — 152 bytes. Passed to personality routines and
    // _CxxThrowException uses it to encode the thrown C++ object.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct ExceptionRecord
    {
        public const int MAXIMUM_PARAMETERS = 15;

        // C++ throw exception code (chosen to be non-fatal-looking).
        public const uint EH_EXCEPTION_NUMBER     = 0xE06D7363;  // "msc" + 0xE0 ms-c-throw
        public const uint EH_MAGIC_NUMBER1        = 0x19930520;
        public const uint EH_MAGIC_NUMBER2        = 0x19930521;
        public const uint EH_MAGIC_NUMBER3        = 0x19930522;

        public const uint EXCEPTION_NONCONTINUABLE = 0x01;
        public const uint EXCEPTION_UNWINDING     = 0x02;
        public const uint EXCEPTION_EXIT_UNWIND   = 0x04;
        public const uint EXCEPTION_STACK_INVALID = 0x08;
        public const uint EXCEPTION_NESTED_CALL   = 0x10;
        public const uint EXCEPTION_TARGET_UNWIND = 0x20;
        public const uint EXCEPTION_COLLIDED_UNWIND = 0x40;

        public uint  ExceptionCode;
        public uint  ExceptionFlags;
        public ExceptionRecord* ExceptionRecord_;     // chain
        public void* ExceptionAddress;
        public uint  NumberParameters;
        public uint  _alignPad;
        // 15 ULONG_PTR params follow inline.
        public fixed ulong ExceptionInformation[15];
    }

    // CONTEXT — 1232 bytes на x64. Layout per winnt.h (AMD64).
    // Offsets are critical; RtlCaptureContext writes by offset.
    [StructLayout(LayoutKind.Explicit, Size = 1232)]
    internal unsafe struct Context
    {
        public const uint CONTEXT_AMD64           = 0x00100000;
        public const uint CONTEXT_CONTROL         = CONTEXT_AMD64 | 0x1;
        public const uint CONTEXT_INTEGER         = CONTEXT_AMD64 | 0x2;
        public const uint CONTEXT_SEGMENTS        = CONTEXT_AMD64 | 0x4;
        public const uint CONTEXT_FLOATING_POINT  = CONTEXT_AMD64 | 0x8;
        public const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_AMD64 | 0x10;
        public const uint CONTEXT_FULL            = CONTEXT_CONTROL | CONTEXT_INTEGER | CONTEXT_FLOATING_POINT;
        public const uint CONTEXT_ALL             = CONTEXT_FULL | CONTEXT_SEGMENTS | CONTEXT_DEBUG_REGISTERS;

        // Argument home space (caller-allocated, used for shadow store).
        [FieldOffset(0x000)] public ulong P1Home;
        [FieldOffset(0x008)] public ulong P2Home;
        [FieldOffset(0x010)] public ulong P3Home;
        [FieldOffset(0x018)] public ulong P4Home;
        [FieldOffset(0x020)] public ulong P5Home;
        [FieldOffset(0x028)] public ulong P6Home;

        // Control flags.
        [FieldOffset(0x030)] public uint ContextFlags;
        [FieldOffset(0x034)] public uint MxCsr;

        // Segment registers.
        [FieldOffset(0x038)] public ushort SegCs;
        [FieldOffset(0x03A)] public ushort SegDs;
        [FieldOffset(0x03C)] public ushort SegEs;
        [FieldOffset(0x03E)] public ushort SegFs;
        [FieldOffset(0x040)] public ushort SegGs;
        [FieldOffset(0x042)] public ushort SegSs;
        [FieldOffset(0x044)] public uint   EFlags;

        // Debug registers.
        [FieldOffset(0x048)] public ulong Dr0;
        [FieldOffset(0x050)] public ulong Dr1;
        [FieldOffset(0x058)] public ulong Dr2;
        [FieldOffset(0x060)] public ulong Dr3;
        [FieldOffset(0x068)] public ulong Dr6;
        [FieldOffset(0x070)] public ulong Dr7;

        // Integer registers.
        [FieldOffset(0x078)] public ulong Rax;
        [FieldOffset(0x080)] public ulong Rcx;
        [FieldOffset(0x088)] public ulong Rdx;
        [FieldOffset(0x090)] public ulong Rbx;
        [FieldOffset(0x098)] public ulong Rsp;
        [FieldOffset(0x0A0)] public ulong Rbp;
        [FieldOffset(0x0A8)] public ulong Rsi;
        [FieldOffset(0x0B0)] public ulong Rdi;
        [FieldOffset(0x0B8)] public ulong R8;
        [FieldOffset(0x0C0)] public ulong R9;
        [FieldOffset(0x0C8)] public ulong R10;
        [FieldOffset(0x0D0)] public ulong R11;
        [FieldOffset(0x0D8)] public ulong R12;
        [FieldOffset(0x0E0)] public ulong R13;
        [FieldOffset(0x0E8)] public ulong R14;
        [FieldOffset(0x0F0)] public ulong R15;

        [FieldOffset(0x0F8)] public ulong Rip;

        // FP/XMM state lives at offset 0x100. We don't unwind those for
        // the C++ EH path (volatile across calls); skip exact layout.
    }

    // RUNTIME_FUNCTION — 12 bytes, .pdata entry. Already mirrored в
    // OS.Boot.EH.RuntimeFunction; this is a duplicate definition keeping
    // SEH module self-contained.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RuntimeFunction
    {
        public uint BeginAddress;
        public uint EndAddress;
        public uint UnwindInfoAddress;
    }

    // DISPATCHER_CONTEXT — passed to language-specific handler. Personality
    // routines read это to know which function they're processing and how
    // unwinding has progressed.
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct DispatcherContext
    {
        public ulong            ControlPc;
        public ulong            ImageBase;
        public RuntimeFunction* FunctionEntry;
        public ulong            EstablisherFrame;
        public ulong            TargetIp;          // for unwinds: where to resume
        public Context*         ContextRecord;
        public void*            LanguageHandler;
        public void*            HandlerData;       // pointer to language-specific data after UNWIND_INFO
        public void*            HistoryTable;      // unwind history (optional, may be null)
        public uint             ScopeIndex;
        public uint             Fill0;
    }

    // UNWIND_INFO header bits.
    internal static class UnwindFlags
    {
        public const byte UNW_FLAG_NHANDLER = 0x0;
        public const byte UNW_FLAG_EHANDLER = 0x1;  // function has exception handler
        public const byte UNW_FLAG_UHANDLER = 0x2;  // function has termination/unwind handler
        public const byte UNW_FLAG_CHAININFO = 0x4; // info chains to a parent (fragment)
    }

    // UNWIND_CODE opcodes (low 4 bits of byte 1 of each 2-byte code).
    internal static class UnwindOp
    {
        public const int UWOP_PUSH_NONVOL     = 0;
        public const int UWOP_ALLOC_LARGE     = 1;
        public const int UWOP_ALLOC_SMALL     = 2;
        public const int UWOP_SET_FPREG       = 3;
        public const int UWOP_SAVE_NONVOL     = 4;
        public const int UWOP_SAVE_NONVOL_FAR = 5;
        public const int UWOP_EPILOG          = 6;   // win8+: epilogue marker (UNWIND_INFO v2)
        public const int UWOP_SPARE_CODE      = 7;   // unused
        public const int UWOP_SAVE_XMM128     = 8;
        public const int UWOP_SAVE_XMM128_FAR = 9;
        public const int UWOP_PUSH_MACHFRAME  = 10;
    }

    // Exception disposition values returned by personality routines.
    internal enum ExceptionDisposition
    {
        ExceptionContinueExecution = 0,
        ExceptionContinueSearch    = 1,
        ExceptionNestedException   = 2,
        ExceptionCollidedUnwind    = 3,
    }
}
