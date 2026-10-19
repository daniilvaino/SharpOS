// A large message (step208): 5000 records, each with numbers, two strings, an
// int[8] and a nested object with a string of its own - 30 001 objects. The
// same file in PIPEPERF (NativeAOT; through a pipe and through BinaryWriter)
// and in apps_managed/Bench (CoreCLR; BinaryWriter), so the BinaryWriter code
// is one code on both runtimes. [Message] is the pipes' catalog; Bench has
// an empty attribute of the same name (MessageAttribute.cs).
using System.IO;
using SharpOS.Std.Pipes;

namespace SharpOS.Micro
{
    [Message]
    public sealed class BigChild
    {
        public int A;
        public string B;
    }

    [Message]
    public sealed class BigRecord
    {
        public int Id;
        public long Stamp;
        public double Value;
        public string Name;
        public string Text;
        public int[] Tags;
        public BigChild Child;
    }

    public static class BigGraph
    {
        public const int Records = 5000;

        /// <summary>The array, and per record: itself, two strings, its tags, its child, the child's string.</summary>
        public const int Objects = 1 + Records * 6;

        public static BigRecord[] Build()
        {
            var items = new BigRecord[Records];
            for (int i = 0; i < Records; i++)
            {
                var tags = new int[8];
                for (int t = 0; t < tags.Length; t++) tags[t] = i * 8 + t;
                items[i] = new BigRecord
                {
                    Id = i,
                    Stamp = 638_000_000_000_000_000L + i,
                    Value = i * 0.5,
                    Name = "record-" + i.ToString(),
                    Text = "some text of record number " + i.ToString() + " with a tail",
                    Tags = tags,
                    Child = new BigChild { A = i, B = "child " + i.ToString() },
                };
            }
            return items;
        }

        public static void Write(BinaryWriter w, BigRecord[] items)
        {
            w.Write(items.Length);
            foreach (BigRecord r in items)
            {
                w.Write(r.Id);
                w.Write(r.Stamp);
                w.Write(r.Value);
                w.Write(r.Name);
                w.Write(r.Text);
                w.Write(r.Tags.Length);
                foreach (int t in r.Tags) w.Write(t);
                w.Write(r.Child.A);
                w.Write(r.Child.B);
            }
        }

        public static BigRecord[] Read(BinaryReader r)
        {
            var items = new BigRecord[r.ReadInt32()];
            for (int i = 0; i < items.Length; i++)
            {
                var rec = new BigRecord
                {
                    Id = r.ReadInt32(),
                    Stamp = r.ReadInt64(),
                    Value = r.ReadDouble(),
                    Name = r.ReadString(),
                    Text = r.ReadString(),
                };
                var tags = new int[r.ReadInt32()];
                for (int t = 0; t < tags.Length; t++) tags[t] = r.ReadInt32();
                rec.Tags = tags;
                rec.Child = new BigChild { A = r.ReadInt32(), B = r.ReadString() };
                items[i] = rec;
            }
            return items;
        }
    }
}
