using System;
using System.IO;
using System.Runtime;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // WRITE file [--append] — the standard input into a file (step197, step206).
    //
    // A stream of byte[] goes in as its bytes, a stream of string as UTF-8
    // lines: the file is those bytes, nothing added. Any other stream — a
    // class, Expando, object — goes in as an externalpipe: the pipe's
    // description, then its blocks as they came, and READ gives the same
    // messages back. Text of them is an explicit conversion: CONVERT --to
    // lines, --to json.
    //
    // The file is created or cut; with --append, added to. An externalpipe
    // is appended to only by the same types — another stream into it is an
    // error naming both (exit code 1); a file of bytes is appended to as
    // bytes. A broken input leaves the file with what came and ends the
    // program with 141 (as any broken standard end does).
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
                else if (NumberArg.IsHelp(args[i])) { Console.WriteLine("usage: WRITE file [--append]"); return 0; }
                // An option it does not know is not a file name (step213:
                // `WRITE --help` made a file called that); ./-x names one.
                else if (args[i].Length > 1 && args[i][0] == '-') return Usage();
                else if (path == null) path = args[i];
                else return Usage();
            }
            if (path == null) return Usage();

            RawPipeReader input = Pipe.Read();
            PipeTransport.WaitSchema(input.Handle);
            byte[] schema = PipeTransport.Schema(input.Handle, out ulong rootKey);
            string type = schema == null ? null : ExternalPipe.RootName(schema, rootKey);
            bool pipe = type != null && type != "System.String" && type != "System.Byte[]";

            bool header = true;
            if (pipe && append)
            {
                string refused = CheckAppend(path, schema, rootKey, type, out header);
                if (refused != null)
                {
                    Console.Error.WriteLine("WRITE: " + refused);
                    input.Dispose();
                    return 1;
                }
            }

            AppFile file;
            try
            {
                file = AppFile.Open(path, append ? AppFile.ModeAppend : AppFile.ModeWrite);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine("WRITE: " + e.Message);
                input.Dispose();
                return 1;
            }

            using (file)
            {
                if (pipe)
                {
                    ExternalPipe.Copy(input, schema, rootKey, header, (first, second) =>
                    {
                        file.Write(first);
                        file.Write(second);
                    });
                    input.Dispose();
                    return 0;
                }

                byte[] line = new byte[256];
                foreach (View v in input)
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

        // An externalpipe takes only its own types: null when the stream may
        // go on to the file (its header wanted when the file is new or empty),
        // otherwise why not.
        private static string CheckAppend(string path, byte[] schema, ulong rootKey, string type, out bool header)
        {
            header = true;
            AppFile existing;
            try
            {
                existing = AppFile.Open(path, AppFile.ModeRead);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (IOException e)
            {
                return e.Message;
            }
            using (existing)
            {
                var file = new ExternalPipeReader(existing.Read);
                if (file.ReadHeader())
                {
                    header = false;
                    if (ExternalPipe.SameTypes(file.Schema, file.RootKey, schema, rootKey)) return null;
                    string held = ExternalPipe.RootName(file.Schema, file.RootKey);
                    return path + " holds " + held + (held == type ? " of another layout" : "")
                           + "; the input is " + type + " — one file, one stream";
                }
                if (file.Complaint == null) return null;    // empty: as a new file
                if (!file.SignatureSeen) return path + " is not an externalpipe; the input is " + type + " — one file, one stream";
                return path + ": " + file.Complaint;
            }
        }

        private static int Usage()
        {
            Console.Error.WriteLine("usage: WRITE file [--append]");
            return 2;
        }
    }
}
