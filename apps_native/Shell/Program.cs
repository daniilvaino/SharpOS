// A shell, so that "run the battery" stops meaning "press Enter eleven times".
//
// Two ways in, one body. With \apps\AUTORUN.SH on the volume the lines are run
// in order and the shell exits — which is what the test rig needs: the build
// arrives over the network, a socket switches the laptop on, and nobody is
// there to type. Without it there is a prompt.
//
// The parsing is not ours: quoting, word splitting, && || ; and redirections
// are a decade of other people's edge cases, and vendor/ShellSyntaxTree
// already holds them. What is ours is the half that has to be — turning a
// clause into a call into this kernel.

using System.Runtime;
using System.Text;
using SharpOS.AppSdk;

namespace Shell
{
    internal static unsafe class AppEntry
    {
        private const string ScriptPath = "\\apps\\AUTORUN.SH";
        private const int MaxScriptBytes = 8192;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int Run()
            => AppHost.FileExistsEx(ScriptPath) == AppServiceStatus.Ok
                ? RunScript()
                : Interactive();

        private static int RunScript()
        {
            byte* buffer = stackalloc byte[MaxScriptBytes];
            if (AppHost.TryReadFile(ScriptPath, buffer, MaxScriptBytes, out uint length)
                != AppServiceStatus.Ok)
            {
                AppHost.WriteString("[sh] cannot read ");
                AppHost.WriteString(ScriptPath);
                AppHost.WriteString("\n");
                return 1;
            }

            AppHost.WriteString("[sh] script ");
            AppHost.WriteString(ScriptPath);
            AppHost.WriteString(" bytes=");
            AppHost.WriteUInt(length);
            AppHost.WriteString("\n");

            var executor = new Executor();
            uint ran = 0;
            uint failed = 0;
            bool handOver = false;

            uint index = 0;
            while (index < length)
            {
                string line = NextLine(buffer, length, ref index);
                if (line.Length == 0 || line[0] == '#') continue;

                // The one word that is not a command: finish the list, then
                // give the machine to whoever is sitting at it. Worth having
                // because a shell the KERNEL started is at nesting depth zero
                // and can actually launch things — one started from the
                // launcher is already at the limit and cannot.
                if (line == "shell") { handOver = true; continue; }

                AppHost.WriteString("[sh] $ ");
                AppHost.WriteString(line);
                AppHost.WriteString("\n");

                ran++;
                if (executor.RunLine(line) != 0) failed++;
                if (executor.ExitRequested) break;
            }

            // The one line the rig greps for.
            AppHost.WriteString("[sh] done ran=");
            AppHost.WriteUInt(ran);
            AppHost.WriteString(" failed=");
            AppHost.WriteUInt(failed);
            AppHost.WriteString("\n");

            if (handOver && !executor.ExitRequested) return Interactive(executor);

            return failed == 0 ? 0 : 1;
        }

        private static int Interactive() => Interactive(new Executor());

        // Takes the executor rather than making one, so a script that ends in
        // `shell` hands over its working directory and last exit code instead
        // of starting the session again from the root.
        private static int Interactive(Executor executor)
        {
            AppHost.WriteString("SharpOS shell. Type 'help' for what exists, 'exit' to leave.\n");

            while (!executor.ExitRequested)
            {
                AppHost.WriteString(executor.WorkingDirectory);
                AppHost.WriteString(" $ ");

                string line = LineReader.Read();
                if (line.Length == 0) continue;

                executor.RunLine(line);
            }

            return 0;
        }

        private static string NextLine(byte* buffer, uint length, ref uint index)
        {
            var text = new StringBuilder();
            while (index < length && buffer[index] != (byte)'\n' && buffer[index] != (byte)'\r')
                text.Append((char)buffer[index++]);

            while (index < length && (buffer[index] == (byte)'\n' || buffer[index] == (byte)'\r'))
                index++;

            return text.ToString().Trim();
        }
    }
}
