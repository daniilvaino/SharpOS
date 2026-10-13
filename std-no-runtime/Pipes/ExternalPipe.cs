using System;
using System.Collections.Generic;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>Bytes handed on: a header (with an empty body) or a record's length and its block.</summary>
    public delegate void ExternalPipeSink(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second);

    /// <summary>Bytes read into the span; 0 at the end.</summary>
    public delegate int ExternalPipeSource(Span<byte> into);

    /// <summary>
    /// externalpipe (step206): a pipe's messages kept in bytes — what `&gt; file`
    /// writes and READ gives back, the same messages under the same
    /// description, class or Expando, with no class needed to read them.
    /// </summary>
    /// <remarks>
    /// A block in a pipe holds references as offsets from its own start and
    /// keys instead of type pointers, so it is already independent of where it
    /// lies and of who wrote it. The file is the pipe's description once, then
    /// the blocks as they came:
    ///
    ///     "SharpOS externalpipe 1\n"   the signature, ASCII
    ///     u64 root key, u32 n, n bytes of the description (RegionSchema)
    ///     then per message: u64 length, the block's bytes
    ///
    /// Little-endian. A reader checks a block against the description as it
    /// would any writer's: a broken file refuses where it is read.
    /// </remarks>
    public static unsafe class ExternalPipe
    {
        public static ReadOnlySpan<byte> Signature => "SharpOS externalpipe 1\n"u8;

        // A block longer than this is a broken length, not a message.
        internal const ulong MaxBlock = 1UL << 32;

        public static bool StartsWithSignature(ReadOnlySpan<byte> bytes)
            => bytes.Length >= Signature.Length && bytes.Slice(0, Signature.Length).SequenceEqual(Signature);

        /// <summary>The signature, the root key and the description.</summary>
        public static byte[] Header(byte[] schema, ulong rootKey)
        {
            int at = Signature.Length;
            var header = new byte[at + 12 + schema.Length];
            Signature.CopyTo(header);
            fixed (byte* p = header)
            {
                *(ulong*)(p + at) = rootKey;
                *(uint*)(p + at + 8) = (uint)schema.Length;
            }
            schema.CopyTo(header, at + 12);
            return header;
        }

        /// <summary>
        /// Every message of <paramref name="input"/> to <paramref name="sink"/>,
        /// the header first when <paramref name="header"/>; the messages copied.
        /// The description must be known (PipeTransport.WaitSchema). A broken
        /// input throws, as in any loop.
        /// </summary>
        public static long Copy(RawPipeReader input, byte[] schema, ulong rootKey, bool header, ExternalPipeSink sink)
        {
            if (header) sink(Header(schema, rootKey), default);
            ulong length;
            var prefix = new ReadOnlySpan<byte>(&length, 8);
            long count = 0;
            RawRegion region = null;
            while ((region = input.Next(region)) != null)
            {
                try
                {
                    length = region.Length;
                    sink(prefix, new ReadOnlySpan<byte>(region.Block, checked((int)region.Length)));
                }
                finally
                {
                    region.Dispose();
                }
                count++;
            }
            return count;
        }

        /// <summary>The root type's name in a description, or its key.</summary>
        public static string RootName(byte[] schema, ulong rootKey)
        {
            Dictionary<ulong, TypeKeys.Description> types = schema == null ? null : RegionSchema.Parse(schema, out _);
            return types != null && types.TryGetValue(rootKey, out TypeKeys.Description d) ? d.Name : "0x" + rootKey.ToString("x");
        }

        /// <summary>
        /// One stream: the same root, and the same set of types. Keys hold the
        /// name and the layout, so equal keys are one type; the whole set, as a
        /// nested type's layout is not in its owner's key.
        /// </summary>
        public static bool SameTypes(byte[] a, ulong rootA, byte[] b, ulong rootB)
        {
            if (rootA != rootB || a == null || b == null) return false;
            Dictionary<ulong, TypeKeys.Description> left = RegionSchema.Parse(a, out _);
            Dictionary<ulong, TypeKeys.Description> right = RegionSchema.Parse(b, out _);
            if (left == null || right == null || left.Count != right.Count) return false;
            foreach (ulong key in left.Keys)
                if (!right.ContainsKey(key)) return false;
            return true;
        }

        /// <summary>
        /// The messages of <paramref name="file"/> (its header read) to the
        /// standard output under the file's description; on the screen when
        /// the output was handed to nobody. The exit code: 0; 1 for a broken
        /// file — said, the output broken; 141 when the output broke.
        /// </summary>
        public static int Replay(ExternalPipeReader file, string program)
        {
            int output = Pipe.TakeOutput();
            RegionShapes shapes = null;
            try
            {
                if (output != 0)
                {
                    PipeStatus opened = PipeTransport.OpenEnd(output, file.Schema, file.RootKey, out string error);
                    if (opened != PipeStatus.Ok)
                    {
                        PipeTransport.Print(program + ": the output: " + (error ?? Pipe.Explain(opened)));
                        return 1;
                    }
                }
                else
                {
                    shapes = RegionShapes.Parse(file.Schema, out string bad);
                    if (shapes == null)
                    {
                        PipeTransport.Print(program + ": the file's description is malformed: " + bad);
                        return 1;
                    }
                }

                void* block;
                while ((block = file.Next(out ulong length)) != null)
                {
                    if (output == 0)
                    {
                        var region = new RawRegion((byte*)block, length, file.Schema, 0, shapes);
                        try { ScreenText.Print(region.Root); }
                        finally { region.Dispose(); }
                        continue;
                    }
                    PipeStatus sent = PipeTransport.Send(output, block, length);
                    if (sent == PipeStatus.Ok) continue;
                    PipeTransport.Free(block);
                    if (sent == PipeStatus.Broken) return 141;
                    PipeTransport.Print(program + ": the output: " + Pipe.Explain(sent));
                    return 1;
                }
                if (file.Complaint == null) return 0;
                PipeTransport.Print(program + ": " + file.Complaint);
                if (output != 0)
                {
                    // What came is not the whole stream: the next stage must not take it for one.
                    PipeTransport.Break(output);
                    output = 0;
                }
                return 1;
            }
            finally
            {
                if (output != 0) PipeTransport.End(output);
            }
        }
    }

    /// <summary>An externalpipe read from its bytes: the header, then a block per message.</summary>
    public sealed unsafe class ExternalPipeReader
    {
        private readonly ExternalPipeSource _source;

        public ExternalPipeReader(ExternalPipeSource source) => _source = source;

        public byte[] Schema { get; private set; }
        public ulong RootKey { get; private set; }

        /// <summary>Messages read so far.</summary>
        public long Count { get; private set; }

        /// <summary>Why reading stopped short: a broken header, a cut or broken message; null at a clean end.</summary>
        public string Complaint { get; private set; }

        /// <summary>
        /// The signature and the description. False with a complaint when they
        /// are not there; false without one when the bytes are none at all.
        /// </summary>
        public bool ReadHeader()
        {
            int length = ExternalPipe.Signature.Length;
            byte* head = stackalloc byte[length + 12];
            int got = Fill(new Span<byte>(head, length + 12));
            if (got == 0) return false;
            if (got < length + 12 || !ExternalPipe.StartsWithSignature(new ReadOnlySpan<byte>(head, length)))
                return Fail("not an externalpipe: the signature or the header is missing");
            RootKey = *(ulong*)(head + length);
            uint size = *(uint*)(head + length + 8);
            if (size == 0 || size > 16u << 20) return Fail("the header gives a description of " + size.ToString() + " bytes");
            var schema = new byte[size];
            if (Fill(schema) != schema.Length) return Fail("the file ends inside the description");
            if (RegionSchema.Parse(schema, out string bad) == null) return Fail("the description is malformed: " + bad);
            Schema = schema;
            return true;
        }

        /// <summary>
        /// The next message's block, in the exchange arena — the caller sends
        /// or frees it; null at the end, or with a complaint.
        /// </summary>
        public void* Next(out ulong length)
        {
            length = 0;
            if (Complaint != null) return null;
            ulong size;
            int got = Fill(new Span<byte>(&size, 8));
            if (got == 0) return null;
            if (got < 8) return FailNull("the file ends inside the length of message " + (Count + 1).ToString());
            if (size == 0 || size > ExternalPipe.MaxBlock)
                return FailNull("message " + (Count + 1).ToString() + " gives a length of " + size.ToString());
            void* block = PipeTransport.Allocate(size);
            if (block == null)
                return FailNull("no room for message " + (Count + 1).ToString() + " of " + size.ToString() + " bytes");
            ulong at = 0;
            while (at < size)
            {
                int piece = (int)Math.Min(size - at, 1UL << 30);
                int n = Fill(new Span<byte>((byte*)block + at, piece));
                at += (ulong)n;
                if (n < piece) break;
            }
            if (at < size)
            {
                PipeTransport.Free(block);
                return FailNull("the file ends inside message " + (Count + 1).ToString());
            }
            Count++;
            length = size;
            return block;
        }

        // The span filled, or what was left.
        private int Fill(Span<byte> into)
        {
            int got = 0;
            while (got < into.Length)
            {
                int n = _source(into.Slice(got));
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        private bool Fail(string why)
        {
            Complaint = why;
            return false;
        }

        private void* FailNull(string why)
        {
            Complaint = why;
            return null;
        }
    }
}
