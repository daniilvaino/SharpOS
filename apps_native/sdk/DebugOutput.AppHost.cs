// App-tier backend for System.Diagnostics.Debug output: routes to the
// kernel console through the AppHost service table. Counterpart of the
// kernel-tier backend in std/no-runtime/shared/Runtime/
// Diagnostics.Output.KernelConsole.cs — each csproj compiles exactly one.

using SharpOS.AppSdk;

namespace System.Diagnostics
{
    internal static class DebugOutput
    {
        internal static void Write(string message) => AppHost.WriteString(message);
        internal static void WriteChar(char c) => AppHost.WriteChar(c);
    }
}
