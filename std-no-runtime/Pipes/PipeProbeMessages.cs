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

namespace SharpOS.Std.Pipes.Probe
{
    /// <summary>
    /// The std types test, the same code on both sides: strings, bytes and an
    /// Expando carrying every std structure, built by one image and checked
    /// by the other. The point is that std types have the same key in every
    /// image, so neither side declares anything.
    /// </summary>
    public static unsafe class StdProbe
    {
        public const int Texts = 3;

        public static string Text(int i)
            => i == 0 ? "plain ascii" : i == 1 ? "кириллица и 世界" : "";

        public const int ByteArrays = 3;

        public static int ByteLength(int i) => i == 0 ? 1 : i == 1 ? 4096 : 100_000;

        public static byte[] Bytes(int length)
        {
            var bytes = new byte[length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i * 7 + 1);
            return bytes;
        }

        public static bool BytesOk(byte[] bytes, int length)
        {
            if (bytes == null || bytes.Length != length) return false;
            for (int i = 0; i < bytes.Length; i++)
                if (bytes[i] != (byte)(i * 7 + 1)) return false;
            return true;
        }

        private static System.Guid SampleGuid
            => new System.Guid(0x12345678, 0x1234, 0x5678, 0x9A, 0xBC, 0xDE, 0xF0, 0x11, 0x22, 0x33, 0x44);

        public static Expando Sample(string who)
        {
            var child = new Expando();
            child["Depth"] = 1;
            child["Who"] = who;

            var e = new Expando();
            e["Name"] = "sample from " + who;
            e["Count"] = 42;
            e["Big"] = 1L << 40;
            e["Ratio"] = 0.25;
            e["Flag"] = true;
            e["Letter"] = 'ж';
            e["When"] = new System.DateTime(2026, 10, 5, 12, 30, 0);
            e["Span"] = System.TimeSpan.FromSeconds(90);
            e["Id"] = SampleGuid;
            e["Key"] = new System.ConsoleKeyInfo('q', System.ConsoleKey.Q, false, true, false);
            e["At"] = new System.Numerics.Vector3(1, 2, 3);
            e["Values"] = new[] { 1, 2, 3 };
            e["Bytes"] = new byte[] { 9, 8, 7 };
            e["Names"] = new[] { "a", "b" };
            e["Child"] = child;
            e["Nothing"] = null;
            return e;
        }

        /// <summary>Null when <paramref name="e"/> is Sample(<paramref name="who"/>); otherwise the first field that is not.</summary>
        public static string Check(Expando e, string who)
        {
            if (e == null) return "no Expando";
            if (e.Count != 16) return "Count " + e.Count.ToString();
            if (!(e["Name"] is string name) || name != "sample from " + who) return "Name";
            if (!(e["Count"] is int count) || count != 42) return "Count";
            if (!(e["Big"] is long big) || big != 1L << 40) return "Big";
            if (!(e["Ratio"] is double ratio) || ratio != 0.25) return "Ratio";
            if (!(e["Flag"] is bool flag) || !flag) return "Flag";
            if (!(e["Letter"] is char letter) || letter != 'ж') return "Letter";
            if (!(e["When"] is System.DateTime when) || when != new System.DateTime(2026, 10, 5, 12, 30, 0)) return "When";
            if (!(e["Span"] is System.TimeSpan span) || span != System.TimeSpan.FromSeconds(90)) return "Span";
            if (!(e["Id"] is System.Guid id) || !SameGuid(id, SampleGuid)) return "Id";
            if (!(e["Key"] is System.ConsoleKeyInfo key) || key.KeyChar != 'q' || key.Key != System.ConsoleKey.Q
                || key.Modifiers != System.ConsoleModifiers.Alt) return "Key";
            if (!(e["At"] is System.Numerics.Vector3 at) || at.X != 1 || at.Y != 2 || at.Z != 3) return "At";
            if (!(e["Values"] is int[] values) || values.Length != 3 || values[0] != 1 || values[2] != 3) return "Values";
            if (!(e["Bytes"] is byte[] bytes) || bytes.Length != 3 || bytes[0] != 9 || bytes[2] != 7) return "Bytes";
            if (!(e["Names"] is string[] names) || names.Length != 2 || names[0] != "a" || names[1] != "b") return "Names";
            if (!(e["Child"] is Expando child) || child.Count != 2 || !(child["Depth"] is int depth) || depth != 1
                || !(child["Who"] is string childWho) || childWho != who) return "Child";
            if (!e.ContainsKey("Nothing") || e["Nothing"] != null) return "Nothing";
            int i = 0;
            foreach (System.Collections.Generic.KeyValuePair<string, object> field in e)
            {
                if (i == 0 && field.Key != "Name") return "order";
                if (i == 15 && field.Key != "Nothing") return "order";
                i++;
            }
            return i == 16 ? null : "enumeration";
        }

        private static bool SameGuid(System.Guid a, System.Guid b)
        {
            byte* x = (byte*)&a;
            byte* y = (byte*)&b;
            for (int i = 0; i < 16; i++)
                if (x[i] != y[i]) return false;
            return true;
        }
    }
}

namespace SharpOS.Std.Pipes.Probe
{
    /// <summary>An enum both sides have: its member names travel in the description.</summary>
    [Message]
    public enum ProbeMood { Calm = 1, Deep = 2 }

    /// <summary>
    /// The view test, the same code on both sides. Each side has its own
    /// report class the other does not — the kernel's KernelReport, the app's
    /// AppReport — with the same field names and these values, and an Expando
    /// carrying the same; one check, through a view, reads all three.
    /// </summary>
    public static class ViewProbe
    {
        public const byte B = 200;
        public const sbyte SB = -100;
        public const short S = -30000;
        public const ushort US = 60000;
        public const int I = -2_000_000_000;
        public const uint UI = 4_000_000_000;
        public const long L = -9_000_000_000_000;
        public const ulong UL = 18_000_000_000_000_000_000;
        public const float F = 1.5f;
        public const double D = -2.25;
        public const char C = 'ж';
        public const string Text = "отчёт";
        public static System.DateTime When => new System.DateTime(2026, 10, 6, 8, 0, 0);
        public static int[] Numbers => new[] { 1, 2, 3 };
        public static string[] Words => new[] { "a", "b" };
        public static ProbeMood[] Moods => new[] { ProbeMood.Deep, ProbeMood.Calm };

        public static Expando Sample()
        {
            var where = new Expando();
            where["Name"] = "hall";
            where["Floor"] = 3;
            var a = new Expando();
            a["Name"] = "a";
            a["Floor"] = 4;
            var b = new Expando();
            b["Name"] = "b";
            b["Floor"] = 5;

            var e = new Expando();
            e["B"] = B; e["SB"] = SB; e["S"] = S; e["US"] = US; e["I"] = I; e["UI"] = UI;
            e["L"] = L; e["UL"] = UL; e["F"] = F; e["D"] = D; e["Flag"] = true; e["C"] = C;
            e["Text"] = Text;
            e["Mood"] = ProbeMood.Deep;
            e["Moods"] = Moods;
            e["Where"] = where;
            e["Nowhere"] = null;
            e["Numbers"] = Numbers;
            e["Words"] = Words;
            e["Places"] = new object[] { a, b };
            e["When"] = When;
            e["Next"] = e;
            return e;
        }

        /// <summary>Null when the view shows a report with these values; otherwise the first field that does not.</summary>
        public static string Check(View v)
        {
            if (v.Kind != ViewKind.Object) return "kind " + v.Kind.ToString();
            if (!(v["B"] == B) || (byte)v["B"] != B) return "B";
            if (!(v["SB"] == SB)) return "SB";
            if (!(v["S"] == S)) return "S";
            if (!(v["US"] == US)) return "US";
            if (!(v["I"] == I) || (int)v["I"] != I) return "I";
            if (!(v["UI"] == UI)) return "UI";
            if (!(v["L"] == L)) return "L";
            if ((ulong)v["UL"] != UL) return "UL";
            if (!(v["F"] == 1.5) || (float)v["F"] != F) return "F";
            if (!(v["D"] == D) || !(v["D"] < 0)) return "D";
            if (!v["Flag"] || !(v["Flag"] == true)) return "Flag";
            if ((char)v["C"] != C) return "C";
            if (!(v["Text"] == Text) || (string)v["Text"] != Text || v["Text"].Kind != ViewKind.String) return "Text";
            if (!(v["Mood"] == 2) || v["Mood"].ToString() != "Deep") return "Mood";
            View moods = v["Moods"];
            if (moods.Length != 2 || !(moods[1] == 1) || moods[1].ToString() != "Calm") return "Moods";
            if (!(v["Where"]["Name"] == "hall") || !(v["Where"]["Floor"] == 3)) return "Where";
            if (!v["Nowhere"].IsNull) return "Nowhere";
            View numbers = v["Numbers"];
            if (numbers.Kind != ViewKind.Array || numbers.Length != 3 || !(numbers[2] == 3)) return "Numbers";
            if (v["Words"].Length != 2 || !(v["Words"][1] == "b")) return "Words";
            View places = v["Places"];
            if (places.Length != 2 || !(places[1]["Floor"] == 5) || !(places[0]["Name"] == "a")) return "Places";
            if (!(v["When"]["_ticks"] == When.Ticks)) return "When";
            if (!(v["Next"]["I"] == I) || !(v["Next"]["Next"]["Text"] == Text)) return "Next";
            if (v.Has("Missing") || !v.Has("Text")) return "Has";
            int named = 0;
            foreach (ViewField f in v.Fields)
                if (f.Name == "Text" || f.Name == "Mood") named++;
            if (named != 2) return "Fields";
            return null;
        }
    }
}

namespace SharpOS.Std.Pipes.Probe
{
    // The devirtualization detector. ILC sees the whole program and knows which
    // types it constructs; a type an image only RECEIVES — through a pipe, Into,
    // ToHeap — is never constructed there. Each side constructs only some of
    // these: the kernel B, the app A; C is what the app's Into produces.
    public abstract class DevirtBase
    {
        public abstract int Value();
        public virtual string Name() => "base";
    }

    public interface IDevirt
    {
        int Kind();
    }

    [Message]
    public sealed class DevirtA : DevirtBase, IDevirt
    {
        public int X;
        public override int Value() => 100 + X;
        public override string Name() => "A";
        public int Kind() => 1;
    }

    [Message]
    public sealed class DevirtB : DevirtBase, IDevirt
    {
        public int X;
        public override int Value() => 200 + X;
        public override string Name() => "B";
        public int Kind() => 2;
    }

    [Message]
    public sealed class DevirtC : DevirtBase, IDevirt
    {
        public int X;
        public override int Value() => 300 + X;
        public override string Name() => "C";
        public int Kind() => 3;
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
