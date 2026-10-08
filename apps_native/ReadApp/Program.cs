using System;
using System.IO;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // READ file [--chunk N] — the file's bytes to the standard output as
    // byte[] messages of N bytes (64 KiB by default; the last one shorter).
    // On the screen, when the output was handed to nobody, a text file shows
    // as text. No file: a message and exit code 1 (step197).
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
            int chunk = 64 * 1024;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--chunk" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out chunk) || chunk <= 0) return Usage();
                }
                else if (path == null) path = args[i];
                else return Usage();
            }
            if (path == null) return Usage();

            AppFile file;
            try
            {
                file = AppFile.Open(path, AppFile.ModeRead);
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine("READ: no such file: " + path);
                return 1;
            }
            catch (IOException e)
            {
                Console.WriteLine("READ: " + e.Message);
                return 1;
            }

            using (file)
            using (PipeWriter<byte[]> output = Pipe.Write<byte[]>())
            {
                var piece = new byte[chunk];
                while (true)
                {
                    int got = Fill(file, piece);
                    if (got == 0) break;
                    if (got == chunk)
                    {
                        output.Copy(piece);
                        continue;
                    }
                    var last = new byte[got];
                    Array.Copy(piece, last, got);
                    output.Copy(last);
                    break;
                }
            }
            return 0;
        }

        // A whole piece, or what is left of the file.
        private static int Fill(AppFile file, byte[] piece)
        {
            int got = 0;
            while (got < piece.Length)
            {
                int n = file.Read(new Span<byte>(piece, got, piece.Length - got));
                if (n == 0) break;
                got += n;
            }
            return got;
        }

        private static int Usage()
        {
            Console.WriteLine("usage: READ file [--chunk N]");
            return 2;
        }
    }
}
