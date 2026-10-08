using System.IO;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --from lines: bytes (or text) into a string per line of the file —
    // LF and CRLF ends, a UTF-8 BOM skipped (StreamReader does both).
    internal sealed class FromLines : IConverter
    {
        public string Direction => "from";
        public string Format => "lines";
        public string Options => "";

        public int Run(string[] options)
        {
            using TextReader input = Pipe.ReadText();
            using PipeWriter<string> output = Pipe.Write<string>();
            string line;
            while ((line = input.ReadLine()) != null)
                output.Copy(line);
            return 0;
        }
    }
}
