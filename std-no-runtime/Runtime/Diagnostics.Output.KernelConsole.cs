// Kernel-tier backend for System.Diagnostics.Debug output: routes to the
// HAL console (UEFI in Phase 0; framebuffer post-Phase 5). Counterpart of
// the app-tier backend in apps_native/sdk/DebugOutput.AppHost.cs — each
// csproj compiles exactly one, same pattern as
// GcMemorySource.{KernelHeap,AppStatic}.

using OS.Hal;

namespace System.Diagnostics
{
    internal static class DebugOutput
    {
        internal static void Write(string message) => Console.Write(message);
        internal static void WriteChar(char c) => Console.WriteChar(c);
    }
}
