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
            int plain = 0;                      // 0 not asked, 1 terminal text, 2 not
            foreach (View v in Pipe.Read())
            {
                if (v.TryGetChars(out char* chars, out int length))
                {
                    // Terminal text loses the terminal's controls (step242).
                    if (plain == 0) plain = Pipe.InputIsConsoleText ? 1 : 2;
                    output.Copy(plain == 1 ? Pipe.PlainText(new ReadOnlySpan<char>(chars, length))
                                           : new string(new ReadOnlySpan<char>(chars, length)));
                }
                else
                    output.Copy(v.IsNull ? "null" : v.ToString());
            }
            return 0;
        }
    }
}
