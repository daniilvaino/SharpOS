using System;
using System.Text;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --to bytes [--chunk N]: any pipe as bytes. A byte[] message goes as it
    // is, a string as its UTF-8 line ("\n" after it), any other message as the
    // line the screen shows for it — what `--to lines` makes of it, as bytes.
    // Without --chunk each message goes on at once as its own byte[] message:
    // nothing is held back, the pipe stays as live as its input. --chunk N
    // regroups the bytes into messages of exactly N (the last may be shorter);
    // --chunk 1 is a byte a message. The reverse is `--from lines`.
    internal sealed unsafe class ToBytes : IConverter
    {
        public string Direction => "to";
        public string Format => "bytes";
        public string Options => "[--chunk N]";

        public int Run(string[] options)
        {
            int chunk = 0;
            for (int i = 0; i < options.Length; i++)
            {
                if (options[i] == "--chunk" && i + 1 < options.Length && int.TryParse(options[i + 1], out chunk) && chunk > 0)
                {
                    i++;
                    continue;
                }
                Console.Error.WriteLine("CONVERT --to bytes: unknown option " + options[i] + "; known: --chunk N (N > 0)");
                return 2;
            }

            using PipeWriter<byte[]> output = Pipe.Write<byte[]>();
            byte[] pending = chunk > 0 ? new byte[chunk] : null;
            int fill = 0;
            foreach (View v in Pipe.Read())
            {
                byte[] bytes = BytesOf(v);
                if (pending == null)
                {
                    if (bytes.Length > 0) output.Copy(bytes);
                    continue;
                }

                int at = 0;
                while (at < bytes.Length)
                {
                    int n = Math.Min(chunk - fill, bytes.Length - at);
                    Array.Copy(bytes, at, pending, fill, n);
                    fill += n;
                    at += n;
                    if (fill == chunk)
                    {
                        output.Copy(pending);
                        fill = 0;
                    }
                }
            }

            if (fill > 0)
            {
                var last = new byte[fill];
                Array.Copy(pending, last, fill);
                output.Copy(last);
            }
            return 0;
        }

        private static byte[] BytesOf(View v)
        {
            if (v.TryGetBytes(out byte* data, out int count))
                return new ReadOnlySpan<byte>(data, count).ToArray();
            string line = v.TryGetChars(out char* chars, out int length)
                ? new string(new ReadOnlySpan<char>(chars, length))
                : v.IsNull ? "null" : v.ToString();
            return Encoding.UTF8.GetBytes(line + "\n");
        }
    }
}
