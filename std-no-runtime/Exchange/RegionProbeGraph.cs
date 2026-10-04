using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpOS.Std.Pipes;

namespace SharpOS.Std.Exchange.Probe
{
    // The graph the region experiments send between images (pipe_plan.md,
    // "Проверить опытом"). One source for producer and receiver: both compile
    // it, so their keys agree exactly when their layouts do.
    //
    // Every class with a virtual or interface method has at least two
    // implementations that are all constructed (the witnesses below): with one,
    // ILC would turn the call into a direct call, and a call on an untranslated
    // object would never read its table word.

    [Message]
    public sealed class Bag
    {
        public string[] Keys;
        public object[] Values;
    }

    [Message]
    public sealed class SelfRef
    {
        public string Text;
        public SelfRef Me;
        public object Any;
        public byte Small;
        public int Middle;
        public long Big;
    }

    [Message]
    public sealed class IntBox
    {
        public int Value;
    }

    [Message]
    public sealed class TextBox
    {
        public string Value;
    }

    [Message]
    public abstract class Base
    {
        public int Tag;
        public abstract int Rank();
    }

    [Message]
    public sealed class Derived : Base, ILabelled, ITriple
    {
        public override int Rank() => 2;
        public int Label() => 20;
        public int Triple() => 21;
    }

    [Message]
    public sealed class Other : Base, ILabelled, ISingle, ITriple
    {
        public override int Rank() => 3;
        public int Label() => 30;
        public int Single() => 31;
        public int Triple() => 32;
    }

    public interface IRanked
    {
        int Sides();
    }

    // Four implementations, so ILC keeps a real interface dispatch. With two
    // (IRanked) it compiles a call into one table compare and assumes the
    // other type when the compare fails.
    public interface ILabelled
    {
        int Label();
    }

    [Message]
    public sealed class Shaped : IRanked, ILabelled
    {
        public int Sides() => 5;
        public int Label() => 50;
    }

    [Message]
    public sealed class Squared : IRanked, ILabelled, ITriple
    {
        public int Sides() => 4;
        public int Label() => 40;
        public int Triple() => 41;
    }

    // How ILC compiles an interface call by the number of implementations,
    // measured on an untranslated object (pipe_plan.md "Проверить опытом", 3):
    // one (ISingle), two (IRanked), three (ITriple), four (ILabelled).
    public interface ISingle
    {
        int Single();
    }

    public interface ITriple
    {
        int Triple();
    }

    [Message]
    public struct Pair
    {
        public string Name;
        public int X;
    }

    [Message]
    public enum Tint
    {
        Pale,
        Amber,
        Deep,
    }

    [Message]
    public sealed class Holder
    {
        public Base Base;
        public IRanked Ranked;
        public TextBox Text;
        public Pair[] Pairs;
        public object Enum;
        public object RefStruct;
        public int[] Numbers;
    }

    public static unsafe class RegionProbeGraph
    {
        /// <summary>Length of the int array in the graph both sides build and check.</summary>
        public const int Numbers = 2000;

        private static bool s_declared;
        private static int s_problems;

        /// <summary>Types the graph uses that could not be declared (a key taken twice).</summary>
        public static int Problems => s_problems;

        /// <summary>
        /// Declares every type of the graph, once per image: they are [Message]
        /// types, so this is the catalog's own registration.
        /// </summary>
        public static void Declare()
        {
            if (s_declared)
                return;
            s_declared = true;
            MessageCatalog.Ensure();
            s_problems = MessageCatalog.Problems.Count;
        }

        /// <summary>
        /// A cycle, a shared reference, a reference back to the root, null, boxes,
        /// strings, arrays of primitives, of references and of structs with
        /// references, a base-typed field holding a derived object, an interface
        /// field, a boxed enum and a boxed struct.
        /// </summary>
        /// <param name="numbers">Length of the int array: grows the region on demand.</param>
        public static object Build(int numbers)
        {
            var self = new SelfRef
            {
                Text = "hello",
                Small = 7,
                Middle = 0x1234,
                Big = 0x1122334455667788,
                Any = new IntBox { Value = 389 },
            };
            self.Me = self;

            var values = new int[numbers];
            for (int i = 0; i < values.Length; i++)
                values[i] = i * 3;

            var holder = new Holder
            {
                Base = new Derived { Tag = 17 },
                Ranked = new Shaped(),
                Text = new TextBox { Value = "boxed" },
                Pairs = new[] { new Pair { Name = "a", X = 1 }, new Pair { Name = null, X = 2 }, new Pair { Name = "c", X = 3 } },
                Enum = Tint.Deep,
                RefStruct = new Pair { Name = "ref", X = 99 },
                Numbers = values,
            };

            var inner = new Bag { Keys = new[] { "x", "up" }, Values = new object[] { "y", null } };
            var root = new Bag
            {
                Keys = new[] { "number", "text", "array", "self", "inner", "none", "again", "holder" },
                Values = new object[] { 42, "text", new[] { 1, 2, 3 }, self, inner, null, self, holder },
            };
            inner.Values[1] = root;
            return root;
        }

        /// <summary>
        /// Checks a graph against what Build made. Returns the failures; with
        /// <paramref name="say"/> null it allocates nothing, so it can run in a
        /// loop while a collector works.
        /// </summary>
        public static int Check(object root, int numbers, Action<string> say)
        {
            int failed = 0;

            var bag = root as Bag;
            failed += Expect(say, "root is Bag", bag != null);
            if (bag == null)
                return failed;

            failed += Expect(say, "Keys is string[8], Keys[1] == \"text\"",
                             bag.Keys is string[] && bag.Keys.Length == 8 && bag.Keys[1] == "text");
            failed += Expect(say, "Values is object[8]", bag.Values is object[] && bag.Values.Length == 8);
            if (bag.Values == null || bag.Values.Length != 8)
                return failed;

            failed += Expect(say, "Values[0] is int 42", bag.Values[0] is int i && i == 42);
            failed += Expect(say, "Values[1] is string \"text\"", bag.Values[1] is string s && s == "text");
            failed += Expect(say, "Values[2] is int[] {1,2,3}",
                             bag.Values[2] is int[] a && a.Length == 3 && a[0] + a[1] + a[2] == 6);

            var self = bag.Values[3] as SelfRef;
            failed += Expect(say, "Values[3] is SelfRef, Me is itself",
                             self != null && ReferenceEquals(self.Me, self));
            failed += Expect(say, "SelfRef fields",
                             self != null && self.Text == "hello" && self.Small == 7 && self.Middle == 0x1234
                             && self.Big == 0x1122334455667788);
            failed += Expect(say, "SelfRef.Any is IntBox 389", self != null && self.Any is IntBox b && b.Value == 389);
            failed += Expect(say, "Values[4] is Bag {x: y}, Values[1] is the root",
                             bag.Values[4] is Bag inner && inner.Keys[0] == "x" && (string)inner.Values[0] == "y"
                             && ReferenceEquals(inner.Values[1], bag));
            failed += Expect(say, "Values[5] is null", bag.Values[5] == null);
            failed += Expect(say, "Values[6] is the same object as Values[3]", ReferenceEquals(bag.Values[6], bag.Values[3]));

            var h = bag.Values[7] as Holder;
            failed += Expect(say, "Values[7] is Holder", h != null);
            if (h == null)
                return failed;
            failed += Expect(say, "Base field holds Derived, virtual Rank() == 2, Tag == 17",
                             h.Base is Derived && h.Base.Rank() == 2 && h.Base.Tag == 17);
            failed += Expect(say, "interface field holds Shaped, Sides() == 5", h.Ranked is Shaped && h.Ranked.Sides() == 5);
            failed += Expect(say, "interface dispatch with four implementations: Label() == 20 and 50",
                             h.Base is ILabelled bl && bl.Label() == 20 && h.Ranked is ILabelled rl && rl.Label() == 50);
            failed += Expect(say, "TextBox \"boxed\"", h.Text != null && h.Text.Value == "boxed");
            failed += Expect(say, "Pair[] {a,1} {null,2} {c,3}",
                             h.Pairs is Pair[] ps && ps.Length == 3 && ps[0].Name == "a" && ps[0].X == 1
                             && ps[1].Name == null && ps[1].X == 2 && ps[2].Name == "c" && ps[2].X == 3);
            failed += Expect(say, "boxed enum is Tint.Deep", h.Enum is Tint t && t == Tint.Deep);
            failed += Expect(say, "boxed struct is Pair {ref, 99}", h.RefStruct is Pair rp && rp.Name == "ref" && rp.X == 99);

            bool numbersOk = h.Numbers != null && h.Numbers.Length == numbers;
            for (int n = 0; numbersOk && n < numbers; n++)
                numbersOk = h.Numbers[n] == n * 3;
            failed += Expect(say, "int[] of the requested length, i*3", numbersOk);
            return failed;
        }

        private static int Expect(Action<string> say, string what, bool ok)
        {
            if (say != null)
                say((ok ? "ok   " : "FAIL ") + what);
            return ok ? 0 : 1;
        }
    }
}
