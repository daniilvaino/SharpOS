using System;
using System.Runtime;
using SharpOS.AppSdk;

namespace KqlApps
{
    // KQLBABY.EXE — BabyKusto (vendor/BabyKusto) on the app tier (step198):
    // placeholder until the engine compiles.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int Run() => 0;
    }
}
