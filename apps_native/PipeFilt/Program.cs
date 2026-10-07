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
            return Run(StressArgs.Apply(AppHost.Arguments));
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
            int min = args.Length > 0 ? int.Parse(args[0]) : 0;
            Pipe.Read<LogEntry>().Where(e => e.Level >= min).WriteTo();
            return 0;
        }
    }
}
