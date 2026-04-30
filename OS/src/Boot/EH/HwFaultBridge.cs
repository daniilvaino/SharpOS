using System.Runtime.InteropServices;
using OS.Hal;
using OS.Hal.Idt;
using SharpOS.Std.NoRuntime;

namespace OS.Boot.EH
{
    // Phase 1 step 10 — bridges hardware exceptions (CPU traps via IDT)
    // to managed EH dispatch.
    //
    // When the CPU raises a fault (e.g., #PF on null deref), the IDT entry
    // trampoline saves register state into an InterruptFrame on the kernel
    // stack and calls into managed code. For supported vectors we build
    // PAL_LIMITED_CONTEXT + ExInfo + exception object, then call DispatchEx.
    // DispatchEx walks frames from the faulting RIP, finds a catching
    // try/catch in user code, and transfers via RhpCallCatchFunclet.
    //
    // Unsupported vectors fall through to PanicDump (legacy path).
    //
    // Stock NativeAOT does this via RhpThrowHwEx asm thunk; we do it inline
    // in C# from the IDT dispatcher because the trap frame is already
    // captured by our trampoline в InterruptFrame format.
    //
    // CRITICAL: interrupts are disabled by the interrupt gate (RFLAGS.IF=0).
    // Our control transfer via mov rsp+jmp в RhpCallCatchFunclet bypasses
    // IRETQ, so IF stays 0 в the catch funclet. For Phase 1 step 10 the
    // test path doesn't need IRQ delivery in the catch — we leave IF cleared
    // и accept that interrupts won't fire after a HW exception until next
    // legitimate IRETQ. Step 11 territory or later: explicit STI before
    // dispatch.
    internal static unsafe class HwFaultBridge
    {
        // Vector numbers we know how to convert to managed exceptions.
        private const int VecDivideByZero       = 0x00;  // #DE
        private const int VecInvalidOpcode      = 0x06;  // #UD
        private const int VecGeneralProtection  = 0x0D;  // #GP
        private const int VecPageFault          = 0x0E;  // #PF

        // Returns true if this vector should be converted to a managed
        // exception. False means caller should panic-dump.
        public static bool IsSupported(int vector)
        {
            switch (vector)
            {
                case VecDivideByZero:
                case VecPageFault:
                case VecGeneralProtection:
                    return true;
                default:
                    return false;
            }
        }

        // Top-level entry from Idt.Dispatch. Builds PAL + ExInfo, resolves
        // exception object from vector, calls DispatchEx. Does NOT return —
        // either Dispatch transfers control via catch funclet (success) or
        // halts on unhandled (per our current Dispatch behavior).
        public static void DispatchTrap(InterruptFrame* frame)
        {
            // Allocate context + ExInfo on local stack. They live для
            // lifetime of dispatch (which never returns normally).
            PalLimitedContext pal = default;
            ExInfo exInfo = default;

            BuildPal(&pal, frame);

            // Resolve managed exception object для this vector.
            object exObj = ResolveException((int)frame->Vector, frame);
            if (exObj == null)
            {
                Console.Write("\r\n*** HwFaultBridge: no exception для vector ");
                Console.WriteUIntRaw((uint)frame->Vector);
                Console.Write(" ***\r\n");
                while (true) { }
            }

            // Reinterpret object reference as raw pointer for shellcode-style
            // ABI compatibility. NativeAOT object refs are just pointers
            // to GcMethodTable* in first slot.
            byte* exceptionPtr = null;
            *(object*)&exceptionPtr = exObj;

            // Build ExInfo. Kind = HardwareFault.
            exInfo.PrevExInfo = (ExInfo*)ExInfoHead.s_head;
            exInfo.ExContext = &pal;
            exInfo.Exception = (ulong)(nuint)exceptionPtr;
            exInfo.Kind = ExInfo.KindHardwareFault;
            exInfo.PassNumber = 1;
            exInfo.IdxCurClause = ExInfo.MaxTryRegionIdx;

            // Link into head chain.
            ExInfoHead.s_head = (System.IntPtr)(&exInfo);

            Log.Begin(LogLevel.Info);
            Console.Write("HW fault: vec=");
            Console.WriteUIntRaw((uint)frame->Vector);
            Console.Write(" RIP=0x");
            Console.WriteHexRaw(frame->Rip, 16);
            Console.Write(" RSP=0x");
            Console.WriteHexRaw(frame->Rsp, 16);
            Console.Write(" CR2=0x");
            Console.WriteHexRaw(frame->Cr2, 16);
            Log.EndLine();

            // Re-enable interrupts before handing к managed dispatcher.
            // IDT entry was an interrupt gate (RFLAGS.IF cleared on entry).
            // Our control transfer through RhpCallCatchFunclet uses
            // mov rsp+jmp bypassing IRETQ, so IF would stay 0 в catch
            // funclet — disabling all hardware interrupts post-catch.
            // STI ensures catch runs с interrupts enabled, как if managed
            // code threw в normal context. X64Asm uses BootInfo.AsmExecBuffer
            // (EfiLoaderCode, R+X) to host the STI shellcode stub.
            OS.Hal.X64Asm.Sti();

            // Hand off к managed dispatcher. Does not return on success.
            DispatchEx.Dispatch(exceptionPtr, &exInfo);

            // If we get here, Dispatch failed (unhandled). Halt.
            Console.Write("\r\n*** HwFaultBridge: Dispatch returned (unhandled HW exception) ***\r\n");
            while (true) { }
        }

        // Populate PAL_LIMITED_CONTEXT from interrupt frame. Captures the
        // pre-fault register state так что StackFrameIterator может walk
        // starting from faulting frame.
        private static void BuildPal(PalLimitedContext* pal, InterruptFrame* frame)
        {
            pal->IP   = frame->Rip;
            pal->Rsp  = frame->Rsp;
            pal->Rbp  = frame->Rbp;
            pal->Rdi  = frame->Rdi;
            pal->Rsi  = frame->Rsi;
            pal->Rbx  = frame->Rbx;
            pal->R12  = frame->R12;
            pal->R13  = frame->R13;
            pal->R14  = frame->R14;
            pal->R15  = frame->R15;
            // Volatile regs (Rax/Rcx/Rdx/R8-R11) are reflected for
            // diagnostics but not used in unwinding (only nonvols matter).
            pal->Rax  = frame->Rax;
            // Xmm registers — InterruptFrame doesn't capture them; leave 0.
            // For Phase 1 step 10 tests don't use xmm в throwing path.
        }

        // Map vector → managed exception object. Allocates the exception
        // на managed heap (GC alloc).
        private static object ResolveException(int vector, InterruptFrame* frame)
        {
            switch (vector)
            {
                case VecDivideByZero:
                    return new System.DivideByZeroException();
                case VecPageFault:
                    // CLR tradition: #PF on low addresses (null + small offsets)
                    // → NullReferenceException; higher unmapped addresses →
                    // AccessViolationException. Boundary 0x10000 covers typical
                    // `obj.field` где compiler emits load с small offset from
                    // null base.
                    if (frame->Cr2 < 0x10000)
                        return new System.NullReferenceException();
                    return new System.AccessViolationException();
                case VecGeneralProtection:
                    // #GP — non-canonical address writes/reads. Доступ к памяти
                    // вне canonical range всегда AccessViolationException
                    // (no null-deref path applies).
                    return new System.AccessViolationException();
                default:
                    return null;
            }
        }
    }
}
