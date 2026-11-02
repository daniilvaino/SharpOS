using System.IO;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --from lines: bytes (or text) into a string per line of the file —
    // LF and CRLF ends, a UTF-8 BOM skipped (StreamReader). Written through
    // Pipe.WriteText, as READ --lines is: a line goes on as soon as its end
    // arrives, and one longer than PipeTextWriter.MaxLineChars goes in pieces —
    // a stream with no line feeds (a boot log's zeroed sectors) would otherwise
    // be one string larger than a message may be.
    internal sealed class FromLines : IConverter
    {
        public string Direction => "from";
        public string Format => "lines";
        public string Options => "";

        public int Run(string[] options)
        {
            using TextReader input = Pipe.ReadText();
            using TextWriter output = Pipe.WriteText();
            var buffer = new char[4096];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(new string(buffer, 0, n));
            return 0;
        }
    }
}
