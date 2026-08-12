using System.Runtime;
using OS.Kernel.Diagnostics;

namespace OS.PAL.SharpOSHost
{
    // Does the fork print its exception-dispatch trace?
    //
    // The trace ([SFI], [DESP], [CCF-resume] and the stack dumps around them)
    // was written during EH bring-up and left permanently on. It is worth
    // hundreds of serial lines per thrown exception, and PowerShell throws on
    // ordinary paths: writing its module analysis cache fails on our
    // filesystem every command. The result looked like a scheduler problem —
    // input dead for seconds after a command — when the CPU was simply busy
    // printing.
    //
    // Kept as a switch rather than deleted: it is the tool that closed
    // step103 and step107, and the next EH investigation will want it back.
    internal static class EhDiagnostics
    {
        [RuntimeExport("SharpOSHost_EhDiagEnabled")]
        public static int EhDiagEnabled() => Probes.EhVerboseDiagnostics ? 1 : 0;
    }
}
