using System;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // VIEWFILT min — a test stage with no class of its own (step197): passes
    // on, untranslated, the messages whose Level is at least min.
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
            long min = args.Length > 0 ? long.Parse(args[0]) : 0;
            Pipe.Read().Where(v => v["Level"] >= min).WriteTo();
            return 0;
        }
    }
}
