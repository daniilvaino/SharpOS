using System;
using System.Runtime;
using SharpOS.AppSdk;

namespace PipeApps
{
    // CONVERT --from FORMAT | --to FORMAT [options] (step197): bytes into
    // objects and back. The pipe stays a pipe of objects; this program is
    // where the bytes of a file meet them. An unknown direction or format
    // prints the known ones and exits with 2.
    internal static unsafe partial class AppEntry
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
            if (args.Length >= 2 && (args[0] == "--from" || args[0] == "--to"))
            {
                string direction = args[0].Substring(2);
                foreach (IConverter c in Registry.All)
                {
                    if (c.Direction != direction || c.Format != args[1]) continue;
                    var options = new string[args.Length - 2];
                    Array.Copy(args, 2, options, 0, options.Length);
                    return c.Run(options);
                }
            }

            Console.WriteLine("CONVERT: unknown conversion" + (args.Length > 0 ? ": " + string.Join(" ", args) : "") + "; known:");
            foreach (IConverter c in Registry.All)
                Console.WriteLine("  CONVERT --" + c.Direction + " " + c.Format + (c.Options.Length > 0 ? " " + c.Options : ""));
            return 2;
        }
    }
}
