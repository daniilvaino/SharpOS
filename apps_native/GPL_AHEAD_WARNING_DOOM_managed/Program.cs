using System.Runtime;
using SharpOS.AppSdk;

namespace DoomApp
{
    // P1 stub entry for the ManagedDoom freestanding build. Its only job right
    // now is to give ILC an entry point and force the DOOM core to compile, so a
    // build surfaces the app-std gaps (System.IO, System.Numerics, extra
    // collections, ...). The real main loop (tick -> render -> blit -> input)
    // lands once the core compiles and the GOP/PS2 shims exist.
    internal static unsafe class Entry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            AppHost.WriteString("doom: P1 stub entry\n");
            return 0;
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        // csc requires a Main for OutputType=Exe; never called — the real
        // entry is SharpAppBootstrap via /ENTRY (same as the other apps).
        private static int Main() => 0;
    }
}
