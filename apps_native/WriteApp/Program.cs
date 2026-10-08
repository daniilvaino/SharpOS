using System;
using System.IO;
using System.Runtime;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // WRITE file [--append] — the standard input into a file (step197): a
    // byte[] message as its bytes, a string as UTF-8 and a line feed, any
    // other message as the line the screen would show for it. The file is
    // created or cut (appended to with --append). A broken input leaves the
    // file with what came and ends the program with 141 (as any broken
    // standard end does).
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
            string path = null;
            bool append = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--append") append = true;
                else if (path == null) path = args[i];
                else return Usage();
            }
            if (path == null) return Usage();

            AppFile file;
            try
            {
                file = AppFile.Open(path, append ? AppFile.ModeAppend : AppFile.ModeWrite);
            }
            catch (IOException e)
            {
                Console.WriteLine("WRITE: " + e.Message);
                return 1;
            }

            byte[] line = new byte[256];
            using (file)
            {
                foreach (View v in Pipe.Read())
                {
                    if (v.TryGetBytes(out byte* bytes, out int count))
                    {
                        file.Write(new ReadOnlySpan<byte>(bytes, count));
                        continue;
                    }
                    string text = v.TryGetChars(out char* chars, out int length)
                        ? new string(new ReadOnlySpan<char>(chars, length))
                        : v.IsNull ? "null" : v.ToString();
                    int max = Encoding.UTF8.GetMaxByteCount(text.Length) + 1;
                    if (line.Length < max) line = new byte[max];
                    int n = Encoding.UTF8.GetBytes(text.AsSpan(), new Span<byte>(line));
                    line[n] = (byte)'\n';
                    file.Write(new ReadOnlySpan<byte>(line, 0, n + 1));
                }
            }
            return 0;
        }

        private static int Usage()
        {
            Console.WriteLine("usage: WRITE file [--append]");
            return 2;
        }
    }
}
