using System.Runtime.InteropServices;
using OS.Hal;
using OS.Hal.Idt;
using OS.Kernel.Memory;
using OS.Kernel.Threading;
using OS.PAL.SharpOSHost;
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
            // Phase E9.b step 102 -- guard-page stack-overflow detection.
            // Each hosted/kernel thread (other than the UEFI boot thread)
            // reserves an unmapped 4 KiB page below StackBase. A #PF
            // whose CR2 lands inside that range = stack overflow on the
            // CURRENT thread. Report cleanly + halt; without this the
            // overflow would corrupt the adjacent GC-heap allocation
            // and surface as a mysterious downstream fault.
            if (frame->Vector == 14)
            {
                var curr = OS.Kernel.Threading.Scheduler.Current;
                if (curr != null && curr.GuardPage != null)
                {
                    ulong guardLo = (ulong)curr.GuardPage;
                    ulong guardHi = guardLo + 4096UL;
                    if (frame->Cr2 >= guardLo && frame->Cr2 < guardHi)
                    {
                        Console.Write("\r\n*** STACK OVERFLOW thread.Id=");
                        Console.WriteUIntRaw((uint)curr.Id);
                        Console.Write(" guard=[0x");
                        Console.WriteHexRaw(guardLo, 16);
                        Console.Write("..0x");
                        Console.WriteHexRaw(guardHi, 16);
                        Console.Write(") CR2=0x");
                        Console.WriteHexRaw(frame->Cr2, 16);
                        Console.Write(" RIP=0x");
                        Console.WriteHexRaw(frame->Rip, 16);
                        Console.Write(" ***\r\n");
                        // No clean recovery path yet (Phase E only halts);
                        // future work: kill thread, signal Join, return
                        // to scheduler.
                        while (true) { }
                    }
                }
            }

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
            Console.Write(" ERR=0x");
            Console.WriteHexRaw(frame->ErrorCode, 8);
            Log.EndLine();

            // For #PF (vec=14) — decode the page-fault error code so we can
            // tell write-vs-fetch / present-vs-not / NX-violation at a glance.
            //   bit 0: P  (1 = protection violation; 0 = not-present)
            //   bit 1: W  (1 = write; 0 = read)
            //   bit 2: U  (1 = user-mode access)
            //   bit 3: R  (reserved bit set in PTE)
            //   bit 4: I  (instruction fetch — NX violation when bit 0 also set)
            if (frame->Vector == 14)
            {
                Log.Begin(LogLevel.Info);
                ulong pfec = frame->ErrorCode;
                Console.Write("  PFEC: P=");
                Console.WriteUIntRaw((uint)((pfec >> 0) & 1));
                Console.Write(" W=");
                Console.WriteUIntRaw((uint)((pfec >> 1) & 1));
                Console.Write(" U=");
                Console.WriteUIntRaw((uint)((pfec >> 2) & 1));
                Console.Write(" R=");
                Console.WriteUIntRaw((uint)((pfec >> 3) & 1));
                Console.Write(" I=");
                Console.WriteUIntRaw((uint)((pfec >> 4) & 1));
                Console.Write("  (");
                if ((pfec & 0x10) != 0) Console.Write("instr-fetch ");
                else if ((pfec & 0x2) != 0) Console.Write("write ");
                else Console.Write("read ");
                if ((pfec & 0x1) == 0) Console.Write("not-present");
                else Console.Write("protection");
                Console.Write(")");
                Log.EndLine();

                // PTE-of-faulting-VA — tells whether mapping exists and what
                // protection bits are set (helps confirm NX-violation diagnosis).
                if (global::OS.Kernel.Paging.X64PageTable.TryGetKernelLeafPte(frame->Cr2, out ulong pte))
                {
                    Log.Begin(LogLevel.Info);
                    Console.Write("  PTE(CR2)=0x");
                    Console.WriteHexRaw(pte, 16);
                    Console.Write("  P=");
                    Console.WriteUIntRaw((uint)((pte >> 0) & 1));
                    Console.Write(" W=");
                    Console.WriteUIntRaw((uint)((pte >> 1) & 1));
                    Console.Write(" NX=");
                    Console.WriteUIntRaw((uint)((pte >> 63) & 1));
                    Log.EndLine();
                }
            }

            // Phase 6.1.b deep diagnostic — register snapshot + stack top.
            // Helps localize WHICH callsite + what register values were
            // in flight when fault hit.
            Log.Begin(LogLevel.Info);
            Console.Write("  RAX=0x"); Console.WriteHexRaw(frame->Rax, 16);
            Console.Write(" RBX=0x"); Console.WriteHexRaw(frame->Rbx, 16);
            Console.Write(" RCX=0x"); Console.WriteHexRaw(frame->Rcx, 16);
            Console.Write(" RDX=0x"); Console.WriteHexRaw(frame->Rdx, 16);
            Log.EndLine();
            Log.Begin(LogLevel.Info);
            Console.Write("  RSI=0x"); Console.WriteHexRaw(frame->Rsi, 16);
            Console.Write(" RDI=0x"); Console.WriteHexRaw(frame->Rdi, 16);
            Console.Write(" RBP=0x"); Console.WriteHexRaw(frame->Rbp, 16);
            Console.Write(" R8 =0x"); Console.WriteHexRaw(frame->R8,  16);
            Log.EndLine();
            Log.Begin(LogLevel.Info);
            Console.Write("  R9 =0x"); Console.WriteHexRaw(frame->R9,  16);
            Console.Write(" R10=0x"); Console.WriteHexRaw(frame->R10, 16);
            Console.Write(" R11=0x"); Console.WriteHexRaw(frame->R11, 16);
            Console.Write(" R12=0x"); Console.WriteHexRaw(frame->R12, 16);
            Log.EndLine();
            Log.Begin(LogLevel.Info);
            Console.Write("  R13=0x"); Console.WriteHexRaw(frame->R13, 16);
            Console.Write(" R14=0x"); Console.WriteHexRaw(frame->R14, 16);
            Console.Write(" R15=0x"); Console.WriteHexRaw(frame->R15, 16);
            Log.EndLine();
            // Stack top — first 32 qwords from RSP. For indirect-call
            // fault into BSS, multiple frames may be in zero-memory
            // chain. Look for first .text-range return address to find
            // real CoreCLR caller. Loaded image base ≈ 0xC1A9000;
            // .text spans ~0xC1AA000 to ~0xCE5E8DC.
            ulong* sp = (ulong*)frame->Rsp;
            if (sp != null)
            {
                for (int i = 0; i < 32; i++)
                {
                    ulong v = sp[i];
                    Log.Begin(LogLevel.Info);
                    Console.Write("  [RSP+0x"); Console.WriteHexRaw((ulong)(i * 8), 3);
                    Console.Write("] = 0x"); Console.WriteHexRaw(v, 16);
                    // Hint: tag values that look like .text return addresses.
                    if (v >= 0xC1A9000UL && v < 0xCE5F000UL) Console.Write("  <- .text");
                    Log.EndLine();
                }
            }

            // Sage-2 frontier discriminator (step 71). Cheapest single-run
            // signal that splits frontier A (stack overflow / no guard-page)
            // from frontier B (LCG/DynamicMethod). Pure C#, fault-time only —
            // no alloc, only pointer-walks segment headers. See work/sage2-*.
            FaultClassify(frame);

            // Re-enable interrupts before handing к managed dispatcher.
            // IDT entry was an interrupt gate (RFLAGS.IF cleared on entry).
            // Our control transfer through RhpCallCatchFunclet uses
            // mov rsp+jmp bypassing IRETQ, so IF would stay 0 в catch
            // funclet — disabling all hardware interrupts post-catch.
            // STI ensures catch runs с interrupts enabled, как if managed
            // code threw в normal context. X64Asm uses BootInfo.AsmExecBuffer
            // (EfiLoaderCode, R+X) to host the STI shellcode stub.
            OS.Hal.X64Asm.Sti();

            // step106: AV / #PF first try PAL SEH dispatcher — it knows
            // __C_specific_handler for C++ EX_TRY frames (e.g.
            // Object::ValidateInner's AVInRuntimeImplOkayHolder). Our
            // own DispatchEx.Dispatch uses CoffEhDecoder (NativeAOT EH
            // only) and silently steps past C++ frames, so a defensive
            // AV inside CoreCLR runtime leaks out as unhandled.
            // DispatchFromHwFault returns only when no handler matched —
            // then we fall through to the NativeAOT path which still
            // catches null-derefs in our own managed kernel code.
            if (frame->Vector == 13 || frame->Vector == 14)
            {
                // C# requires `fixed` scope to take address-of of a managed
                // static field. Our static fields live in .bss and never
                // move under SharpOS's non-moving GC, but the compiler
                // doesn't know that — `fixed` makes it happy at zero cost.
                fixed (ExceptionRecord* rec = &s_hwRec)
                fixed (Context*         ctx = &s_hwCtx)
                {
                    BuildHwExceptionRecord(rec, frame);
                    BuildContextFromInterruptFrame(ctx, frame);
                    SehDispatch.DispatchFromHwFault(rec, ctx);
                    // Returned — PAL SEH didn't find a handler. Fall through.
                }
            }

            // Hand off к managed dispatcher. Does not return on success.
            DispatchEx.Dispatch(exceptionPtr, &exInfo);

            // If we get here, Dispatch failed (unhandled). Halt.
            Console.Write("\r\n*** HwFaultBridge: Dispatch returned (unhandled HW exception) ***\r\n");
            while (true) { }
        }

        // step106: pre-allocated record + context for the PAL SEH path.
        // Avoids GcHeap alloc (which RaiseExceptionImpl uses) — the fault
        // handler must not depend on GcHeap state being sane (corruption
        // can be the very reason we're here). Single-threaded fault
        // handling means no re-entry concerns.
        private static ExceptionRecord s_hwRec;
        private static Context s_hwCtx;

        private static void BuildHwExceptionRecord(ExceptionRecord* rec, InterruptFrame* frame)
        {
            // Zero the struct (no Span here — kernel-aot, no Buffer.MemoryCopy).
            byte* p = (byte*)rec;
            for (int i = 0; i < sizeof(ExceptionRecord); i++) p[i] = 0;

            const uint STATUS_ACCESS_VIOLATION = 0xC0000005;
            rec->ExceptionCode = STATUS_ACCESS_VIOLATION;
            rec->ExceptionFlags = 0;
            rec->ExceptionAddress = (void*)frame->Rip;
            // Win64 #PF/#GP ExceptionInformation:
            //   [0] = 0 read / 1 write / 8 DEP-violation (instruction fetch)
            //   [1] = faulting VA (CR2 on #PF; we reuse RIP-target if available)
            rec->NumberParameters = 2;
            if (frame->Vector == 14)
            {
                // #PF — ErrorCode bit 1 = write, bit 4 = instruction fetch.
                ulong info0 = 0;
                if ((frame->ErrorCode & 0x10) != 0) info0 = 8;       // instr fetch (DEP)
                else if ((frame->ErrorCode & 0x02) != 0) info0 = 1;  // write
                rec->ExceptionInformation[0] = info0;
                rec->ExceptionInformation[1] = frame->Cr2;
            }
            else
            {
                // #GP — no CR2; report read-style access of unknown target.
                rec->ExceptionInformation[0] = 0;
                rec->ExceptionInformation[1] = 0;
            }
        }

        private static void BuildContextFromInterruptFrame(Context* ctx, InterruptFrame* frame)
        {
            // Zero the entire 1232-byte struct.
            byte* p = (byte*)ctx;
            for (int i = 0; i < sizeof(Context); i++) p[i] = 0;

            ctx->ContextFlags = Context.CONTEXT_FULL;
            ctx->SegCs = (ushort)frame->Cs;
            ctx->SegSs = (ushort)frame->Ss;
            ctx->EFlags = (uint)frame->Rflags;

            ctx->Rax = frame->Rax;
            ctx->Rcx = frame->Rcx;
            ctx->Rdx = frame->Rdx;
            ctx->Rbx = frame->Rbx;
            ctx->Rsp = frame->Rsp;
            ctx->Rbp = frame->Rbp;
            ctx->Rsi = frame->Rsi;
            ctx->Rdi = frame->Rdi;
            ctx->R8  = frame->R8;
            ctx->R9  = frame->R9;
            ctx->R10 = frame->R10;
            ctx->R11 = frame->R11;
            ctx->R12 = frame->R12;
            ctx->R13 = frame->R13;
            ctx->R14 = frame->R14;
            ctx->R15 = frame->R15;
            ctx->Rip = frame->Rip;
        }

        // ---- Sage-2 frontier discriminator (step 71) ------------------------
        //
        // The two open frontiers share ONE symptom (an ASCII qword sits where
        // a pointer/MethodTable was expected → #GP) but, per sage-2, almost
        // certainly have TWO roots:
        //   A  recursion/checked  — kernel stack has no guard page; deep
        //                            recursion runs RSP down into adjacent
        //                            memory / the GC heap → object headers
        //                            read as strings → MethodTable::SanityCheck.
        //   B  System.Text.Json   — LCG / DynamicMethod / dynamic-IL path
        //                            (ReflectionEmitCachingMemberAccessor).
        //
        // The decisive cheap test that needs NO recorded stack bounds and NO
        // stack-size change: is the faulting RSP literally INSIDE a managed GC
        // heap segment ("stack-bottom-falls-into-heap")? Plus the ASCII-spray
        // run length and a RAX/RCX classifier (canonical / ASCII / heap /
        // image). One run, fires on whichever frontier crashes first.

        private static bool IsCanonical(ulong v)
        {
            ulong hi = v >> 47;            // bits 63..47 must all equal bit 47
            return hi == 0UL || hi == 0x1FFFFUL;
        }

        private static bool IsAsciiQword(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                byte b = (byte)(v >> (i * 8));
                if (b < 0x20 || b > 0x7E) return false;
            }
            return true;
        }

        private static void ClassifyWord(string name, ulong v)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("  ["); Console.Write(name); Console.Write("]=0x");
            Console.WriteHexRaw(v, 16);
            if (!IsCanonical(v)) Console.Write("  non-canonical");
            if (IsAsciiQword(v))
            {
                Console.Write("  ascii=\"");
                for (int i = 0; i < 8; i++) Console.WriteChar((char)(byte)(v >> (i * 8)));
                Console.Write("\"");
            }
            if (GcHeap.IsInitialized &&
                GcHeap.FindSegmentContaining((nint)v) != null)
                Console.Write("  in-GC-heap");
            ulong ib = (ulong)CoffRuntimeFunctionTable.ImageBase;
            if (ib != 0 && v >= ib && v < ib + 0x10000000UL)
                Console.Write("  in-image");
            Log.EndLine();
        }

        private static void FaultClassify(InterruptFrame* frame)
        {
            ClassifyWord("RAX", frame->Rax);
            ClassifyWord("RCX", frame->Rcx);


            // BigStack.RunOn swaps RSP into a dedicated mapped buffer.
            // Its active bounds are authoritative; do not classify by the
            // larger UEFI/physical region that happens to contain the
            // buffer. If RSP is outside those bounds and inside GcHeap, that
            // is a real stack/heap overlap or overflow signal.
            ulong bsLo = 0, bsHi = 0;
            bool rspInBigStack = BigStack.TryGetActiveBounds(frame->Rsp, out bsLo, out bsHi);

            // Frontier-A discriminator: faulting RSP inside a managed heap
            // segment is the literal "stack ran into the heap" — a near-
            // certain stack-overflow verdict that needs no stack bounds.
            GcSegmentHeader* rspSeg = GcHeap.IsInitialized
                ? GcHeap.FindSegmentContaining((nint)frame->Rsp)
                : null;
            bool rspInHeap = rspSeg != null;
            Log.Begin(LogLevel.Info);
            if (rspInBigStack)
            {
                Console.Write("  [BIGSTACK] RSP inside active BigStack buffer [0x");
                Console.WriteHex(bsLo);
                Console.Write("..0x");
                Console.WriteHex(bsHi);
                Console.Write(") — CoreCLR-hosted run, RSP swapped by BigStack.RunOn");
            }
            else if (rspInHeap)
                Console.Write("  [SO-SUSPECT] RSP is INSIDE a GC heap segment "
                    + "— stack descended into managed heap (frontier A: SO)");
            else
                Console.Write("  RSP not in GC heap (frontier-A SO not confirmed by this signal)");
            Log.EndLine();

            // Step104 discriminator: print current thread stack bounds AND
            // the GC segment that contains RSP (if any), so we can tell
            // whether RSP fell below StackBase (true overflow into adjacent
            // memory) vs RSP is normal but stack/heap address ranges OVERLAP
            // (allocation bug — stack and GC segment reserved at same VA).
            Thread? curr = Scheduler.Current;
            if (curr != null)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  [STK] thread Id=");
                Console.WriteUIntRaw((uint)curr.Id);
                Console.Write(" StackBase=0x"); Console.WriteHex((ulong)curr.StackBase);
                Console.Write(" StackTop=0x");  Console.WriteHex((ulong)curr.StackTop);
                Console.Write(" StackBytes=0x"); Console.WriteHex((ulong)curr.StackBytes);
                Console.Write(" GuardPage=0x"); Console.WriteHex((ulong)curr.GuardPage);
                Log.EndLine();

                ulong rsp = frame->Rsp;
                ulong sbase = (ulong)curr.StackBase;
                ulong stop  = (ulong)curr.StackTop;
                Log.Begin(LogLevel.Info);
                if (rspInBigStack)
                {
                    Console.Write("  [STK] wrapper bounds shown above — execution actually on BigStack");
                }
                else if (sbase == 0 || stop == 0)
                {
                    Console.Write("  [STK] thread has no owned stack (boot/wrapped thread)");
                }
                else if (rsp < sbase)
                {
                    Console.Write("  [STK] RSP BELOW StackBase by 0x");
                    Console.WriteHex(sbase - rsp);
                }
                else if (rsp >= stop)
                {
                    Console.Write("  [STK] RSP ABOVE StackTop by 0x");
                    Console.WriteHex(rsp - stop);
                }
                else
                {
                    Console.Write("  [STK] RSP within bounds, used=0x");
                    Console.WriteHex(stop - rsp);
                    Console.Write(" of 0x");
                    Console.WriteHex(stop - sbase);
                }
                Log.EndLine();
            }
            else
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  [STK] Scheduler.Current == null (no thread context)");
                Log.EndLine();
            }

            if (rspSeg != null)
            {
                Log.Begin(LogLevel.Info);
                Console.Write("  [GC-SEG] RSP-containing segment: Start=0x");
                Console.WriteHex((ulong)rspSeg->Start);
                Console.Write(" End=0x");
                Console.WriteHex((ulong)rspSeg->End);
                Console.Write(" size=0x");
                Console.WriteHex((ulong)(rspSeg->End - rspSeg->Start));
                Log.EndLine();
            }

            // ASCII-spray extent: a long contiguous run of one ASCII qword up
            // the stack is the overrun/spray fingerprint (frontier A). Scan is
            // upward only (mapped, contiguous with the dump above) — never
            // downward (may be unmapped → recursive fault).
            ulong* sp = (ulong*)frame->Rsp;
            if (sp != null && IsAsciiQword(frame->Rax))
            {
                ulong pat = frame->Rax;
                int run = 0;
                while (run < 4096 && sp[run] == pat) run++;   // contiguous from RSP
                int total = 0;
                for (int i = 0; i < 4096; i++) if (sp[i] == pat) total++;
                Log.Begin(LogLevel.Info);
                Console.Write("  [SPRAY] RAX-pattern qwords: run-from-RSP=");
                Console.WriteUIntRaw((uint)run);
                Console.Write(" total-in-32KB=");
                Console.WriteUIntRaw((uint)total);
                Console.Write(" (of 4096)");
                Log.EndLine();
            }
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
