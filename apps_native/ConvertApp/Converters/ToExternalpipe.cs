using System;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --to externalpipe: any messages into the bytes of an externalpipe as
    // byte[] messages — the header, then one per message (step206). What
    // `> file` writes for a stream of objects, as a stage of its own.
    internal sealed unsafe class ToExternalpipe : IConverter
    {
        public string Direction => "to";
        public string Format => "externalpipe";
        public string Options => "";

        public int Run(string[] options)
        {
            RawPipeReader input = Pipe.Read();
            using PipeWriter<byte[]> output = Pipe.Write<byte[]>();
            PipeTransport.WaitSchema(input.Handle);
            byte[] schema = PipeTransport.Schema(input.Handle, out ulong rootKey);
            if (schema == null)
            {
                // The writer left without a type: no messages, no bytes.
                input.Dispose();
                return 0;
            }
            ExternalPipe.Copy(input, schema, rootKey, true, (first, second) =>
            {
                var piece = new byte[first.Length + second.Length];
                first.CopyTo(piece);
                second.CopyTo(new Span<byte>(piece, first.Length, second.Length));
                output.Copy(piece);
            });
            input.Dispose();
            return 0;
        }
    }
}
