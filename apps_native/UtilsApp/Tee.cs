using System;
using System.Collections.Generic;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    // tee [-a] FILE...: every message on to the output, and to each file. A file
    // is a WRITE of its own, so it keeps what WRITE keeps: bytes and text as
    // they are, objects as an externalpipe READ gives back. A WRITE that fails
    // says why, and the rest go on.
    internal static unsafe class Tee
    {
        private sealed class Copy
        {
            public Process Writer;
            public int End;
            public PipePair Pair;
        }

        public static int Run(string[] args)
        {
            var options = new Options("tee [-a] FILE... — the input on to the output and into the files");
            Dictionary<string, string> o = options.Parse(args, "a", "");
            if (options.Failed) return options.Refuse();

            RawPipeReader input = Pipe.Read();
            PipeTransport.WaitSchema(input.Handle);
            byte[] schema = PipeTransport.Schema(input.Handle, out ulong rootKey);

            string write = Process.FindProgram("WRITE");
            if (write == null && options.Words.Count > 0)
            {
                Console.Error.WriteLine("tee: no WRITE along PATH");
                input.Dispose();
                return 1;
            }

            int code = 0;
            var copies = new List<Copy>();
            foreach (string path in options.Words)
            {
                PipePair pair = Pipe.Create();
                string[] writeArgs = o.ContainsKey("a") ? new[] { path, "--append" } : new[] { path };
                var copy = new Copy { Pair = pair, Writer = Process.Start(write, writeArgs, pair.ReadEnd, null, null) };
                copy.End = pair.WriteEnd.Take("tee's copy");
                // No schema: an input that ended before saying what it carries.
                if (schema != null) PipeTransport.OpenEnd(copy.End, schema, rootKey, out _);
                copies.Add(copy);
            }

            try
            {
                using (var output = new Output(input))
                {
                    RawRegion region;
                    while ((region = input.Next(null)) != null)
                    {
                        foreach (Copy copy in copies)
                        {
                            if (copy.End == 0) continue;
                            if (!SendCopy(copy.End, region))
                            {
                                PipeTransport.End(copy.End);
                                copy.End = 0;
                                code = 1;
                            }
                        }
                        output.Forward(region);
                    }
                }
            }
            finally
            {
                foreach (Copy copy in copies)
                    if (copy.End != 0) PipeTransport.End(copy.End);
                input.Dispose();
            }

            foreach (Copy copy in copies)
            {
                copy.Writer.WaitForExit();
                if (copy.Writer.ExitCode != 0) code = 1;
                copy.Writer.Dispose();
                copy.Pair.ReadEnd.Dispose();
                copy.Pair.WriteEnd.Dispose();
            }
            return code;
        }

        // A block of its own for the file's pipe: the one that came goes on to the output.
        private static bool SendCopy(int end, RawRegion region)
        {
            byte* block = (byte*)PipeTransport.Allocate(region.Length);
            if (block == null) throw new OutOfMemoryException("no exchange block for a copy");
            Buffer.MemoryCopy(region.Block, block, region.Length, region.Length);
            if (PipeTransport.Send(end, block, region.Length) == PipeStatus.Ok) return true;
            PipeTransport.Free(block);
            return false;
        }
    }
}
