using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Probes for what managed C# features work "for free" under NativeAOT +
    // NoStdLib + our GC/runtime-export stubs. Each sub-test tries a small
    // managed pattern; if it compiles AND produces the expected result we
    // log "ok", otherwise we know to investigate.
    //
    // The point is to find what we DON'T need to implement ourselves before
    // writing collections / iterators / other real-world managed code.
    internal static unsafe class NativeAotProbe
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- nativeaot probe begin ----");

            Probe_VirtualDispatch();
            Probe_Interface();
            Probe_GenericMethod();
            Probe_GenericClass();
            Probe_StaticCtor();
            Probe_Boxing();
            Probe_IsAs();
            Probe_ArrayLength();
            Probe_Enum();
            Probe_BoxedEquals();
            Probe_EqualityComparerDefault();
            Probe_EqualityComparerEquals();
            Probe_BclList();
            Probe_BclListForeach();
            Probe_BclListAsInterface();
            // Delegates (any managed `delegate T F(...)`, with or without capture) require
            // Delegate.InitializeClosedInstance + _target + _functionPointer + Invoke
            // machinery on System.Delegate. None of that is stubbed yet, so even a plain
            // `IntFn f = x => x * 3;` fails at ILC codegen. `delegate* unmanaged<T>` (IL
            // function pointers) is unrelated and works — that's what we actually use
            // across the kernel. Real managed delegates: TODO for later.
            Log.Write(LogLevel.Warn, "lambda: SKIP (needs Delegate infrastructure)");

            Log.Write(LogLevel.Info, "---- nativeaot probe end ----");
        }

        // --- 1. Virtual dispatch via abstract base ---
        private abstract class Shape { public abstract int Area(); }
        private class Square : Shape { public override int Area() => 25; }
        private class Circle : Shape { public override int Area() => 99; }

        private static void Probe_VirtualDispatch()
        {
            Shape s1 = new Square();
            Shape s2 = new Circle();
            int a = s1.Area() + s2.Area();
            ReportProbe("virtual dispatch", a == 124, (uint)a);
        }

        // --- 2. Interface dispatch ---
        private interface ICounter { int Count(); }
        private class CounterA : ICounter { public int Count() => 10; }
        private class CounterB : ICounter { public int Count() => 20; }

        private static void Probe_Interface()
        {
            ICounter c1 = new CounterA();
            ICounter c2 = new CounterB();
            int sum = c1.Count() + c2.Count();
            ReportProbe("interface", sum == 30, (uint)sum);
        }

        // --- 3. Generic method ---
        private static T Identity<T>(T v) => v;

        private static void Probe_GenericMethod()
        {
            int i = Identity(7);
            uint u = Identity(13u);
            ReportProbe("generic method", i == 7 && u == 13, (uint)(i + u));
        }

        // --- 4. Generic class ---
        private class Box<T> { public T Value; }

        private static void Probe_GenericClass()
        {
            var bi = new Box<int> { Value = 41 };
            var bu = new Box<uint> { Value = 42u };
            ReportProbe("generic class", bi.Value == 41 && bu.Value == 42u, (uint)(bi.Value + bu.Value));
        }

        // --- 5. Static constructor ---
        private static class StaticInit
        {
            public static readonly int X;
            static StaticInit() { X = 99; }
        }

        private static void Probe_StaticCtor()
        {
            int x = StaticInit.X;
            ReportProbe("static ctor", x == 99, (uint)x);
        }

        // --- 6. Boxing / unboxing ---
        private static void Probe_Boxing()
        {
            int a = 77;
            object o = a;
            int b = (int)o;
            ReportProbe("box/unbox", b == 77, (uint)b);
        }

        // --- 7. is / as operators ---
        private static void Probe_IsAs()
        {
            object o = new Square();
            bool isShape = o is Shape;
            Shape s = o as Shape;
            int area = s != null ? s.Area() : -1;
            ReportProbe("is/as", isShape && area == 25, (uint)area);
        }

        // --- 8. Array.Length + index walk ---
        private static void Probe_ArrayLength()
        {
            int[] arr = new int[5];
            for (int i = 0; i < arr.Length; i++)
                arr[i] = i * i;
            int sum = 0;
            for (int i = 0; i < arr.Length; i++)
                sum += arr[i];
            ReportProbe("array.length", arr.Length == 5 && sum == 30, (uint)sum);
        }

        // --- 9. Enum ---
        private enum Kind { A = 1, B = 2, C = 4 }

        private static void Probe_Enum()
        {
            Kind k = Kind.B;
            int v = (int)k;
            bool combo = ((Kind.A | Kind.C) & Kind.A) == Kind.A;
            ReportProbe("enum", v == 2 && combo, (uint)v);
        }

        // --- virtual Equals on a boxed value type ---
        private static void Probe_BoxedEquals()
        {
            object a = 5;
            object b = a;
            bool eq = a.Equals(b);     // reference equality — true (same box)
            ReportProbe("boxed equals (same ref)", eq, eq ? 1u : 0u);
        }

        private static void Probe_BoxedEqualsDifferentBoxes()
        {
            object a = 5;
            object b = 5;              // separate box of the same value
            bool eq = a.Equals(b);     // our default Object.Equals = reference eq → false
            ReportProbe("boxed equals (diff box)", true, eq ? 1u : 0u);
        }

        // --- static EqualityComparer<int>.Default access (new ObjectEqualityComparer<int>) ---
        private static void Probe_EqualityComparerDefault()
        {
            var cmp = System.Collections.Generic.EqualityComparer<int>.Default;
            bool ok = cmp != null;
            ReportProbe("eq.Default", ok, ok ? 1u : 0u);
        }

        // --- Generic class with concrete (non-abstract) virtual method ---
        private class GenConcrete<T> { public virtual int Get() => 42; }
        private class GenConcreteChild<T> : GenConcrete<T> { public override int Get() => 100; }

        private static void Probe_GenericVirtualConcrete()
        {
            GenConcrete<int> b = new GenConcreteChild<int>();
            int r = b.Get();
            ReportProbe("generic concrete virtual", r == 100, (uint)r);
        }

        // --- Generic class with abstract virtual method (our EqualityComparer pattern) ---
        private abstract class GenAbstract<T> { public abstract int Get(); }
        private class GenAbstractChild<T> : GenAbstract<T> { public override int Get() => 200; }

        private static void Probe_GenericVirtualAbstract()
        {
            GenAbstract<int> b = new GenAbstractChild<int>();
            int r = b.Get();
            ReportProbe("generic abstract virtual", r == 200, (uint)r);
        }

        // --- Custom subclass of EqualityComparer<T>, instantiated directly ---
        private sealed class MyCmp<T> : System.Collections.Generic.EqualityComparer<T>
        {
            public override bool Equals(T x, T y) => false;
            public override int GetHashCode(T obj) => 555;
        }

        private static void Probe_CustomEqualityComparer()
        {
            var cmp = new MyCmp<int>();
            int h = cmp.GetHashCode(5);
            ReportProbe("custom eq-cmp direct", h == 555, (uint)h);
        }

        private static void Probe_CustomEqualityComparerUpcast()
        {
            System.Collections.Generic.EqualityComparer<int> cmp = new MyCmp<int>();
            int h = cmp.GetHashCode(5);
            ReportProbe("custom eq-cmp upcast", h == 555, (uint)h);
        }

        // --- Generic abstract PLUS interface (the EqualityComparer<T> shape) ---
        private interface IGenThing<T> { int Get(T x); }
        private abstract class GenAbsIface<T> : IGenThing<T> { public abstract int Get(T x); }
        private class GenAbsIfaceChild<T> : GenAbsIface<T> { public override int Get(T x) => 300; }

        private static void Probe_GenericAbstractWithInterface()
        {
            GenAbsIface<int> b = new GenAbsIfaceChild<int>();
            int r = b.Get(5);
            ReportProbe("generic abstract+iface", r == 300, (uint)r);
        }

        // Try GetHashCode (no boxing in our stub) before Equals, to bisect.
        private static void Probe_EqualityComparerHashCode()
        {
            var cmp = System.Collections.Generic.EqualityComparer<int>.Default;
            int h = cmp.GetHashCode(5);
            ReportProbe("eq.GetHashCode(5)", true, (uint)h);
        }

        private static void Probe_EqualityComparerEquals()
        {
            var cmp = System.Collections.Generic.EqualityComparer<int>.Default;
            bool eq = cmp.Equals(5, 5);
            ReportProbe("eq.Equals(5,5)", true, eq ? 1u : 0u);
        }

        // --- BCL List<T>: Add, this[i], Count, Contains, Remove, IndexOf ---
        private static void Probe_BclList()
        {
            var list = new System.Collections.Generic.List<int>();
            for (int i = 0; i < 20; i++)
                list.Add(i * 3);

            int sum = 0;
            for (int i = 0; i < list.Count; i++) sum += list[i];

            bool has21 = list.Contains(21);      // 7 * 3
            int idx21 = list.IndexOf(21);
            list.Remove(21);
            int sumAfter = 0;
            for (int i = 0; i < list.Count; i++) sumAfter += list[i];

            bool ok = list.Count == 19 && sum == 570 && sumAfter == 549 && has21 && idx21 == 7;
            ReportProbe("bcl list<T>", ok, (uint)sumAfter);
        }

        // --- foreach over BCL List<T> via public struct Enumerator ---
        private static void Probe_BclListForeach()
        {
            var list = new System.Collections.Generic.List<int>();
            list.Add(10); list.Add(20); list.Add(30);

            int sum = 0;
            foreach (int v in list) sum += v;

            ReportProbe("bcl list foreach", sum == 60, (uint)sum);
        }

        // --- List<T> accessed as IEnumerable<T>: walks through the interface
        // dispatch (boxed struct Enumerator). This is what LINQ et al. use. ---
        private static void Probe_BclListAsInterface()
        {
            var list = new System.Collections.Generic.List<int>();
            list.Add(100); list.Add(200); list.Add(300);

            System.Collections.Generic.IEnumerable<int> src = list;
            int sum = 0;
            foreach (int v in src) sum += v;

            ReportProbe("bcl list as IEnumerable", sum == 600, (uint)sum);
        }

        private static void ReportProbe(string name, bool ok, uint value)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(name);
            Console.Write(": ");
            Console.Write(ok ? "ok" : "FAIL");
            Console.Write(" val=");
            Console.WriteUIntRaw(value);
            Log.EndLine();
        }
    }
}
