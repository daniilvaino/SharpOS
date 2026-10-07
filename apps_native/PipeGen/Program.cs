using System;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // PIPEGEN.EXE n [name] — n records to the standard output (the screen when
    // the output was handed to nobody), or to the pipe called name. The target
    // code of step 194.
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
            int n = args.Length > 0 ? int.Parse(args[0]) : 10;
            using var o = args.Length > 1 ? Pipe.Write<LogEntry>(args[1]) : Pipe.Write<LogEntry>();
            for (int i = 0; i < n; i++) o.Copy(new LogEntry { Level = i % 5, Text = "запись " + i });
            return 0;
        }
    }
}
