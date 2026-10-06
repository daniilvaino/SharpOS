// `dynamic` cases (step 193), one source for two machines: the battery runs
// them on SharpOS, tools/DynamicReference runs them on desktop .NET 8 with the real
// Microsoft.CSharp binder (DESKTOP defined). Each case states the result C#
// gives; the desktop run is what proves the statement, SharpOS must agree.
// A value prints with its type ("int 7"), so a wrong promotion shows.

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.CSharp.RuntimeBinder;
#if DESKTOP
using Bag = System.Dynamic.ExpandoObject;
#else
using Bag = SharpOS.Std.Pipes.Expando;
#endif

namespace AotTests
{
    internal class DynAnimal
    {
        public string Name = "rex";
        public int V = 1;
        public virtual string Speak() => "...";
    }

    internal sealed class DynDog : DynAnimal
    {
        public int Legs = 4;
        public new int V = 2;
        public override string Speak() => "woof";
    }

    internal struct DynPoint
    {
        public int X;
        public int Y { get; set; }
        public int Sum() => X + Y;
    }

    internal sealed class DynBox<T>
    {
        public T Value;
        public T Get() => Value;
    }

    internal sealed class DynOver
    {
        public string M(int x) => "int";
        public string M(long x) => "long";
        public string M(string x) => "string";
        public string M(object x) => "object";
        public string P(params int[] xs) => "params " + xs.Length.ToString();
        public string Opt(int a, int b = 40) => "opt " + (a + b).ToString();
        public static string S(int x) => "S" + x.ToString();
        public Func<int, int> Twice = x => x * 2;
        public void Nothing() { }
    }

    internal sealed class DynAmbiguous
    {
        public string M(int a, long b) => "il";
        public string M(long a, int b) => "li";
    }

    internal sealed class DynPair
    {
        public readonly string Made;
        public DynPair(int x) { Made = "int " + x.ToString(); }
        public DynPair(string s) { Made = "string " + s; }
    }

    internal sealed class DynGrid
    {
        private readonly int[] _cells = new int[100];
        public int this[int x, int y]
        {
            get => _cells[x * 10 + y];
            set => _cells[x * 10 + y] = value;
        }
    }

    internal interface IDynShape
    {
        int Corners { get; }
    }

    internal sealed class DynSquare : IDynShape
    {
        public int Corners => 4;
    }

    internal struct DynMoney
    {
        public int Cents;
        public DynMoney(int cents) { Cents = cents; }
        public static DynMoney operator +(DynMoney a, DynMoney b) => new DynMoney(a.Cents + b.Cents);
        public static bool operator ==(DynMoney a, DynMoney b) => a.Cents == b.Cents;
        public static bool operator !=(DynMoney a, DynMoney b) => a.Cents != b.Cents;
        public static implicit operator DynMoney(int cents) => new DynMoney(cents);
        public override bool Equals(object o) => o is DynMoney m && m.Cents == Cents;
        public override int GetHashCode() => Cents;
        public override string ToString() => Cents.ToString() + "c";
    }

    internal enum DynColor { Red = 1, Green = 2, Blue = 4 }

    internal static class DynamicCases
    {
        private static int s_pass, s_total;
        private static Action<string> s_out;

        /// <summary>Runs every case; returns (passed, total).</summary>
        public static int Run(Action<string> output, out int total)
        {
            s_out = output;
            s_pass = s_total = 0;
            Members();
            Calls();
            Indexers();
            Operators();
            Conversions();
            Errors();
            Bags();
            total = s_total;
            return s_pass;
        }

        private static void Case(string name, Func<object> f, string expected)
        {
            string got;
            try { got = Show(f()); }
            catch (RuntimeBinderException e) { got = "RBE: " + e.Message; }
            catch (OverflowException) { got = "Overflow"; }
            catch (InvalidCastException) { got = "InvalidCast"; }
            catch (IndexOutOfRangeException) { got = "IndexOutOfRange"; }
            catch (Exception e) { got = "Exception: " + e.Message; }
            s_total++;
            bool ok = got == expected;
            if (ok) s_pass++;
            s_out((ok ? "  ok   dyn " : "  FAIL dyn ") + name + " = " + got + (ok ? "" : "   (expected " + expected + ")"));
        }

        private static string Show(object v)
        {
            switch (v)
            {
                case null: return "null";
                case int i: return "int " + i.ToString();
                case long l: return "long " + l.ToString();
                case uint ui: return "uint " + ui.ToString();
                case ulong ul: return "ulong " + ul.ToString();
                case short s: return "short " + s.ToString();
                case ushort us: return "ushort " + us.ToString();
                case byte b: return "byte " + b.ToString();
                case sbyte sb: return "sbyte " + sb.ToString();
                case char c: return "char " + c.ToString();
                case bool bo: return bo ? "bool true" : "bool false";
                case double d: return "double " + d.ToString();
                case float f: return "float " + f.ToString();
                case string str: return "string \"" + str + "\"";
                case DynColor color: return "DynColor " + ((int)color).ToString();
                case DynMoney m: return "DynMoney " + m.ToString();
            }
            return "object " + v.ToString();
        }

        private static void Members()
        {
            dynamic dog = new DynDog();
            Case("class field", () => dog.Legs, "int 4");
            Case("base class field", () => dog.Name, "string \"rex\"");
            Case("derived hides base", () => dog.V, "int 2");
            Case("virtual call", () => dog.Speak(), "string \"woof\"");
            Case("set field", () => { dog.Legs = 3; return dog.Legs; }, "int 3");
            Case("set base field", () => { dog.Name = "fido"; return dog.Name; }, "string \"fido\"");

            dynamic p = new DynPoint { X = 1, Y = 2 };
            Case("struct field", () => p.X, "int 1");
            Case("struct property", () => p.Y, "int 2");
            Case("struct method", () => p.Sum(), "int 3");
            Case("struct set in the box", () => { p.X = 10; return p.Sum(); }, "int 12");

            dynamic anon = new { Title = "anon", Count = 3 };
            Case("anonymous type", () => anon.Title + "/" + anon.Count, "string \"anon/3\"");

            dynamic box = new DynBox<int> { Value = 7 };
            Case("closed generic field", () => box.Value, "int 7");
            Case("closed generic method", () => box.Get() + 1, "int 8");
            dynamic sbox = new DynBox<string> { Value = "s" };
            Case("closed generic over string", () => sbox.Get(), "string \"s\"");

            dynamic arr = new[] { 1, 2, 3 };
            Case("array length", () => arr.Length, "int 3");
            dynamic text = "hello";
            Case("string length", () => text.Length, "int 5");
            Case("ToString on a number", () => ((dynamic)42).ToString(), "string \"42\"");
        }

        private static void Calls()
        {
            dynamic o = new DynOver();
            dynamic i = 1, l = 1L, s = "a", d = 1.5, sh = (short)1, by = (byte)1;
            Case("overload int", () => o.M(i), "string \"int\"");
            Case("overload long", () => o.M(l), "string \"long\"");
            Case("overload string", () => o.M(s), "string \"string\"");
            Case("overload object (double)", () => o.M(d), "string \"object\"");
            Case("overload short -> int", () => o.M(sh), "string \"int\"");
            Case("overload byte -> int", () => o.M(by), "string \"int\"");
            Case("overload literal on dynamic receiver", () => o.M(5L), "string \"long\"");
            Case("params expanded", () => o.P(1, 2, 3), "string \"params 3\"");
            Case("params empty", () => o.P(), "string \"params 0\"");
            Case("params as array", () => o.P(new[] { 1, 2 }), "string \"params 2\"");
            Case("optional argument left out", () => o.Opt(2), "string \"opt 42\"");
            Case("static method, dynamic argument", () => DynOver.S(i), "string \"S1\"");
            Case("Math.Max with dynamic", () => Math.Max(i, 3), "int 3");
            Case("Math.Max long", () => Math.Max(l, 3), "long 3");
            Case("new with dynamic argument (int)", () => new DynPair(i).Made, "string \"int 1\"");
            Case("new with dynamic argument (string)", () => new DynPair(s).Made, "string \"string a\"");
            dynamic f = new Func<int, int>(x => x * 3);
            Case("delegate call", () => f(14), "int 42");
            Case("delegate in a field", () => o.Twice(21), "int 42");
            var list = new List<int>();
            Case("statically typed receiver, dynamic argument", () => { list.Add(i); list.Add(o.Twice(2)); return list.Count + list[1]; }, "int 6");
            Case("void method result discarded", () => { o.Nothing(); return "done"; }, "string \"done\"");
        }

        private static void Indexers()
        {
            dynamic arr = new[] { 1, 2, 3 };
            Case("array element", () => arr[1], "int 2");
            Case("array element set", () => { arr[1] = 9; return arr[1]; }, "int 9");
            Case("array index long", () => arr[2L], "int 3");
            dynamic strs = new[] { "a", "b" };
            Case("string array element", () => strs[1], "string \"b\"");
            dynamic text = "abc";
            Case("string character", () => text[1], "char b");
            dynamic list = new List<int> { 1, 2 };
            Case("List<int> element", () => list[0] + list[1], "int 3");
            Case("List<int> set", () => { list[0] = 5; return list[0]; }, "int 5");
            dynamic dict = new Dictionary<string, int>();
            Case("Dictionary<string,int> set and get", () => { dict["a"] = 1; dict["b"] = 2; return dict["a"] + dict["b"]; }, "int 3");
            dynamic grid = new DynGrid();
            Case("own indexer, two arguments", () => { grid[1, 2] = 12; return grid[1, 2]; }, "int 12");
            Case("index out of range", () => arr[5], "IndexOutOfRange");
        }

        private static void Operators()
        {
            dynamic five = 5, two = 2L, half = 0.5, b1 = (byte)1, ch = 'a', s = "x", t = true, f = false, nul = null;
            Case("int + long", () => five + two, "long 7");
            Case("int / int", () => five / 2, "int 2");
            Case("int % int", () => five % 3, "int 2");
            Case("int * double", () => five * half, "double 2.5");
            Case("byte + byte", () => b1 + b1, "int 2");
            Case("char + int", () => ch + 1, "int 98");
            Case("int - long", () => five - two, "long 3");
            Case("double % double", () => 7.5 % (half + 2), "double 0");
            Case("compare >", () => five > two, "bool true");
            Case("compare == literal", () => five == 5, "bool true");
            Case("compare != double", () => five != 5.0, "bool false");
            Case("compare <=", () => half <= 0.5, "bool true");
            Case("string + int", () => s + 1, "string \"x1\"");
            Case("int + string", () => 1 + s, "string \"1x\"");
            Case("string + null", () => s + nul, "string \"x\"");
            Case("null == null", () => nul == null, "bool true");
            Case("int == null", () => five == null, "bool false");
            Case("string == string", () => s == "x", "bool true");
            Case("&&", () => t && f, "bool false");
            Case("||", () => f || t, "bool true");
            Case("&& short-circuits", () => f && Fail(), "bool false");
            Case("unary minus", () => -five, "int -5");
            Case("unary not", () => !t, "bool false");
            Case("unary ~", () => ~five, "int -6");
            Case("unary - on uint", () => -(dynamic)3u, "long -3");
            Case("++ on int", () => { dynamic c = 5; c++; return c; }, "int 6");
            Case("++ on byte wraps", () => { dynamic c = (byte)255; c++; return c; }, "byte 0");
            Case("+= on int", () => { dynamic c = 5; c += 10; return c; }, "int 15");
            Case("+= on a member", () => { dynamic dog = new DynDog(); dog.Legs += 2; return dog.Legs; }, "int 6");
            Case("++ on a member", () => { dynamic dog = new DynDog(); dog.Legs++; return dog.Legs; }, "int 5");
            Case("checked overflow", () => checked(int.MaxValue + five), "Overflow");
            Case("unchecked wraps", () => unchecked(int.MaxValue + five), "int -2147483644");
            Case("shift", () => five << 2, "int 20");
            Case("bitwise and", () => five & 4, "int 4");
            Case("enum or", () => (dynamic)DynColor.Red | DynColor.Blue, "DynColor 5");
            Case("enum + int", () => (dynamic)DynColor.Red + 1, "DynColor 2");
            dynamic m1 = new DynMoney(150), m2 = new DynMoney(50);
            Case("user-defined +", () => m1 + m2, "DynMoney 200c");
            Case("user-defined ==", () => m1 == m2, "bool false");
            Case("ulong + int is ambiguous", () => (dynamic)1UL + five, "RBE: Operator '+' cannot be applied to operands of type 'ulong' and 'int'");
        }

        private static bool Fail() => throw new InvalidOperationException("not short-circuited");

        private static void Conversions()
        {
            dynamic five = 5, big = 300, lng = 5L, s = "str", dog = new DynDog(), sq = new DynSquare(), t = true;
            Case("int x = d", () => { int x = five; return x; }, "int 5");
            Case("long x = d (int)", () => { long x = five; return x; }, "long 5");
            Case("double x = d (int)", () => { double x = five; return x; }, "double 5");
            Case("int x = d (long) fails", () => { int x = lng; return x; }, "RBE: Cannot implicitly convert type 'long' to 'int'. An explicit conversion exists (are you missing a cast?)");
            Case("string x = d", () => { string x = s; return x; }, "string \"str\"");
            Case("string x = d (int) fails", () => { string x = five; return x; }, "RBE: Cannot implicitly convert type 'int' to 'string'");
            Case("(byte)d wraps", () => (byte)big, "byte 44");
            Case("checked (byte)d", () => checked((byte)big), "Overflow");
            Case("(int)d of long", () => (int)lng, "int 5");
            Case("(DynColor)d", () => (DynColor)(dynamic)4, "DynColor 4");
            Case("to an interface", () => { IDynShape x = sq; return x.Corners; }, "int 4");
            Case("to a base class", () => { DynAnimal a = dog; return a.Speak(); }, "string \"woof\"");
            Case("to IComparable (boxing)", () => { IComparable c = five; return c != null; }, "bool true");
            Case("user-defined implicit", () => { DynMoney m = five; return m; }, "DynMoney 5c");
            Case("foreach over a dynamic array", () => { int sum = 0; foreach (var x in (dynamic)new[] { 1, 2, 3 }) sum += x; return sum; }, "int 6");
            Case("foreach over a dynamic List<int>", () => { int sum = 0; foreach (int x in (dynamic)new List<int> { 4, 5 }) sum += x; return sum; }, "int 9");
            Case("if (d)", () => { if (t) return "yes"; return "no"; }, "string \"yes\"");
            Case("if (d) with an int fails", () => { if (five) return "yes"; return "no"; }, "RBE: Cannot implicitly convert type 'int' to 'bool'");
            Case("ternary on dynamic", () => t ? 1 : 2, "int 1");
            Case("nullable int from d", () => { int? n = five; return n.HasValue ? n.Value : -1; }, "int 5");
            Case("nullable int from null", () => { int? n = (dynamic)null; return n.HasValue ? 1 : 0; }, "int 0");
        }

        private static void Errors()
        {
            dynamic o = new DynOver(), amb = new DynAmbiguous(), nul = null, one = 1;
            Case("no such member", () => o.Nope, "RBE: 'AotTests.DynOver' does not contain a definition for 'Nope'");
            Case("no such method", () => o.Nope(), "RBE: 'AotTests.DynOver' does not contain a definition for 'Nope'");
            Case("ambiguous call", () => amb.M(one, one), "RBE: The call is ambiguous between the following methods or properties: 'AotTests.DynAmbiguous.M(int, long)' and 'AotTests.DynAmbiguous.M(long, int)'");
            Case("null receiver", () => nul.X, "RBE: Cannot perform runtime binding on a null reference");
            Case("wrong argument count", () => o.M(1, 2), "RBE: No overload for method 'M' takes 2 arguments");
            Case("no conversion to class", () => { DynDog x = o; return x; }, "RBE: Cannot implicitly convert type 'AotTests.DynOver' to 'AotTests.DynDog'");
            Case("operator on wrong types", () => o + 1, "RBE: Operator '+' cannot be applied to operands of type 'AotTests.DynOver' and 'int'");
            Case("void result used", () => { object r = o.Nothing(); return r; }, "RBE: Cannot implicitly convert type 'void' to 'object'");
        }

        private static void Bags()
        {
            dynamic x = new Bag();
            x.Name = "диск";
            x.Free = 12;
            x.Free += 30;
            Case("bag members", () => x.Name + " " + x.Free, "string \"диск 42\"");
            Case("bag member type", () => x.Free, "int 42");
            Case("bag missing member", () => x.Nope, "RBE: '" + BagName + "' does not contain a definition for 'Nope'");
        }

#if DESKTOP
        private const string BagName = "System.Dynamic.ExpandoObject";
#else
        private const string BagName = "SharpOS.Std.Pipes.Expando";
#endif
    }
}
