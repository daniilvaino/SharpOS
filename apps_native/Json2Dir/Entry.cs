// JSON2DIR — json2dir (vendor/json2dir, MIT) as a SharpOS program: the
// directory tree a JSON document describes, made in the working directory.
//
//     READ tree.json | JSON2DIR
//
// The document comes on the standard input (bytes, or text); errors go to
// the error stream. FAT has no symbolic links and no mode bits: a ["link",
// target] refuses the whole document before anything is written, and a
// ["script", content] is written as an ordinary file (vendor/json2dir/
// PROVENANCE.md).

using System.Runtime;
using SharpOS.AppSdk;

internal static unsafe class AppEntry
{
    [RuntimeExport("SharpAppEntry")]
    private static int SharpAppEntry(ulong startupPointer)
    {
        AppRuntime.Initialize((AppStartupBlock*)startupPointer);
        return Program.Main(AppHost.Arguments);
    }

    [RuntimeExport("SharpAppBootstrap")]
    private static int SharpAppBootstrap(ulong startupPointer)
    {
        RuntimeImports.ManagedStartup();
        return SharpAppEntry(startupPointer);
    }

    // The C# entry point (StartupObject): without arguments, as every app
    // here has it — ILC cannot start a freestanding image at Main(string[]).
    private static int Main() => Program.Main(AppHost.Arguments);
}
