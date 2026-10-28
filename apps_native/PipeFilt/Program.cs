using System;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // PIPEFILT.EXE min — a stage: the records of level min and up, from the
    // standard input on to the standard output, each block as it came.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run(StressArgs.Apply(AppHost.Arguments)));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static int Run(string[] args)
        {
            if (!NumberArg.TryFirst(args, "PIPEFILT", "PIPEFILT [min] — the records of level min (0) and up", 0, out long level, out int exit))
                return exit;
            int min = (int)level;
            Pipe.Read<LogEntry>().Where(e => e.Level >= min).WriteTo();
            return 0;
        }
    }
}
