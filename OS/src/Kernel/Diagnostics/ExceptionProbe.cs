using System;
using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase 1 minimal exception-engine validation. Triggers an explicit
    // `throw new InvalidOperationException("test message")` and verifies
    // RhpThrowEx routes through PrintExceptionInfo with the type's
    // virtual `Message` property. Expected output:
    //
    //     *** UNHANDLED EXCEPTION ***
    //     message: test message
    //     *** halting ***
    //
    // Like IdtProbe, this never returns — gated behind `if (false)` in
    // Kernel.cs by default. Flip to `true` once per change to ExceptionEngine
    // to verify the path.
    internal static class ExceptionProbe
    {
        public static void TriggerThrow()
        {
            Log.Write(LogLevel.Info, "exception probe: throw new InvalidOperationException");
            throw new InvalidOperationException("test message from exception probe");
            // Unreachable — RhpThrowEx halts.
        }
    }
}
