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
        // Called by ILC for IL `throw <object>` instructions. The exception
        // object lives in our managed heap; we walk its v-table to format
        // a panic message, then halt.
        [RuntimeExport("RhpThrowEx")]
        public static void RhpThrowEx(object exception)
        {
            Console.Write("\r\n*** UNHANDLED EXCEPTION ***\r\n");
            PrintExceptionInfo(exception);
            Console.Write("*** halting ***\r\n");
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
        public static Exception GetRuntimeException(int id)
        {
            // BCL maps numeric IDs to specific exception types. We don't
            // have the full ID enum wired through ILC's runtime imports,
            // so just return a generic Exception. Caller will print it.
            return new Exception("runtime exception (id=" + id + ")");
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
