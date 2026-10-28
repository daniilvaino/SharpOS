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
            if (!NumberArg.TryFirst(args, "PIPEGEN", "PIPEGEN [n] [name] — n records (10), to the pipe called name", 10, out long count, out int exit))
                return exit;
            int n = (int)count;
            using var o = args.Length > 1 ? Pipe.Write<LogEntry>(args[1]) : Pipe.Write<LogEntry>();
            for (int i = 0; i < n; i++) o.Copy(new LogEntry { Level = i % 5, Text = "text " + i });
            return 0;
        }
    }
}
