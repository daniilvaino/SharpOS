using System;
using System.IO;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // READ file [--chunk N] [--bytes] — a file to the standard output
    // (step197, step206).
    //
    // An externalpipe (WRITE's file of messages) gives its messages back, under
    // the description it was written with: the class, the Expando, what the
    // writer sent. A broken or cut one gives what is whole, says where it
    // broke, breaks the output and exits with 1.
    //
    // Any other file — and any file with --bytes — goes out as byte[] messages
    // of N bytes (64 KiB by default; the last one shorter). On the screen,
    // when the output was handed to nobody, a text file shows as text. No
    // file: a message and exit code 1.
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

        private static AppFile s_file;

        // The file's first bytes, read to see what it is; given out before the rest.
        private static byte[] s_head;
        private static int s_headAt, s_headEnd;

        private static int Run(string[] args)
        {
            string path = null;
            int chunk = 64 * 1024;
            bool bytes = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--chunk" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out chunk) || chunk <= 0) return Usage();
                }
                else if (args[i] == "--bytes") bytes = true;
                else if (path == null) path = args[i];
                else return Usage();
            }
            if (path == null) return Usage();

            try
            {
                s_file = AppFile.Open(path, AppFile.ModeRead);
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

            using (s_file)
            {
                if (!bytes)
                {
                    s_head = new byte[ExternalPipe.Signature.Length];
                    while (s_headEnd < s_head.Length)
                    {
                        int n = s_file.Read(new Span<byte>(s_head, s_headEnd, s_head.Length - s_headEnd));
                        if (n == 0) break;
                        s_headEnd += n;
                    }
                    if (ExternalPipe.StartsWithSignature(new ReadOnlySpan<byte>(s_head, 0, s_headEnd)))
                    {
                        var file = new ExternalPipeReader(Take);
                        if (!file.ReadHeader())
                        {
                            Console.WriteLine("READ: " + path + ": " + file.Complaint);
                            return 1;
                        }
                        return ExternalPipe.Replay(file, "READ: " + path);
                    }
                }

                using PipeWriter<byte[]> output = Pipe.Write<byte[]>();
                var piece = new byte[chunk];
                while (true)
                {
                    int got = Fill(piece);
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

        // The head first, then the file.
        private static int Take(Span<byte> into)
        {
            if (s_headAt < s_headEnd)
            {
                int n = Math.Min(into.Length, s_headEnd - s_headAt);
                new ReadOnlySpan<byte>(s_head, s_headAt, n).CopyTo(into);
                s_headAt += n;
                return n;
            }
            return s_file.Read(into);
        }

        // A whole piece, or what is left of the file.
        private static int Fill(byte[] piece)
        {
            int got = 0;
            while (got < piece.Length)
            {
                int n = Take(new Span<byte>(piece, got, piece.Length - got));
                if (n == 0) break;
                got += n;
            }
            return got;
        }

        private static int Usage()
        {
            Console.WriteLine("usage: READ file [--chunk N] [--bytes]");
            return 2;
        }
    }
}
