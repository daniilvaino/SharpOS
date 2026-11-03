using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    // xxd [-c N] [-g N] [-s N] [-l N] [-p] [file], xxd -r [-p] [file]: bytes as
    // the hex lines xxd prints, and those lines back into bytes. A stream of
    // other objects is refused by the byte reader, naming the type.
    internal static class Xxd
    {
        public static int Run(string[] args)
        {
            var options = new Options("xxd [-c N] [-g N] [-s N] [-l N] [-p] [-r] [file] — bytes as hex, -r back");
            Dictionary<string, string> o = options.Parse(args, "pr", "cgsl");
            if (options.Failed || options.Words.Count > 1) return options.Refuse();
            bool plain = o.ContainsKey("p");

            Stream input;
            try
            {
                input = options.Words.Count == 1 ? new FileBytes(AppFile.Open(options.Words[0], AppFile.ModeRead)) : Pipe.ReadBytes();
            }
            catch (IOException e)
            {
                Console.Error.WriteLine("xxd: " + e.Message);
                return 1;
            }

            using (input)
            {
                try
                {
                    if (o.ContainsKey("r")) return Reverse(input, plain);
                    int columns = Number(o, "c", plain ? 30 : 16);
                    int group = Number(o, "g", 2);
                    long skip = Number(o, "s", 0);
                    long limit = Number(o, "l", -1);
                    if (columns <= 0 || columns > 256 || group < 0 || skip < 0)
                    {
                        options.Wrong = "a value out of range";
                        return options.Refuse();
                    }
                    return Dump(input, columns, group, skip, limit, plain);
                }
                catch (FormatException)
                {
                    options.Wrong = "not a number";
                    return options.Refuse();
                }
                catch (InvalidDataException e)
                {
                    Console.Error.WriteLine("xxd: " + e.Message);
                    return 1;
                }
            }
        }

        private static int Number(Dictionary<string, string> o, string name, int fallback)
            => o.TryGetValue(name, out string text) ? int.Parse(text) : fallback;

        private const string Digits = "0123456789abcdef";

        private static int Dump(Stream input, int columns, int group, long skip, long limit, bool plain)
        {
            using TextWriter output = Pipe.WriteText();
            var row = new byte[columns];
            var line = new StringBuilder();
            long offset = skip;

            var waste = new byte[4096];
            while (skip > 0)
            {
                int n = input.Read(waste, 0, (int)Math.Min(skip, waste.Length));
                if (n == 0) break;
                skip -= n;
            }

            while (limit != 0)
            {
                int want = limit > 0 && limit < columns ? (int)limit : columns;
                int got = 0;
                while (got < want)
                {
                    int n = input.Read(row, got, want - got);
                    if (n == 0) break;
                    got += n;
                }
                if (got == 0) break;
                if (limit > 0) limit -= got;

                line.Clear();
                if (plain)
                {
                    for (int i = 0; i < got; i++) line.Append(Digits[row[i] >> 4]).Append(Digits[row[i] & 15]);
                }
                else
                {
                    line.Append(offset.ToString("x8")).Append(": ");
                    for (int i = 0; i < columns; i++)
                    {
                        if (i < got) line.Append(Digits[row[i] >> 4]).Append(Digits[row[i] & 15]);
                        else line.Append("  ");
                        if (group > 0 && (i + 1) % group == 0) line.Append(' ');
                    }
                    line.Append(' ');
                    for (int i = 0; i < got; i++)
                        line.Append(row[i] >= 0x20 && row[i] < 0x7F ? (char)row[i] : '.');
                }
                output.WriteLine(line.ToString());
                offset += got;
                if (got < want) break;
            }
            return 0;
        }

        // The hex of each line: after "offset:", and up to the two blanks
        // before the text column; -p: all of it.
        private static int Reverse(Stream input, bool plain)
        {
            using Stream output = Pipe.WriteBytes();
            using var reader = new StreamReader(input);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                int at = 0, end = line.Length;
                if (!plain)
                {
                    int colon = line.IndexOf(':');
                    if (colon < 0) continue;
                    at = colon + 1;
                    int text = line.IndexOf("  ", at);
                    if (text >= 0) end = text;
                }
                int high = -1;
                for (; at < end; at++)
                {
                    int digit = Digits.IndexOf(char.ToLowerInvariant(line[at]));
                    if (digit < 0) continue;
                    if (high < 0)
                    {
                        high = digit;
                        continue;
                    }
                    output.WriteByte((byte)(high << 4 | digit));
                    high = -1;
                }
            }
            return 0;
        }

        // A file as a Stream, for the pieces above.
        private sealed class FileBytes : Stream
        {
            private readonly AppFile _file;
            public FileBytes(AppFile file) => _file = file;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => _file.Read(new Span<byte>(buffer, offset, count));
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() { }
            protected override void Dispose(bool disposing) => _file.Dispose();
        }
    }
}
