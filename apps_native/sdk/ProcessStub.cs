// System.Diagnostics.Process — resolution stub for the app tier. Ported
// host code (ManagedDoom ConfigUtilities.GetExeDirectory) asks
// Process.GetCurrentProcess().MainModule.FileName to locate the exe dir;
// on SharpOS apps live at the volume root, so the chain reports a fixed
// root path. No process model behind this — just enough shape for the
// exe-directory idiom to resolve to "\" via Path.GetDirectoryName.

namespace System.Diagnostics
{
    public class Process
    {
        public static Process GetCurrentProcess() => new Process();

        public ProcessModule MainModule => new ProcessModule();
    }

    public class ProcessModule
    {
        // Apps are staged to \EFI\BOOT (run_build.ps1), so the exe-directory
        // idiom (Path.GetDirectoryName(...FileName)) resolves to "\EFI\BOOT" —
        // where the WADs/configs sit next to the .EXEs.
        public string FileName => "\\EFI\\BOOT\\APP.EXE";
    }
}
