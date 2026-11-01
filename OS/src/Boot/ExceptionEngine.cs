using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using OS.Hal;

#pragma warning disable CS0162 // Probes/Trace switches are compile-time constants: the branch a switch turns off is meant to be dead (docs/build-warnings.md)

#pragma warning disable CS8500 // the runtime takes pointers to managed types by design

namespace OS.Boot
{
    // The managed ends of the exception machinery: the seam a throw lands
    // on (RhpTest_ThrowIngress, from the ThrowEx shellcode, which goes on
    // to DispatchEx) and the classlib hooks (GetRuntimeException, FailFast,
    // AppendExceptionStackFrame, OnFirstChanceException,
    // OnUnhandledException). try/catch/finally, filters and hardware
    // faults are DispatchEx's and HwFaultBridge's (Boot/EH).
    internal static class ExceptionEngine
    {
        // RhpThrowEx moved to OS/src/Boot/EH/ThrowExStub.cs (Phase 1
        // step 5.1). Body is patched by ThrowExPatcher with a 186-byte
        // shellcode that builds PAL_LIMITED_CONTEXT + ExInfo on stack
        // and tail-calls into managed RhpTest_ThrowIngress (this file).

        // Where the throw shellcode lands after building ExInfo: the managed
        // dispatch, which does not return when a catch takes the exception.
        // The name is the patchers' (ThrowExPatcher, RethrowPatcher).
        [RuntimeExport("RhpTest_ThrowIngress")]
        public static unsafe void RhpTest_ThrowIngress(byte* exceptionPtr, OS.Boot.EH.ExInfo* exInfo)
        {
            OS.Boot.EH.DispatchEx.Dispatch(exceptionPtr, exInfo);
            Console.Write("\r\n*** Dispatch returned (BUG) ***\r\n");
            while (true) { }
        }

        // RhpRethrow moved to OS/src/Boot/EH/RethrowStub.cs (Phase 1
        // step 6). Body patched with ~186-byte shellcode that builds
        // ExInfo with kind=Throw|Rethrow и calls DispatchEx — реальный
        // rethrow path (vs original halt-stub).

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
                    // Not `new`: this is asked for when memory has run out.
                    // Fresh if there is room, the preallocated one if not.
                    return SharpOS.Std.NoRuntime.GcHeap.OutOfMemory();
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
