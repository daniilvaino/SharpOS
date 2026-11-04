// Kernel-tier backend for System.Diagnostics.Debug output: routes to the
// HAL console (UEFI in Phase 0; framebuffer post-Phase 5). Counterpart of
// the app-tier backend in apps_native/sdk/DebugOutput.AppHost.cs — each
// csproj compiles exactly one, same pattern as
// GcMemorySource.{KernelHeap,AppStatic}. The console is the kernel's
// (KernelServices); before it is installed the output goes nowhere.

using SharpOS.Std.NoRuntime;

namespace System.Diagnostics
{
    internal static unsafe class DebugOutput
    {
        internal static void Write(string message)
        {
            if (KernelServices.Write != null) KernelServices.Write(message);
        }

        internal static void WriteChar(char c)
        {
            if (KernelServices.WriteChar != null) KernelServices.WriteChar(c);
        }
    }
}
