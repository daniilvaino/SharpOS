using System;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // The same shape as PIPEGEN's record: Into<T> lays out by name, so any
    // class with these fields takes it.
    [Message]
    public sealed class LogEntry
    {
        public int Level;
        public string Text;
    }

    public enum Shade
    {
        Red = 1,
        Green = 2,
        Blue = 40,
    }

    [Message]
    public sealed class Inner
    {
        public string Name;
        public double Weight;
    }

    // Test 3: everything --to json writes in a form of its own, and back.
    [Message]
    public sealed class Record
    {
        public int Id;
        public Shade Color;
        public DateTime When;
        public Guid Key;
        public TimeSpan Span;
        public long Big;
        public bool Flag;
        public string Note;
        public Inner Child;
        public int[] Numbers;
        public Inner[] Items;
    }

    // Test 5: a record of about a hundred bytes of JSON.
    [Message]
    public sealed class Row
    {
        public long Id;
        public int Level;
        public string Name;
        public double Value;
    }
}
