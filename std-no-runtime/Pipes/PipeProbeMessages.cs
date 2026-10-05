namespace SharpOS.Std.Pipes.Probe
{
    // Message types of the native pipe tests that both sides know: the kernel
    // (OS.Kernel.Diagnostics.PipeProbe) and AotTests compile this one source.

    /// <summary>A numbered note: order, loss and limit tests.</summary>
    [Message]
    public sealed class Note
    {
        public int Seq;
        public string Text;
        public int[] Values;
    }

    [Message]
    public sealed class Item
    {
        public int Key;
        public string Name;
    }

    /// <summary>What the echo test sends, changes and sends back.</summary>
    [Message]
    public sealed class EchoMessage
    {
        public int Value;
        public string Tag;
        public Item[] Items;
    }

    /// <summary>
    /// A value with references in it, large enough that one element copied
    /// onto another is a memory-to-memory copy: the compiler moves each
    /// reference with RhpByRefAssignRef. (Two fields fit in registers and go
    /// through RhpCheckedAssignRef instead.)
    /// </summary>
    [Message]
    public struct Tagged
    {
        public int Key;
        public string Name;
        public string Alias;
        public string Note;
        public long Stamp;
    }

    /// <summary>What the byref barrier test copies elements of.</summary>
    [Message]
    public sealed class TaggedList
    {
        public Tagged[] Entries;
    }

    /// <summary>One field that can hold anything: what the refusal tests put a stranger into.</summary>
    [Message]
    public sealed class Envelope
    {
        public object Payload;
        public int[,] Grid;
    }
}

namespace SharpOS.Std.Pipes.Probe
{
    /// <summary>
    /// The byref barrier test, the same code on both sides: a struct with a
    /// reference copied element to element in a region. Inside the block it
    /// passes; carrying a heap reference it is refused, the reference unchanged.
    /// </summary>
    public static class ByRefProbe
    {
        public const int InsidePassed = 1;
        public const int OutsideRefused = 2;

        public static TaggedList Sample()
        {
            var entries = new Tagged[4];
            for (int i = 0; i < entries.Length; i++)
                entries[i] = new Tagged { Key = i, Name = "t" + i.ToString(), Alias = "a", Note = "n", Stamp = i };
            return new TaggedList { Entries = entries };
        }

        // An element copied from one array slot to another: both sides are
        // memory, so the compiler copies the reference with the byref helper.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void CopyElement(Tagged[] into, int at, Tagged[] from, int index) => into[at] = from[index];

        /// <summary>Runs both copies on a received region; InsidePassed | OutsideRefused when both behave.</summary>
        public static int Run(TaggedList list)
        {
            int result = 0;
            CopyElement(list.Entries, 0, list.Entries, 3);
            if (list.Entries[0].Key == 3 && ReferenceEquals(list.Entries[0].Name, list.Entries[3].Name))
                result |= InsidePassed;

            Tagged[] heap = { new Tagged { Key = 9, Name = new string('h', 2), Alias = new string('i', 2), Note = new string('j', 2) } };
            bool refused = false;
            try { CopyElement(list.Entries, 1, heap, 0); }
            catch (RegionReferenceException) { refused = true; }
            if (refused && list.Entries[1].Name == "t1")
                result |= OutsideRefused;
            return result;
        }

        public static int RunOnPipe()
        {
            Pipe.Create<TaggedList>(4, PipeOverflow.DropOldest, out PipeWriter<TaggedList> w, out PipeReader<TaggedList> r);
            int result = -1;
            if (w.Copy(Sample()) == PipeStatus.Ok)
            {
                Region<TaggedList> region = r.Receive();
                if (region != null)
                {
                    result = Run(region.Root);
                    region.Dispose();
                }
            }
            w.Dispose();
            r.Dispose();
            return result;
        }
    }
}

namespace SharpOS.Std.Pipes.Probe.A
{
    /// <summary>Same short name and layout as B.Twin; the full name tells them apart.</summary>
    [Message]
    public sealed class Twin
    {
        public int X;
        public int Y;
    }
}

namespace SharpOS.Std.Pipes.Probe.B
{
    [Message]
    public sealed class Twin
    {
        public int X;
        public int Y;
    }
}
