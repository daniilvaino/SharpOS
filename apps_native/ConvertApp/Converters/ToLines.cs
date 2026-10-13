using System;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --to lines: each message as the line the screen shows for it, a string
    // message as itself (step206). What WRITE did with objects until it kept
    // them as an externalpipe; now asked for.
    internal sealed unsafe class ToLines : IConverter
    {
        public string Direction => "to";
        public string Format => "lines";
        public string Options => "";

        public int Run(string[] options)
        {
            using PipeWriter<string> output = Pipe.Write<string>();
            foreach (View v in Pipe.Read())
            {
                if (v.TryGetChars(out char* chars, out int length))
                    output.Copy(new string(new ReadOnlySpan<char>(chars, length)));
                else
                    output.Copy(v.IsNull ? "null" : v.ToString());
            }
            return 0;
        }
    }
}
