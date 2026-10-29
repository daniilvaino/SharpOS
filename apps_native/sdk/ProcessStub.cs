// System.Diagnostics.Process — resolution stub for the app tier. Ported
// host code (ManagedDoom ConfigUtilities.GetExeDirectory) asks
// Process.GetCurrentProcess().MainModule.FileName to locate the exe dir.
// No process model behind this — just enough shape for that idiom: the
// file this process was loaded from, as the kernel keeps it (step207).

namespace System.Diagnostics
{
    public class Process
    {
        public static Process GetCurrentProcess() => new Process();

        public ProcessModule MainModule => new ProcessModule();
    }

    public class ProcessModule
    {
        // Path.GetDirectoryName of it is where the program lies — /games for
        // DOOM, beside its WADs.
        public string FileName => SharpOS.AppSdk.Process.ImagePath;
    }
}
