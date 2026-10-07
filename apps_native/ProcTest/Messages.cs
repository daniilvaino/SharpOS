using SharpOS.Std.Pipes;

namespace PipeApps
{
    /// <summary>
    /// Not PIPEGEN's record: the same name with a field more, so that a pipe
    /// between PIPEGEN and this program fails its type check on a field
    /// (step194 §7, test 4). The real one is in PipeGen/LogEntry.cs.
    /// </summary>
    [Message]
    public sealed class LogEntry
    {
        public int Level;
        public long Stamp;
        public string Text;
    }

    /// <summary>Where a process lives: its image, a static of it, an object of its heap.</summary>
    [Message]
    public sealed class Where
    {
        public ulong Image;
        public ulong Static;
        public ulong Heap;
        public uint Id;
    }

    /// <summary>A number in a sequence.</summary>
    [Message]
    public sealed class Seq
    {
        public int N;
        public string Text;
    }

    /// <summary>An address handed from one process to another.</summary>
    [Message]
    public sealed class Address
    {
        public ulong Block;
    }

    public interface IHasArea
    {
        int Area();
    }

    /// <summary>A type read in place with its own virtual and interface methods (test 3).</summary>
    [Message]
    public class Shape : IHasArea
    {
        public int Sides;
        public string Name;

        public virtual int Corners() => Sides;
        public virtual int Area() => 0;
    }

    [Message]
    public sealed class Square : Shape
    {
        public int Side;

        public override int Corners() => 4;
        public override int Area() => Side * Side;
    }
}
