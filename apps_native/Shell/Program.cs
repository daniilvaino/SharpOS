// A shell, so that "run the battery" stops meaning "press Enter eleven times".
//
// Two ways in, one body. SHELL.EXE SCRIPT runs the script's lines in order
// and exits (step207: the script is an argument, as in sh — the kernel starts
// the shell with the battery's when the configuration says so) — which is what the test rig needs: the build
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
        {
            // sh -c LINE [--report NAME] (step197): the line runs with this
            // shell's own standard ends — what Pipe.From and Pipe.To start.
            string[] args = AppHost.Arguments;
            if (args.Length >= 2 && args[0] == "-c")
            {
                string report = args.Length >= 4 && args[2] == "--report" ? args[3] : null;
                var executor = new Executor(SharpOS.Std.Pipes.Pipe.InputEnd(), SharpOS.Std.Pipes.Pipe.OutputEnd(), report);
                return executor.RunLine(args[1]);
            }

            // SHELL.EXE --complete LINE (step208): what Tab at the end of LINE
            // offers, a string each, to the output — the battery's way to the
            // completion, which at the prompt reads the keyboard.
            if (args.Length == 2 && args[0] == "--complete")
            {
                var completion = new Completion(new Executor());
                string line = args[1];
                int index = line.LastIndexOfAny(completion.Separators) + 1;
                string[] found = completion.GetSuggestions(line, index) ?? new string[0];
                using var output = SharpOS.Std.Pipes.Pipe.Write<string>();
                foreach (string s in found) output.Copy(s);
                return 0;
            }

            return args.Length >= 1 ? RunScript(Process.ResolvePath(args[0])) : Interactive();
        }

        private static int RunScript(string ScriptPath)
        {
            byte* buffer = stackalloc byte[MaxScriptBytes];
            if (AppHost.TryReadFile(ScriptPath, buffer, MaxScriptBytes, out uint length)
                != AppServiceStatus.Ok)
            {
                AppHost.Terminal.Write("[sh] cannot read ");
                AppHost.Terminal.Write(ScriptPath);
                AppHost.Terminal.Write("\n");
                return 1;
            }

            AppHost.Terminal.Write("[sh] script ");
            AppHost.Terminal.Write(ScriptPath);
            AppHost.Terminal.Write(" bytes=");
            AppHost.Terminal.WriteUInt(length);
            AppHost.Terminal.Write("\n");

            // The shell's own output (step212): its commands and builtins write
            // there, each a copy of the end; the screen when none was handed over.
            var executor = new Executor(null, SharpOS.Std.Pipes.Pipe.OutputEnd(), null);
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

                AppHost.Terminal.Write("[sh] $ ");
                AppHost.Terminal.Write(line);
                AppHost.Terminal.Write("\n");

                ran++;
                if (executor.RunLine(line) != 0) failed++;
                if (executor.ExitRequested) break;
            }

            // The one line the rig greps for.
            AppHost.Terminal.Write("[sh] done ran=");
            AppHost.Terminal.WriteUInt(ran);
            AppHost.Terminal.Write(" failed=");
            AppHost.Terminal.WriteUInt(failed);
            AppHost.Terminal.Write("\n");

            if (handOver && !executor.ExitRequested) return Interactive(executor);
            if (executor.ExitRequested) return executor.LastExitCode;

            return failed == 0 ? 0 : 1;
        }

        private static int Interactive()
        {
            // `shell | pipecnt` (step212): what the commands print goes to the
            // pipe; the prompt and the line being typed stay on the terminal.
            var executor = new Executor(null, SharpOS.Std.Pipes.Pipe.OutputEnd(), null);
            RunStartup(executor);
            return Interactive(executor);
        }

        // ENV (step208, as POSIX sh has it): a script the prompt runs first,
        // its lines quietly — what it is for is export lines. Not for `sh -c`
        // or a script: those get only what they were started with.
        private static void RunStartup(Executor executor)
        {
            string path = System.Environment.GetEnvironmentVariable("ENV");
            if (string.IsNullOrEmpty(path)) return;
            path = Process.ResolvePath(path);

            byte* buffer = stackalloc byte[MaxScriptBytes];
            if (AppHost.TryReadFile(path, buffer, MaxScriptBytes, out uint length) != AppServiceStatus.Ok)
            {
                AppHost.Terminal.Write("[sh] ENV: cannot read ");
                AppHost.Terminal.Write(path);
                AppHost.Terminal.Write("\n");
                return;
            }
            uint index = 0;
            while (index < length && !executor.ExitRequested)
            {
                string line = NextLine(buffer, length, ref index);
                if (line.Length == 0 || line[0] == '#') continue;
                executor.RunLine(line);
            }
        }

        // Takes the executor rather than making one, so a script that ends in
        // `shell` hands over its working directory and last exit code instead
        // of starting the session again from the root.
        private static int Interactive(Executor executor)
        {
            AppHost.Terminal.Write("\u001b[1;36mSharpOS shell\u001b[0m\u001b[90m — 'help' for what exists, Tab completes, Up/Down for history, 'exit' to leave\u001b[0m\n");

            var reader = new LineReader(new Completion(executor));
            executor.History = reader.History;

            while (!executor.ExitRequested)
            {
                string prompt = Prompt(executor, out int width);
                string line = reader.Read(prompt, width);
                if (line == null) break;                 // Ctrl+D
                if (line.Trim().Length == 0) continue;

                executor.RunLine(line);
            }

            // `exit N` leaves with N; Ctrl+D with the last command's code, as sh.
            return executor.LastExitCode;
        }

        // The directory in green, the last exit code in red when it was not 0,
        // then "$". Its visible width (no escapes) is where the line starts.
        private static string Prompt(Executor executor, out int width)
        {
            string directory = executor.WorkingDirectory;
            string code = executor.LastExitCode != 0 ? " [" + executor.LastExitCode.ToString() + "]" : "";
            width = directory.Length + code.Length + 3;
            return "\u001b[32m" + directory + "\u001b[0m"
                 + (code.Length != 0 ? "\u001b[31m" + code + "\u001b[0m" : "")
                 + " $ ";
        }

        // A script line is UTF-8. It used to be read a byte per char — Latin-1 —
        // which turned every non-ASCII character into two, and an argument
        // passed on from it arrived double-encoded.
        private static string NextLine(byte* buffer, uint length, ref uint index)
        {
            uint start = index;
            while (index < length && buffer[index] != (byte)'\n' && buffer[index] != (byte)'\r')
                index++;
            string text = Encoding.UTF8.GetString(new System.ReadOnlySpan<byte>(buffer + start, (int)(index - start)));

            while (index < length && (buffer[index] == (byte)'\n' || buffer[index] == (byte)'\r'))
                index++;

            return text.Trim();
        }
    }
}
