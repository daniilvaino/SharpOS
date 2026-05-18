using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase B#3 sub-step 3 — shell-engine oracle. Drives Shell.Execute
    // with literal command lines (the scancode->line path is already
    // covered by Ps2Probe/LineEditorProbe) and asserts dispatch: every
    // known command is recognised, an unknown one is rejected, and the
    // `mem` data path returns a sane non-zero total. Each command's
    // own output is also emitted to serial (visible in the log).
    // Deterministic, headless. The interactive REPL is the next
    // sub-step and reuses this engine unchanged.
    internal static class ShellProbe
    {
        public static void Run()
        {
            bool help = Shell.Execute("help");
            bool ver  = Shell.Execute("ver");
            bool mem  = Shell.Execute("mem");
            bool dev  = Shell.Execute("devices");
            bool ech  = Shell.Execute("echo hello-shell");
            bool blank = Shell.Execute("   ");
            bool unk  = Shell.Execute("frobnicate now");

            ulong mib = Shell.TotalUsableMiB(out uint regions);

            bool pass = help && ver && mem && dev && ech && blank
                        && !unk && mib > 0 && regions > 0;

            Console.Write("[shell] known=ok unk=");
            Console.Write(unk ? "ACCEPTED" : "rejected");
            Console.Write(" memMiB=");
            Console.WriteUInt((uint)mib);
            Console.Write(" regions=");
            Console.WriteUInt(regions);
            Console.WriteLine(pass ? " PASS" : " FAIL");
        }
    }
}
