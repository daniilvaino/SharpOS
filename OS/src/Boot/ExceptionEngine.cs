using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using OS.Hal;

namespace OS.Boot
{
    // Phase 1 minimal exception path — `throw new X("msg")` now produces a
    // readable panic dump (type + message) instead of an opaque halt loop.
    //
    // What this provides:
    //   - RhpThrowEx(object) — entry point ILC emits for IL `throw`. We
    //     print the exception's type and message via virtual dispatch on
    //     System.Exception, then halt. No stack unwinding, no catch.
    //   - Classlib hooks Test.CoreLib provides (GetRuntimeException,
    //     FailFast, AppendExceptionStackFrame, OnFirstChanceException,
    //     OnUnhandledException) — most are no-ops or final-halt.
    //
    // What this does NOT provide:
    //   - try / catch / finally semantics (requires full StackFrameIterator
    //     + DispatchEx + per-method EH clause table walker — 3-6 months
    //     focused work; deferred to either a future longjmp milestone or
    //     CoreCLR fork in Phase 6).
    //   - Hardware-fault → managed-exception bridging (vector 14 #PF
    //     would map to NullReferenceException via RhpThrowHwEx). Until
    //     full unwinder is in place, hardware faults stay in our IDT
    //     PanicDump path from step 35.
    //
    // The explicit `throw new X(...)` from BCL-ported code (StringBuilder,
    // collections, Unsafe.AsPointer with bad input, etc.) now logs the
    // exception type+message before halting — same effect as Panic.Fail
    // but reachable through normal C# `throw` syntax.
    internal static class ExceptionEngine
    {
        // RhpThrowEx moved to OS/src/Boot/EH/ThrowExStub.cs (Phase 1
        // step 5.1). Body is patched by ThrowExPatcher with a 186-byte
        // shellcode that builds PAL_LIMITED_CONTEXT + ExInfo on stack
        // and tail-calls into managed RhpTest_ThrowIngress (this file).

        // Phase 1 step 5.1 — managed seam where the throw shellcode
        // lands after building ExInfo. Logs the snapshot's invariants
        // (kind/passNumber/idxCurClause/IP/exception), confirms head
        // chain, then halts. This is intentionally a halt — the full
        // dispatch (DispatchEx + funclet calling) lands later in 5.6.
        [RuntimeExport("RhpTest_ThrowIngress")]
        [System.Runtime.InteropServices.UnmanagedCallersOnly(EntryPoint = "RhpTest_ThrowIngress")]
        public static unsafe void RhpTest_ThrowIngress(byte* exceptionPtr, OS.Boot.EH.ExInfo* exInfo)
        {
            // [UnmanagedCallersOnly] doesn't allow `object` parameter,
            // so we accept the raw pointer (RCX from shellcode) and
            // reinterpret the 8-byte slot as a managed reference.
            object exception = null;
            *(byte**)&exception = exceptionPtr;

            Console.Write("\r\n*** RhpTest_ThrowIngress (5.1) ***\r\n");

            Console.Write("  exception type: ");
            PrintExceptionInfo(exception);

            Console.Write("  exInfo=0x");
            Console.WriteHexRaw((ulong)(nuint)exInfo, 16);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(long)OS.Boot.EH.ExInfoHead.s_head, 16);
            Console.Write("\r\n");

            Console.Write("  pass=");
            Console.WriteUIntRaw(exInfo->PassNumber);
            Console.Write(" kind=");
            Console.WriteUIntRaw(exInfo->Kind);
            Console.Write(" idxCurClause=0x");
            Console.WriteHexRaw(exInfo->IdxCurClause, 8);
            Console.Write("\r\n");

            Console.Write("  prevExInfo=0x");
            Console.WriteHexRaw((ulong)(nuint)exInfo->PrevExInfo, 16);
            Console.Write(" exContext=0x");
            Console.WriteHexRaw((ulong)(nuint)exInfo->ExContext, 16);
            Console.Write("\r\n");

            if (exInfo->ExContext != null)
            {
                Console.Write("  ctx.IP=0x");
                Console.WriteHexRaw(exInfo->ExContext->IP, 16);
                Console.Write(" ctx.Rsp=0x");
                Console.WriteHexRaw(exInfo->ExContext->Rsp, 16);
                Console.Write("\r\n");
            }

            // Phase 1 step 5.2 — initialise the embedded StackFrameIterator
            // from the captured PAL_LIMITED_CONTEXT and log its
            // invariants. Stock NativeAOT does this from DispatchEx
            // (managed) before walking — we do it inline here for the
            // intermediate ingress probe.
            if (exInfo->ExContext != null)
            {
                Console.Write("\r\n*** RhpTest_SfiInit (5.2) ***\r\n");

                OS.Boot.EH.StackFrameIteratorOps.Init(
                    &exInfo->FrameIter, exInfo->ExContext);

                Console.Write("  sfi.controlPC=0x");
                Console.WriteHexRaw(exInfo->FrameIter.ControlPC, 16);
                Console.Write(" sfi.originalPC=0x");
                Console.WriteHexRaw(exInfo->FrameIter.OriginalControlPC, 16);
                Console.Write("\r\n");

                Console.Write("  sfi.framePointer=0x");
                Console.WriteHexRaw(exInfo->FrameIter.FramePointer, 16);
                Console.Write(" sfi.SP=0x");
                Console.WriteHexRaw(exInfo->FrameIter.RegDisplay.SP, 16);
                Console.Write("\r\n");

                Console.Write("  regSet: pRbx=0x");
                Console.WriteHexRaw((ulong)(nuint)exInfo->FrameIter.RegDisplay.pRbx, 16);
                Console.Write(" pRbp=0x");
                Console.WriteHexRaw((ulong)(nuint)exInfo->FrameIter.RegDisplay.pRbp, 16);
                Console.Write(" pR12=0x");
                Console.WriteHexRaw((ulong)(nuint)exInfo->FrameIter.RegDisplay.pR12, 16);
                Console.Write("\r\n");

                // Sanity: register pointers should point INTO the PAL
                // struct on stack (callee-owned). PAL begins at
                // exContext, ends at exContext + sizeof(PAL) = +0x100.
                ulong palStart = (ulong)(nuint)exInfo->ExContext;
                ulong palEnd = palStart + (ulong)OS.Boot.EH.PalLimitedContext.Size;
                ulong pRbxAddr = (ulong)(nuint)exInfo->FrameIter.RegDisplay.pRbx;
                bool inPal = pRbxAddr >= palStart && pRbxAddr < palEnd;
                Console.Write("  pRbx in PAL? ");
                Console.Write(inPal ? "yes" : "no");
                Console.Write("\r\n");

                // Phase 1 step 5.4 — replace 5.3 ad-hoc walk-up with
                // production FindFirstPassHandler that walks frames AND
                // matches typed clauses against exception type via class
                // hierarchy. Filter clauses skipped (5.5 territory).
                // RhpCallCatchFunclet still halt-stub — we only verify
                // first-pass decision is correct.
                Console.Write("\r\n*** FindFirstPassHandler (5.4) ***\r\n");

                // Exception object's first 8 bytes = MT pointer.
                SharpOS.Std.NoRuntime.GcMethodTable* exType = null;
                if (exceptionPtr != null)
                    exType = *(SharpOS.Std.NoRuntime.GcMethodTable**)exceptionPtr;

                Console.Write("  exType=0x");
                Console.WriteHexRaw((ulong)(nuint)exType, 16);
                Console.Write("\r\n");

                OS.Boot.EH.DispatchEx.FirstPassResult fp =
                    OS.Boot.EH.DispatchEx.FindFirstPassHandler(exType, &exInfo->FrameIter);

                if (fp.Found)
                {
                    Console.Write("  MATCH: framesWalked=");
                    Console.WriteUIntRaw((uint)fp.FramesWalked);
                    Console.Write(" handler=0x");
                    Console.WriteHexRaw((ulong)(nuint)fp.HandlerAddress, 16);
                    Console.Write(" idxCurClause=");
                    Console.WriteUIntRaw(fp.IdxCurClause);
                    Console.Write("\r\n");
                    Console.Write("  methodStart=0x");
                    Console.WriteHexRaw((ulong)(nuint)fp.MethodStart, 16);
                    Console.Write(" codeOffset=0x");
                    Console.WriteHexRaw(fp.CodeOffset, 8);
                    Console.Write("\r\n");
                }
                else
                {
                    Console.Write("  NO MATCH after framesWalked=");
                    Console.WriteUIntRaw((uint)fp.FramesWalked);
                    Console.Write("\r\n");
                }
            }

            Console.Write("*** halting (5.4 first-pass probe) ***\r\n");
            while (true) { }
        }

        // Some BCL paths call `throw;` (rethrow). ILC emits RhpRethrow.
        [RuntimeExport("RhpRethrow")]
        public static void RhpRethrow(object exception)
        {
            Console.Write("\r\n*** UNHANDLED EXCEPTION (rethrow) ***\r\n");
            PrintExceptionInfo(exception);
            Console.Write("*** halting ***\r\n");
            while (true) { }
        }

        // Hardware fault → managed exception bridge. Without an unwinder
        // we never actually invoke this from IDT — IDT goes through
        // PanicDump directly. Stub kept so ILC linkage doesn't break if
        // anything references it.
        [RuntimeExport("RhpThrowHwEx")]
        public static void RhpThrowHwEx(uint exceptionCode, IntPtr faultingIP)
        {
            Console.Write("\r\n*** HARDWARE EXCEPTION (managed dispatch) ***\r\n");
            Console.Write("code=0x");
            Console.WriteHexRaw(exceptionCode, 8);
            Console.Write(" RIP=0x");
            Console.WriteHexRaw((ulong)faultingIP, 16);
            Console.Write("\r\n*** halting ***\r\n");
            while (true) { }
        }

        // Classlib hooks — runtime calls these during EH dispatch. Most
        // are no-ops in our minimal model; FailFast is the terminal halt.

        [RuntimeExport("GetRuntimeException")]
        public static Exception GetRuntimeException(System.Runtime.ExceptionIDs id)
        {
            // Classlib-provided "translate runtime fault to managed
            // exception object" helper. Runtime passes one of the BCL's
            // ExceptionIDs values; we instantiate the concrete type it
            // expects. This is what makes `int* p = null; *p = 0;` →
            // NullReferenceException (rather than generic Exception).
            //
            // Mirror of Test.CoreLib's RuntimeExceptionHelpers.cs. The
            // wrap-in-try/catch pattern (so this never leaks an exception
            // back into the dispatcher) will matter once the unwinder
            // lands; for step 1 the throws here just halt.
            switch (id)
            {
                case System.Runtime.ExceptionIDs.OutOfMemory:
                    return new OutOfMemoryException();
                case System.Runtime.ExceptionIDs.Arithmetic:
                    return new ArithmeticException();
                case System.Runtime.ExceptionIDs.ArrayTypeMismatch:
                    return new ArrayTypeMismatchException();
                case System.Runtime.ExceptionIDs.DivideByZero:
                    return new DivideByZeroException();
                case System.Runtime.ExceptionIDs.IndexOutOfRange:
                    return new IndexOutOfRangeException();
                case System.Runtime.ExceptionIDs.InvalidCast:
                    return new InvalidCastException();
                case System.Runtime.ExceptionIDs.Overflow:
                    return new OverflowException();
                case System.Runtime.ExceptionIDs.NullReference:
                    return new NullReferenceException();
                case System.Runtime.ExceptionIDs.DataMisaligned:
                    // No DataMisalignedException type yet; PNS is the
                    // canonical Test.CoreLib placeholder.
                    return new PlatformNotSupportedException();
                default:
                    return new Exception("runtime exception (unknown id)");
            }
        }

        [RuntimeExport("FailFast")]
        public static void FailFast(uint reason, Exception exception, IntPtr pExAddress, IntPtr pExContext)
        {
            Console.Write("\r\n*** FAIL FAST (reason=");
            Console.WriteUIntRaw(reason);
            Console.Write(") ***\r\n");
            if (exception != null)
                PrintExceptionInfo(exception);
            Console.Write("*** halting ***\r\n");
            while (true) { }
        }

        [RuntimeExport("AppendExceptionStackFrame")]
        public static void AppendExceptionStackFrame(object exceptionObj, IntPtr IP, int flags)
        {
            // Without unwinding we never walk frames. No-op.
        }

        [RuntimeExport("OnFirstChanceException")]
        public static void OnFirstChanceException(object e)
        {
            // First-chance hook. No subscribers in kernel-tier.
        }

        [RuntimeExport("OnUnhandledException")]
        public static void OnUnhandledException(object e)
        {
            // We treat every throw as unhandled (no catch infra). Logging
            // is already done in RhpThrowEx. No-op here.
        }

        // Format and print an exception object. Avoids `is Exception` cast
        // which would require RhTypeCast_IsInstanceOfClass helper. Instead
        // reads `_message` field directly at the known offset (8 bytes
        // after MethodTable*), then walks the string's char array. Same
        // pattern as MemoryExtensions.StringHelpers.GetFirstCharRef.
        //
        // IL spec guarantees only System.Exception subtypes are thrown,
        // so the field-offset assumption is safe.
        private static unsafe void PrintExceptionInfo(object exception)
        {
            if (exception == null)
            {
                Console.Write("(null exception object)\r\n");
                return;
            }

            // Get raw pointer to the object via local reinterpret.
            object local = exception;
            nint objAddr = *(nint*)Unsafe.AsPointer(ref local);

            // Exception layout: [MT*](8) [_message reference](8) ...
            // _message is itself a managed string reference (or null).
            nint msgPtr = *(nint*)(objAddr + 8);

            Console.Write("message: ");
            if (msgPtr == 0)
            {
                Console.Write("(no message)");
            }
            else
            {
                // String layout: [MT*](8) [Length](4) [chars...](length*2)
                int length = *(int*)(msgPtr + 8);
                char* chars = (char*)(msgPtr + 12);
                for (int i = 0; i < length; i++) Console.WriteChar(chars[i]);
            }
            Console.Write("\r\n");
        }
    }
}
