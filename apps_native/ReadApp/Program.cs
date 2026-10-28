using System;
using System.IO;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // READ file [--chunk N] [--bytes | --externalpipe | --lines] — a file to the
    // standard output (step197, step206; the explicit modes step208).
    //
    // Without a mode READ looks at the file's first bytes. An externalpipe
    // (WRITE's file of messages) gives its messages back, under the
    // description it was written with: the class, the Expando, what the
    // writer sent. A broken or cut one gives what is whole, says where it
    // broke, breaks the output and exits with 1. Any other file goes out as
    // byte[] messages of N bytes (64 KiB by default; the last one shorter).
    //
    // The modes say it instead of the guess: --bytes — bytes, whatever the
    // file is (an externalpipe too, for CONVERT --from externalpipe to take);
    // --externalpipe — messages, and a file that is not one is refused
    // (exit 1, nothing out) rather than sent on as bytes; --lines — UTF-8
    // text, a string a line (step213): `READ log --lines | KQL 'take 45'`.
    //
    // On the screen, when the output was handed to nobody, a text file shows
    // as text. No file: a message and exit code 1.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run(StressArgs.Apply(AppHost.Arguments)));
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
            bool bytes = false, externalpipe = false, lines = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--chunk" && i + 1 < args.Length)
                {
                    if (!int.TryParse(args[++i], out chunk) || chunk <= 0) return Usage();
                }
                else if (args[i] == "--bytes") bytes = true;
                else if (args[i] == "--externalpipe") externalpipe = true;
                else if (args[i] == "--lines") lines = true;
                else if (args[i] == "--help" || args[i] == "-h" || args[i] == "-?")
                {
                    Console.WriteLine(UsageText);
                    return 0;
                }
                else if (args[i].Length > 1 && args[i][0] == '-') return Usage();
                else if (path == null) path = args[i];
                else return Usage();
            }
            if (path == null || (bytes ? 1 : 0) + (externalpipe ? 1 : 0) + (lines ? 1 : 0) > 1) return Usage();

            try
            {
                s_file = AppFile.Open(path, AppFile.ModeRead);
            }
            catch (FileNotFoundException)
            {
                Console.Error.WriteLine("READ: no such file: " + path);
                return 1;
            }
            catch (IOException e)
            {
                Console.Error.WriteLine("READ: " + e.Message);
                return 1;
            }

            using (s_file)
            {
                if (lines) return Lines(chunk);
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
                            Console.Error.WriteLine("READ: " + path + ": " + file.Complaint);
                            return 1;
                        }
                        return ExternalPipe.Replay(file, "READ: " + path);
                    }
                    if (externalpipe)
                    {
                        Console.Error.WriteLine("READ: " + path + ": not an externalpipe (no signature at its start)");
                        return 1;
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

        // --lines (step213): the file as UTF-8 text, a string message a line
        // (without its \n, a CRLF's \r dropped), read N bytes at a time; a
        // character cut by a piece's end is finished from the next. A line
        // longer than 64 Ki characters goes in pieces (PipeTextWriter).
        private static int Lines(int chunk)
        {
            using System.IO.TextWriter output = Pipe.WriteText();
            var piece = new byte[chunk + 3];
            int carried = 0;
            while (true)
            {
                int n = Fill(piece, carried, chunk);
                int length = carried + n;
                if (length == 0) break;
                int whole = n == 0 ? length : WholeCharacters(piece, length);
                output.Write(System.Text.Encoding.UTF8.GetString(piece, 0, whole));
                carried = length - whole;
                Array.Copy(piece, whole, piece, 0, carried);
                if (n == 0) break;
            }
            return 0;
        }

        // How many bytes from the start end on a character boundary: a lead
        // byte in the last three whose sequence runs past the end stays out.
        private static int WholeCharacters(byte[] bytes, int length)
        {
            for (int back = 1; back <= 3 && back <= length; back++)
            {
                byte b = bytes[length - back];
                if ((b & 0xC0) == 0x80) continue;
                int need = b >= 0xF0 ? 4 : b >= 0xE0 ? 3 : b >= 0xC0 ? 2 : 1;
                return need > back ? length - back : length;
            }
            return length;
        }

        // Up to count bytes into piece from at; fewer only at the file's end.
        private static int Fill(byte[] piece, int at, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = Take(new Span<byte>(piece, at + got, count - got));
                if (n == 0) break;
                got += n;
            }
            return got;
        }

        private const string UsageText = "usage: READ file [--chunk N] [--bytes | --externalpipe | --lines]";

        private static int Usage()
        {
            Console.Error.WriteLine(UsageText);
            return 2;
        }
    }
}
