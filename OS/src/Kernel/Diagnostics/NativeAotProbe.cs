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
            Probe_ListInt();
            Probe_ListForeach();
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

        // --- List<T>: Add, this[i], Count, grow across multiple reallocations ---
        private static void Probe_ListInt()
        {
            var list = new SharpOS.Std.Collections.List<int>();
            for (int i = 0; i < 20; i++)
                list.Add(i * 3);

            int sum = 0;
            for (int i = 0; i < list.Count; i++)
                sum += list[i];

            // 0+3+6+...+57 = 3 * (0+1+...+19) = 3 * 190 = 570
            ReportProbe("list<int>", list.Count == 20 && sum == 570, (uint)sum);
        }

        // --- foreach over List<T>: duck-typed struct Enumerator, no interface ---
        private static void Probe_ListForeach()
        {
            var list = new SharpOS.Std.Collections.List<int>(8);
            list.Add(10); list.Add(20); list.Add(30);

            int sum = 0;
            foreach (int v in list) sum += v;

            ReportProbe("list foreach", sum == 60, (uint)sum);
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
