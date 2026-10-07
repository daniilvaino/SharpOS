using System;
using System.Diagnostics;
using Microsoft.CSharp.RuntimeBinder;
using SharpOS.AppSdk;
using SharpOS.Std.Dynamic;
using SharpOS.Std.Pipes;
using SharpOS.Std.Pipes.Probe;

namespace AotTests
{
    // `dynamic` (step 193): the compiler lowers it onto std's CallSite/Binder
    // surface, the generator writes the thunks and member tables, the binder
    // runs here. The cases both machines run are in DynamicCases.cs; this file
    // has what only SharpOS has: views and Expando from a pipe, the call-site
    // cache, timings.
    internal static unsafe partial class AppEntry
    {
        internal sealed class DynGeneric
        {
            public string Id<T>(T x) => "Id<" + (typeof(T) == typeof(int) ? "Int32" : "?") + "> " + x.ToString();
        }

        internal sealed class DynamicLine
        {
            public string Text;
            public int Level;

            public override string ToString() => "line " + Text + "/" + Level.ToString();
        }

        // The task's own example, as written there.
        private static string DynamicProbe()
        {
            dynamic x = new Expando();
            x.Name = "диск";
            x.Free = 12;
            x.Free += 30;
            dynamic o = new DynamicLine { Text = "t", Level = 5 };
            o.Level++;
            int n = o.Level * 2;
            string s = o.Text;
            if (x.Free > 40) s += "!";
            return s + n.ToString() + o.ToString();
        }

        private static void CheckDynamic()
        {
            CheckStackArgumentRoot();

            string probe = null;
            try { probe = DynamicProbe(); }
            catch (Exception e) { probe = "threw " + e.Message; }
            AppHost.WriteString("[dyn] probe: " + probe + "\n");
            Check("dynamic: Expando, class member, +=, ++, *, conversions", probe == "t!12line t/6");

            // The shared cases (DynamicCases.cs), the same source tools/DynamicReference runs on desktop .NET.
            int pass = DynamicCases.Run(line => AppHost.WriteString(line + "\n"), out int total);
            Check("dynamic: " + pass.ToString() + "/" + total.ToString() + " cases as on desktop .NET", pass == total);

            // The kernel's probe pipes have fixed names: one battery at a time.
            if (!s_concurrent)
            {
                DynamicTarget();
                DynamicViews();
            }
            DynamicCache();
            DynamicInference();
            DynamicExpando();
            if (!s_gcStress && !s_kernelStressed && !s_concurrent) BenchDynamic();
        }

        private static int RunDynamicOnly()
        {
            AppHost.WriteString("==== dynamic ====\n");
            CheckDynamic();
            AppHost.WriteString("==== ");
            AppHost.WriteUInt(s_pass);
            AppHost.WriteString("/");
            AppHost.WriteUInt(s_total);
            AppHost.WriteString(" passed ====\n");
            return (int)s_pass;
        }

        // A reference held only by a stack-passed argument (the fifth and later
        // on Win64) survives a collection. The walk read such slots from the
        // wrong place until step 193 — `caller-SP` relative, taken as current
        // SP plus the outgoing area — and DynamicBinder's eighth parameter,
        // the call site's argument list, was swept under GC stress.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int HeldOnlyAsEighth(int a, int b, string c, int d, object e, object f, object g, object h)
        {
            System.GC.Collect();
            for (int i = 0; i < 64; i++) { object junk = new int[4]; }
            return h is int[] numbers && numbers.Length == 3 && numbers[2] == 33 ? numbers[0] + c.Length : -1;
        }

        // The shape of `dynamic`'s generated registration — eight arguments
        // (four on the stack), fresh arrays, type objects, cached lambdas —
        // with collections inside the callee. Under GC stress it lost roots
        // three ways until step 193: stack-passed arguments read at the wrong
        // address, the frame-relative slots of a slim GcInfo header never
        // marked (RegChunk: 24 calls, an RBP frame), the mark stack
        // overflowing on the tables.
        private sealed class RegRecord
        {
            public Type Owner, ParamsElement, Returns;
            public string Name;
            public Type[] Parameters;
            public Func<object, object[], object> Invoke;
            public int Required;
        }

        private static System.Collections.Generic.List<RegRecord> s_regs;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Reg(Type owner, string name, bool isStatic, Type[] parameters, Type paramsElement,
                                Type returns, int required, Func<object, object[], object> invoke)
        {
            if ((s_regs.Count % 7) == 3) System.GC.Collect();
            s_regs.Add(new RegRecord
            {
                Owner = owner, Name = name, Parameters = parameters, ParamsElement = paramsElement,
                Returns = returns, Required = required, Invoke = invoke,
            });
        }

        private static string CheckRegistrationShape()
        {
            s_regs = new System.Collections.Generic.List<RegRecord>();
            for (int round = 0; round < 20; round++)
            {
                Reg(typeof(int), "A", false, new Type[] { typeof(string) }, null, typeof(string), 1, static (o, a) => (object)((int)o).ToString());
                Reg(typeof(long), "B", true, new Type[] { typeof(int), typeof(int) }, typeof(int), typeof(long), 2, static (o, a) => (object)((int)a[0] + (int)a[1]));
                Reg(typeof(string), "C", false, new Type[] { }, null, typeof(int), 0, static (o, a) => (object)((string)o).Length);
                Reg(typeof(DynDog), "D", false, new Type[] { typeof(object) }, typeof(object), null, 0, static (o, a) => { ((DynDog)o).Legs = 1; return null; });
            }
            System.GC.Collect();
            for (int i = 0; i < 200; i++) { object junk = new object[5]; }
            for (int i = 0; i < s_regs.Count; i++)
            {
                RegRecord r = s_regs[i];
                switch (i % 4)
                {
                    case 0: if (r.Owner != typeof(int) || r.Name != "A" || r.Parameters.Length != 1 || r.Parameters[0] != typeof(string) || r.Returns != typeof(string) || (string)r.Invoke(5, null) != "5") return "A at " + i.ToString(); break;
                    case 1: if (r.Owner != typeof(long) || r.Parameters[1] != typeof(int) || r.ParamsElement != typeof(int) || r.Returns != typeof(long) || (int)r.Invoke(null, new object[] { 2, 3 }) != 5) return "B at " + i.ToString(); break;
                    case 2: if (r.Owner != typeof(string) || r.Parameters.Length != 0 || r.Returns != typeof(int) || (int)r.Invoke("abc", null) != 3) return "C at " + i.ToString(); break;
                    case 3: if (r.Owner != typeof(DynDog) || r.ParamsElement != typeof(object) || r.Returns != null) return "D at " + i.ToString(); break;
                }
            }
            return null;
        }

        // The generated chunk's own shape: 24 registrations in one method.
        private static void RegChunk()
        {
            Reg(typeof(int), "N0", false, new Type[] { typeof(uint), typeof(object) }, typeof(object), typeof(int), 0, static (o, x) => (object)0);
            Reg(typeof(long), "N1", false, new Type[] { typeof(ulong), typeof(DynDog) }, typeof(DynDog), typeof(long), 1, static (o, x) => (object)1);
            Reg(typeof(string), "N2", false, new Type[] { typeof(char), typeof(DynAnimal) }, typeof(DynAnimal), typeof(string), 2, static (o, x) => (object)2);
            Reg(typeof(byte), "N3", false, new Type[] { typeof(bool), typeof(DynOver) }, typeof(DynOver), typeof(byte), 0, static (o, x) => (object)3);
            Reg(typeof(short), "N4", false, new Type[] { typeof(double), typeof(DynPair) }, typeof(DynPair), typeof(short), 1, static (o, x) => (object)4);
            Reg(typeof(uint), "N5", false, new Type[] { typeof(float), typeof(DynGrid) }, typeof(DynGrid), typeof(uint), 2, static (o, x) => (object)5);
            Reg(typeof(ulong), "N6", false, new Type[] { typeof(object), typeof(DynSquare) }, typeof(DynSquare), typeof(ulong), 0, static (o, x) => (object)6);
            Reg(typeof(char), "N7", false, new Type[] { typeof(DynDog), typeof(DynMoney) }, typeof(DynMoney), typeof(char), 1, static (o, x) => (object)7);
            Reg(typeof(bool), "N8", false, new Type[] { typeof(DynAnimal), typeof(DynPoint) }, typeof(DynPoint), typeof(bool), 2, static (o, x) => (object)8);
            Reg(typeof(double), "N9", false, new Type[] { typeof(DynOver), typeof(DynamicLine) }, typeof(DynamicLine), typeof(double), 0, static (o, x) => (object)9);
            Reg(typeof(float), "N10", false, new Type[] { typeof(DynPair), typeof(Type) }, typeof(Type), typeof(float), 1, static (o, x) => (object)10);
            Reg(typeof(object), "N11", false, new Type[] { typeof(DynGrid), typeof(sbyte) }, typeof(sbyte), typeof(object), 2, static (o, x) => (object)11);
            Reg(typeof(DynDog), "N12", false, new Type[] { typeof(DynSquare), typeof(ushort) }, typeof(ushort), typeof(DynDog), 0, static (o, x) => (object)12);
            Reg(typeof(DynAnimal), "N13", false, new Type[] { typeof(DynMoney), typeof(int) }, typeof(int), typeof(DynAnimal), 1, static (o, x) => (object)13);
            Reg(typeof(DynOver), "N14", false, new Type[] { typeof(DynPoint), typeof(long) }, typeof(long), typeof(DynOver), 2, static (o, x) => (object)14);
            Reg(typeof(DynPair), "N15", false, new Type[] { typeof(DynamicLine), typeof(string) }, typeof(string), typeof(DynPair), 0, static (o, x) => (object)15);
            Reg(typeof(DynGrid), "N16", false, new Type[] { typeof(Type), typeof(byte) }, typeof(byte), typeof(DynGrid), 1, static (o, x) => (object)16);
            Reg(typeof(DynSquare), "N17", false, new Type[] { typeof(sbyte), typeof(short) }, typeof(short), typeof(DynSquare), 2, static (o, x) => (object)17);
            Reg(typeof(DynMoney), "N18", false, new Type[] { typeof(ushort), typeof(uint) }, typeof(uint), typeof(DynMoney), 0, static (o, x) => (object)18);
            Reg(typeof(DynPoint), "N19", false, new Type[] { typeof(int), typeof(ulong) }, typeof(ulong), typeof(DynPoint), 1, static (o, x) => (object)19);
            Reg(typeof(DynamicLine), "N20", false, new Type[] { typeof(long), typeof(char) }, typeof(char), typeof(DynamicLine), 2, static (o, x) => (object)20);
            Reg(typeof(Type), "N21", false, new Type[] { typeof(string), typeof(bool) }, typeof(bool), typeof(Type), 0, static (o, x) => (object)21);
            Reg(typeof(sbyte), "N22", false, new Type[] { typeof(byte), typeof(double) }, typeof(double), typeof(sbyte), 1, static (o, x) => (object)22);
            Reg(typeof(ushort), "N23", false, new Type[] { typeof(short), typeof(float) }, typeof(float), typeof(ushort), 2, static (o, x) => (object)23);
        }

        private static string CheckBigChunk()
        {
            s_regs = new System.Collections.Generic.List<RegRecord>();
            for (int round = 0; round < 10; round++) RegChunk();
            System.GC.Collect();
            for (int i = 0; i < 200; i++) { object junk = new object[5]; }
            for (int i = 0; i < s_regs.Count; i++)
            {
                RegRecord r = s_regs[i];
                if (r.Parameters == null || r.Parameters.Length != 2 || r.Parameters[0] == null || r.Parameters[1] == null
                    || r.Parameters[1] != r.ParamsElement || r.Owner != r.Returns || (int)r.Invoke(null, null) != i % 24)
                    return "record " + i.ToString();
            }
            return null;
        }

        private static void CheckStackArgumentRoot()
        {

            int ok = 0;
            for (int i = 0; i < 3; i++)
                if (HeldOnlyAsEighth(1, 2, "x", 4, null, null, null, new[] { 11, 22, 33 }) == 12) ok++;
            Check("GC: a reference held only by a stack-passed argument survives a collection", ok == 3);
            string shape = CheckRegistrationShape();
            Report("registrations with collections inside", shape);
            Check("GC: eight-argument registrations with collections inside keep every argument", shape == null);
            string chunk = CheckBigChunk();
            Report("a method of 24 registrations", chunk);
            Check("GC: a method of 24 registrations (an RBP frame, a slim GcInfo header) keeps its roots", chunk == null);
            Check("GC: a frame of 700 live references (past the walk's own stack buffer) keeps them all", BigFrameSurvives() == 0);
        }

        // The task's example as written: the kernel's log lines, a class the app lacks.
        private static void DynamicTarget()
        {
            RawPipeReader reader = Pipe.Read("myapp.log");
            int sent = Probe(23, 0, null);
            var copies = new System.Collections.Generic.List<Expando>();
            foreach (dynamic v in reader)
            {
                if (v.Level >= 3) Console.WriteLine(v.Text);
                Console.WriteLine(v.Origin.App + ": " + v.Tags[0]);
                v.Seen = true;
                v.Origin.Thread = 7;
                Expando copy = v;
                copies.Add(copy);
            }
            bool ok = sent == 0 && copies.Count == 2;
            for (int i = 0; ok && i < copies.Count; i++)
                ok = copies[i]["Seen"] is bool seen && seen
                     && copies[i]["Origin"] is Expando origin && origin["Thread"] is int thread && thread == 7;
            ok = ok && (string)((Expando)copies[0]["Origin"])["App"] == "storage" && ((string[])copies[1]["Tags"])[0] == "net";
            Check("dynamic: the task's example over the kernel's log lines (Console, Origin.Thread = 7, Expando copy)", ok);
        }

        // The kernel's report, read without its class: values come out as values,
        // objects as views bound to the step, writes go into the block.
        private static void DynamicViews()
        {
            RawPipeReader reports = Pipe.Read("probe.dynamic.report");
            RawPipeReader bags = Pipe.Read("probe.dynamic.expando");
            int sent = Probe(21, 0, null);

            int count = 0, sum = 0, lookupsSecond = 0, lookupsThird = 0;
            string bad = null, missing = null, refused = null;
            Expando copy = null;
            dynamic kept = null;
            foreach (dynamic v in reports)
            {
                if (count == 1) lookupsSecond = DynamicRuntime.Lookups;
                if (count == 2) lookupsThird = DynamicRuntime.Lookups;
                if (v.I != ViewProbe.I) bad ??= "I";
                if (v.B != ViewProbe.B || v.SB != ViewProbe.SB || v.S != ViewProbe.S || v.US != ViewProbe.US) bad ??= "small integers";
                if (v.UI != ViewProbe.UI || v.L != ViewProbe.L || v.UL != ViewProbe.UL) bad ??= "large integers";
                if (v.F != 1.5f || v.D != ViewProbe.D || v.D >= 0) bad ??= "floats";
                if (v.C != 'ж' || !v.Flag) bad ??= "char, bool";
                if (v.Text != "отчёт" || v.Text.Length != 5) bad ??= "Text";
                if (v.Mood != 2) bad ??= "Mood (an enum as its number)";
                if (v.Where.Name != "hall" || v.Where.Floor != 3) bad ??= "Where";
                if (v.Nowhere != null) bad ??= "Nowhere";
                if (v.Numbers.Length != 3 || v.Numbers[2] != 3 || v.Words[1] != "b") bad ??= "arrays";
                if (v.Places[1].Floor != 5 || v.Next.Next.I != ViewProbe.I) bad ??= "nested, the cycle";
                int i = v.I;
                long l = v.I;
                string text = v.Text;
                object raw = v.Text;
                if (!(raw is string)) bad ??= "a string field comes out as a string";
                if (i != ViewProbe.I || l != ViewProbe.I || text != "отчёт") bad ??= "conversions";
                foreach (var x in v.Numbers) sum += x;
                if (v.Flag) sum += 100;

                // Values written in place, read back through the same block.
                v.Flag = false;
                v.Where.Floor = 7;
                v.Numbers[0] = 100;
                v.I += 1;
                if (v.Flag || v.Where.Floor != 7 || v.Numbers[0] != 100 || v.I != ViewProbe.I + 1) bad ??= "writes";

                if (count == 0)
                {
                    try { object nope = v.Nope; missing = "no exception"; }
                    catch (RuntimeBinderException e) { missing = e.Message; }
                    try { v.Text = "другой"; refused = "no exception"; }
                    catch (InvalidOperationException) { refused = null; }
                    copy = v;               // a deep copy, after the writes
                    kept = v;
                }
                count++;
            }
            string afterStep;
            try { object late = kept.I; afterStep = "read after the step"; }
            catch (ObjectDisposedException) { afterStep = null; }
            catch (Exception e) { afterStep = "threw " + e.Message; }

            Report("dynamic over the kernel's report", bad);
            Check("dynamic: a view from the kernel: every kind of field, nested, arrays, the cycle, conversions",
                  sent == 0 && count == 3 && bad == null && sum == 3 * (1 + 2 + 3 + 100));
            Check("dynamic: values written through a view into the block (v.Where.Floor = 7, v.I += 1)", bad == null && count == 3);
            Check("dynamic: no such field names the writer's type",
                  missing == "'SharpOS.Probe.Kernel.KernelReport' does not contain a definition for 'Nope'");
            Check("dynamic: a string into a region is refused", refused == null);
            bool copyOk = copy != null && copy["Where"] is Expando where && where["Floor"] is int floor && floor == 7
                          && copy["Text"] is string s && s == "отчёт" && ReferenceEquals(copy["Next"], copy);
            Check("dynamic: Expando e = v — a deep copy, with the writes", copyOk);
            Report("a view kept past its step", afterStep);
            Check("dynamic: a view kept past its step throws ObjectDisposedException", afterStep == null);
            Check("dynamic: same shape, next message: no name looked up again",
                  lookupsSecond > 0 && lookupsThird == lookupsSecond);

            string fromBag = "nothing received";
            foreach (dynamic v in bags)
                fromBag = v.I == ViewProbe.I && v.Where.Floor == 3 && v.Places[1].Name == "b" && v.Text == ViewProbe.Text ? null : "values";
            Report("dynamic over the kernel's Expando", fromBag);
            Check("dynamic: an Expando from the kernel, read the same way", fromBag == null);
        }

        // A generic method needs type inference, which the binder does not do;
        // with the type argument written it binds.
        private static void DynamicInference()
        {
            dynamic g = new DynGeneric();
            string inferred;
            try { inferred = g.Id(5); }
            catch (RuntimeBinderException e) { inferred = e.Message; }
            string named = g.Id<int>(5);
            Check("dynamic: a generic method without type arguments: RuntimeBinderException",
                  inferred == "The type arguments for method 'AotTests.AppEntry.DynGeneric.Id' cannot be inferred from the usage. Try specifying the type arguments explicitly.");
            Check("dynamic: with the type argument written it binds", named == "Id<Int32> 5");
        }

        // One call site, one type: the name is looked up once. Two types in turn: twice.
        private static void DynamicCache()
        {
            dynamic dog = new DynDog(), animal = new DynAnimal();
            int before = DynamicRuntime.Lookups, sum = 0;
            for (int i = 0; i < 50; i++) sum += dog.Legs;
            int one = DynamicRuntime.Lookups - before;

            before = DynamicRuntime.Lookups;
            int mixed = 0;
            for (int i = 0; i < 50; i++)
            {
                dynamic x = (i & 1) == 0 ? dog : animal;
                mixed += x.V;
            }
            int two = DynamicRuntime.Lookups - before;
            AppHost.WriteString("[dyn] cache: one type " + one.ToString() + " lookup(s), two types " + two.ToString() + "\n");
            Check("dynamic: one type at a call site — the name looked up once", one == 1 && sum == 200);
            Check("dynamic: two types in turn — once each, the right member for each", two == 2 && mixed == 25 * 2 + 25 * 1);
        }

        private static void DynamicExpando()
        {
            dynamic x = new Expando();
            x.A = 1;
            x["B"] = "b";
            int before = DynamicRuntime.Lookups;
            int sum = 0;
            for (int i = 0; i < 3; i++) sum += x.A;
            x.A = "changed";                     // an entry changes type: not cached
            bool changed = x.A == "changed";
            // Check takes a bool: a dynamic argument would make the call itself dynamic.
            bool ok = sum == 3 && (bool)(x.B == "b") && changed && ((Expando)x).Count == 2 && ((Expando)x).ContainsKey("A");
            Check("dynamic: Expando — entries and the indexer, an entry that changes type; own members through a cast", ok);
            string own;
            try { object c = x.Count; own = "read Count"; }
            catch (RuntimeBinderException e) { own = e.Message; }
            Check("dynamic: Expando — a name with no entry is RuntimeBinderException, even an own member's",
                  own == "'SharpOS.Std.Pipes.Expando' does not contain a definition for 'Count'");
            Check("dynamic: Expando is asked every time (nothing cached)", DynamicRuntime.Lookups - before >= 4);
        }

        private static void BenchDynamic()
        {
            const int N = 2000;
            dynamic dog = new DynDog();
            dynamic bag = new Expando();
            bag.Legs = 4;
            var plain = new DynDog();
            long ns(Stopwatch w) => w.ElapsedTicks * 1_000_000_000L / Stopwatch.Frequency / N;

            // Twice, the second counted: the first binds the sites and grows the heap.
            object sink = null;
            long direct = 0, cls = 0, expando = 0, call = 0;
            for (int round = 0; round < 2; round++)
            {
                var w = Stopwatch.StartNew();
                for (int i = 0; i < N; i++) sink = plain.Legs;
                direct = ns(w);
                w.Restart();
                for (int i = 0; i < N; i++) sink = dog.Legs;
                cls = ns(w);
                w.Restart();
                for (int i = 0; i < N; i++) sink = bag.Legs;
                expando = ns(w);
                w.Restart();
                for (int i = 0; i < N; i++) sink = dog.Speak();
                call = ns(w);
            }
            var watch = new Stopwatch();

            long view = -1;
            RawPipeReader reports = Pipe.Read("probe.dynamic.bench");
            if (Probe(22, 0, null) == 0)
                foreach (dynamic v in reports)
                {
                    for (int round = 0; round < 2; round++)
                    {
                        watch.Restart();
                        for (int i = 0; i < N; i++) sink = v.I;
                        view = ns(watch);
                    }
                }
            AppHost.WriteString("[bench] dynamic: d.Field — view " + view.ToString() + " ns, Expando " + expando.ToString()
                                + " ns, class " + cls.ToString() + " ns (static " + direct.ToString() + " ns); d.Method() "
                                + call.ToString() + " ns" + (sink == null ? " SINK NULL" : "") + "\n");
        }
    }
}
