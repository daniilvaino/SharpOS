using System.Runtime.InteropServices;

namespace OS.Hal.Idt
{
    // Snapshot of register state at the moment of an exception.
    //
    // Stack layout (high addresses → low) when our common stub calls the
    // managed dispatcher. CPU pushes are at the top, our trampoline pushes
    // grow downward, and the dispatcher receives a pointer to the lowest
    // saved value.
    //
    //   high address
    //     SS                ← CPU pushed (always in long mode)
    //     RSP
    //     RFLAGS
    //     CS
    //     RIP
    //     ErrorCode         ← real value if vector has one, dummy 0 otherwise
    //     Vector            ← pushed by per-vector entry stub
    //     R15..R8           ← pushed by common stub (8 regs)
    //     RBP, RDI, RSI, RBX, RDX, RCX, RAX
    //     CR2               ← captured by common stub (page-fault address)
    //   low address  ← rsp at dispatcher call, rdi/rcx points here
    //
    // The struct mirrors that layout exactly so the dispatcher can read it
    // through a single typed pointer without per-field arithmetic.
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterruptFrame
    {
        // Pushed by common stub (low → high in struct order = high → low on stack)
        public ulong Cr2;
        public ulong Rax;
        public ulong Rcx;
        public ulong Rdx;
        public ulong Rbx;
        public ulong Rsi;
        public ulong Rdi;
        public ulong Rbp;
        public ulong R8;
        public ulong R9;
        public ulong R10;
        public ulong R11;
        public ulong R12;
        public ulong R13;
        public ulong R14;
        public ulong R15;

        // Pushed by per-vector entry stub
        public ulong Vector;
        public ulong ErrorCode;

        // Pushed by CPU on interrupt
        public ulong Rip;
        public ulong Cs;
        public ulong Rflags;
        public ulong Rsp;
        public ulong Ss;
    }
}
