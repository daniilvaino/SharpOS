using System;
using System.IO;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --from externalpipe: the bytes of an externalpipe back into its messages,
    // under the description it holds (step206) — what READ does with such a
    // file, for bytes that come down a pipe. No bytes: no messages. Not an
    // externalpipe, or a broken one: said, exit code 1.
    internal sealed class FromExternalpipe : IConverter
    {
        public string Direction => "from";
        public string Format => "externalpipe";
        public string Options => "";

        public int Run(string[] options)
        {
            using Stream input = Pipe.ReadBytes();
            var file = new ExternalPipeReader(input.Read);
            if (!file.ReadHeader())
            {
                if (file.Complaint == null) return 0;
                Console.Error.WriteLine("CONVERT --from externalpipe: " + file.Complaint);
                return 1;
            }
            return ExternalPipe.Replay(file, "CONVERT --from externalpipe");
        }
    }
}
