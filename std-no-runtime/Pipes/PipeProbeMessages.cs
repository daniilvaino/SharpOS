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

    /// <summary>One field that can hold anything: what the refusal tests put a stranger into.</summary>
    [Message]
    public sealed class Envelope
    {
        public object Payload;
        public int[,] Grid;
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
