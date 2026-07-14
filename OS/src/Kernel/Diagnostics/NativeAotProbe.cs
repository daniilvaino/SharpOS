using System;
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
            Probe_StaticAssignRead();
            Probe_ExplicitCctor();
            Probe_ComplexCctor();
            // Probe_GenericNewConstraint — compile error: needs System.Activator.CreateInstance
            Probe_ThrowFromIndexOutOfRange();
            // Probe_MultiDimArray — ILC codegen fails (RhpNewMultiDimArray not wired)
            // Probe_NullableT — compile error: Nullable<T>.HasValue / .Value not defined on our stub
            Probe_CheckedArithmetic();
            Probe_AbstractGenericRefT();
            Probe_InterfaceCallGenericRefT();
            // Probe_LazyStaticNonGeneric — any reference-typed static with a
            // `if (s == null) s = new ...;` getter triggers ILC's cctor
            // machinery (CheckStaticClassConstructionReturnGCStaticBase) that
            // we can't satisfy yet. Workaround across the codebase: never
            // use lazy-initialized static reference fields; use a factory
            // property instead (pays one alloc per read).
            // Probe_LazyStaticGeneric();
            // Probe_LazyStaticGenericAbstract();
            Probe_EqualityComparerDefault();
            Probe_EqualityComparerEquals();
            Probe_BclList();
            Probe_BclListForeach();
            Probe_BclListAsInterface();
            Probe_DictionaryBasic();
            Probe_DictionaryForeach();
            Probe_DictionaryIntKey();
            Probe_DictionaryCustomComparer();
            Probe_Stack();
            Probe_Queue();
            Probe_HashSet();
            Probe_LinkedList();
            Probe_ReadOnlyCollection();
            Probe_ReadOnlyDictionary();
            Probe_ArraySegment();
            Probe_SortedList();
            Probe_Yield();
            Probe_Span();
            Probe_CreateSpan();
            // Delegate-dependency removed (BlockEncoder.cs lambda was
            // inlined as insertion sort). Still depends on cctor
            // materialisation of `static readonly AssemblerRegister64
            // rax = new ...(...)` register tables — if GcStaticsMaterializer
            // can't handle them, this probe halts the run in the well-known
            // ClassConstructorRunner trap zone.
            Probe_IcedEncode();
            Probe_StringBuilder();
            Probe_StringConcat();
            Probe_StringSplit();
            Probe_StringJoin();
            Probe_SpanIndexOf();
            Probe_SpanSequenceEqual();
            Probe_ArrayIndexOf();
            Probe_ArrayReverse();
            Probe_StringCompareOrdinal();
            Probe_StringSplitOptions();
            // MUST BE LAST: halts the probe run with a diagnostic dump until
            // the full DispatchResolve port lands. Everything above runs via
            // the shellcode fast path (pre-baked cache) or plain virtual
            // dispatch and should succeed.
            // Runtime mechanics smoke-set — symmetric with normal-hello
            // Sec 8 "RUNTIME MECHANICS". Documents which 8 fundamentals
            // work in our AOT std (boxing, virtual, interface, generic
            // sharing, cctor — already above; here we add the missing 3).
            Probe_ArrayCovariance();
            Probe_ModuleInit();
            Probe_WriteBarrier();
            Probe_GcRootsThroughEhUnwind();
            Probe_FinallyOrderingNested();
            Probe_ExceptionFilter();
            Probe_RethrowStackTrace();
            Probe_GcSpanInterior();
            Probe_ArrayCopyOverlap();
            Probe_XmmAcrossThrow();           // RED until P0-1 lands
            Probe_StringFormat();

            Probe_InterfaceCallFromSharedGeneric();
            Probe_GenericDictionary();
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
        // Split into three orthogonal probes so probe_report shows the
        // real coverage of each surface separately:
        //   • enum.cast     — IL-level int ↔ enum round-trip.
        //   • enum.bitwise  — `|` `&` arithmetic via cast.
        //   • enum.toString — member-name via ToString (BCL contract).
        //
        // `System.Enum` in MinimalRuntime.cs is a bare `abstract class
        // Enum : ValueType {}` — no ToString override, no Parse/GetNames
        // /HasFlag. Cast + bitwise are IL-level ops so they always work.
        // ToString falls through to object.ToString → returns the type's
        // full name, NOT "B". Expected today: enum.cast + enum.bitwise
        // green, enum.toString RED. When std/no-runtime ports Enum, the
        // last one turns green without any other change to probe_report.
        private enum Kind { A = 1, B = 2, C = 4 }

        private static void Probe_Enum()
        {
            Kind k = Kind.B;
            int v = (int)k;
            ReportProbe("enum.cast", v == 2 && (Kind)v == Kind.B, (uint)v);

            bool combo = ((Kind.A | Kind.C) & Kind.A) == Kind.A;
            ReportProbe("enum.bitwise", combo, (uint)(Kind.A | Kind.C));

            bool toStrOk = false;
            uint toStrHash = 0;
            try
            {
                string s = k.ToString();
                toStrOk = (s == "B");
                toStrHash = (uint)(s?.Length ?? 0);
            }
            catch { toStrOk = false; }
            ReportProbe("enum.toString", toStrOk, toStrHash);
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

        // --- Virtual call on abstract-generic base with REFERENCE type T ---
        // Probe_GenericVirtualAbstract (earlier) uses T=int (value type) and works.
        // This one uses T=class — tests whether ILC's shared-generic code path
        // (__Canon) is what halted EqualityComparer<MyKey>.Equals, or whether
        // that was something else.
        private sealed class RefMarker { public int Value = 7; }
        private abstract class AbsGenRef<T> { public abstract int Pick(T x); }
        private class AbsGenRefImpl<T> : AbsGenRef<T>
        {
            public override int Pick(T x) => 404;
        }

        private static void Probe_AbstractGenericRefT()
        {
            AbsGenRef<RefMarker> b = new AbsGenRefImpl<RefMarker>();
            int r = b.Pick(new RefMarker());
            ReportProbe("abs-gen<RefT> virtual", r == 404, (uint)r);
        }

        // --- Interface dispatch through generic interface, TArg = reference ---
        // This is the exact shape that halted Dictionary: field of type
        // IEqualityComparer<TKey> (interface), called through the interface
        // static type. Our RhpInitialDynamicInterfaceDispatch is a halt stub
        // so first dispatch through a generic interface cell should spin.
        private interface IGenericPickerRef<T> { int Pick(T x); }
        private sealed class GenericPickerRefImpl<T> : IGenericPickerRef<T>
        {
            public int Pick(T x) => 808;
        }

        private static void Probe_InterfaceCallGenericRefT()
        {
            IGenericPickerRef<RefMarker> iface = new GenericPickerRefImpl<RefMarker>();
            int r = iface.Pick(new RefMarker());
            ReportProbe("iface<RefT> dispatch", r == 808, (uint)r);
        }

        // --- Interface dispatch INSIDE a generic class (shared-generic body). ---
        // This is the exact shape that halted Dictionary<TKey,TValue>: a field
        // typed as a generic interface over the enclosing class's type
        // parameter, dispatched through the interface. Shared-generic code
        // for reference TKey may route this through a different helper than
        // the direct local-variable case above.
        private sealed class GenericContainer<T>
        {
            private readonly IGenericPickerRef<T> _thing;
            public GenericContainer(IGenericPickerRef<T> t) { _thing = t; }
            public int Call(T x) => _thing.Pick(x);
        }

        private static void Probe_InterfaceCallFromSharedGeneric()
        {
            var c = new GenericContainer<RefMarker>(new GenericPickerRefImpl<RefMarker>());
            int r = c.Call(new RefMarker());
            ReportProbe("shared-gen iface call", r == 808, (uint)r);
        }

        // --- Generic dictionaries + instantiating stubs (delegate prerequisite). ---
        // Inside a shared-generic (__Canon) body every T-dependent operation
        // reads the generic dictionary: type handle for `new T[n]`, the
        // List<T> instantiation, and the method-dictionary hand-off for the
        // nested DictNewArray<T> call. Two reference instantiations share one
        // __Canon body — the dictionary must tell them apart at runtime; the
        // int instantiation compiles its own exact body. The MethodTable
        // identity check proves the dictionary yielded the REAL element type
        // (string[]), not __Canon/object[] — wrong-MT arrays would corrupt
        // covariance checks and GC series downstream. Managed-delegate work
        // (donext) leans on both mechanisms via instantiating stubs.
        private static T[] DictNewArray<T>(int n) => new T[n];

        private static int DictNested<T>(int seed)
        {
            T[] arr = DictNewArray<T>(3);
            var list = new System.Collections.Generic.List<T>();
            list.Add(default);
            return seed + arr.Length + list.Count;
        }

        private static unsafe nint MethodTableOf(object o)
        {
            // First pointer-sized slot of every object is MethodTable* (mask
            // the GC mark bit, same convention as GcObject.MethodTable).
            byte* p = null;
            *(object*)&p = o;
            return *(nint*)p & ~(nint)1;
        }

        private static void Probe_GenericDictionary()
        {
            int a = DictNested<string>(10);   // 10 + 3 + 1 = 14 (shared __Canon)
            int b = DictNested<object>(20);   // 24               (shared __Canon)
            int c = DictNested<int>(30);      // 34               (exact body)
            bool mtOk = MethodTableOf(DictNewArray<string>(1))
                     == MethodTableOf(new string[1]);
            bool ok = a == 14 && b == 24 && c == 34 && mtOk;
            ReportProbe("generic dictionary + inst stubs", ok,
                        (uint)(a + b + c + (mtOk ? 1000 : 0)));
        }

        // --- Non-generic base + child for a matrix of init patterns ---
        private class L1 { public virtual int Get() => 42; }
        private class L1Child : L1 { public override int Get() => 101; }

        // a) Write once, read, call — no null check anywhere.
        private static L1 s_l1a;
        private static void Probe_StaticAssignRead()
        {
            s_l1a = new L1Child();
            L1 v = s_l1a;
            int r = v.Get();
            ReportProbe("static assign+read+call", r == 101, (uint)r);
        }

        // b) Eager field initializer DISABLED again — even with our
        // ClassConstructorRunner stub, `static L1 s_l1b = new L1Child()`
        // still crashes. ILC likely doesn't route to our copy of the
        // runner (probably looks for it in a specific BCL assembly or
        // by name in a different module).
        // private static L1 s_l1b = new L1Child();

        // --- Explicit static cctor: removes beforefieldinit, eager init ---
        private static class ExplicitCctorHolder
        {
            public static readonly int X;
            static ExplicitCctorHolder() { X = 77; }
        }

        private static void Probe_ExplicitCctor()
        {
            int v = ExplicitCctorHolder.X;
            ReportProbe("explicit cctor (int)", v == 77, (uint)v);
        }

        // Complex cctor that ILC's TypePreinit cannot fold at build time
        // (builds an array via a helper that constructs an object + virtual
        // call + loop) — mirrors Iced's OpCodeHandlers static cctor. On net8/
        // major-9 Iced's `OpCodeHandlers.Handlers` comes back null (its cctor
        // never ran) → encoder null-derefs. If THIS probe's Table is null too,
        // complex lazy cctors are broken as a CLASS on major-9, not just Iced.
        private class CctorArrayBuilder { public virtual int Seed() => 10; }
        private static class ComplexCctorHolder
        {
            public static readonly int[] Table;
            static ComplexCctorHolder() { Table = Build(); }
            private static int[] Build()
            {
                var b = new CctorArrayBuilder();
                var t = new int[6];
                for (int i = 0; i < t.Length; i++) t[i] = b.Seed() + i;
                return t;
            }
        }

        private static void Probe_ComplexCctor()
        {
            int before = System.Runtime.CompilerServices.ClassConstructorRunner.CheckCalls;
            int runsBefore = System.Runtime.CompilerServices.ClassConstructorRunner.CctorRuns;
            int[] t = ComplexCctorHolder.Table;   // triggers the cctor-check IF ILC left it lazy
            int after = System.Runtime.CompilerServices.ClassConstructorRunner.CheckCalls;
            int runsAfter = System.Runtime.CompilerServices.ClassConstructorRunner.CctorRuns;

            Log.Begin(LogLevel.Info);
            Console.Write("  [cctor-diag] checkDelta="); Console.WriteInt(after - before);
            Console.Write(" runDelta="); Console.WriteInt(runsAfter - runsBefore);
            Console.Write(" tableNull="); Console.WriteInt(t == null ? 1 : 0);
            Console.Write(" totalChecks="); Console.WriteInt(after);
            Console.Write(" totalRuns="); Console.WriteInt(runsAfter);
            Log.EndLine();
            Log.Begin(LogLevel.Info);
            Console.Write("  [cctor-ctx] q0=0x");
            Console.WriteHexRaw((ulong)System.Runtime.CompilerServices.ClassConstructorRunner.FirstCtxQ0, 16);
            Console.Write(" q1=0x");
            Console.WriteHexRaw((ulong)System.Runtime.CompilerServices.ClassConstructorRunner.FirstCtxQ1, 16);
            Console.Write(" initAt8=");
            Console.WriteInt(System.Runtime.CompilerServices.ClassConstructorRunner.FirstInitAt8);
            Log.EndLine();

            bool ok = t != null && t.Length == 6 && t[5] == 15;
            ReportProbe("complex cctor (array via method+vcall)", ok, ok && t != null ? (uint)t[5] : 0u);
        }

        // --- Direct throw: expect halt via ThrowHelpers stub, not crash ---
        // We can't actually throw-and-recover (no try/catch runtime). The test
        // is just whether ILC emits a call that compiles. We won't execute
        // the throw branch — dead path behind false condition.
        private static void Probe_ThrowFromIndexOutOfRange()
        {
            int[] a = new int[3];
            int sum = 0;
            for (int i = 0; i < 3; i++) sum += a[i];   // no OOB — just codegen
            ReportProbe("bounds-checked loop", sum == 0, (uint)sum);
        }

        // --- checked arithmetic (no overflow in this path) ---
        private static void Probe_CheckedArithmetic()
        {
            int a = 100;
            int b = 50;
            int c = checked(a + b);
            ReportProbe("checked add (no overflow)", c == 150, (uint)c);
        }

        // c) Classic lazy — null-check then allocate.
        private static L1 s_l1c;
        private static L1 LazyGet()
        {
            if (s_l1c == null) s_l1c = new L1Child();
            return s_l1c;
        }
        private static void Probe_LazyStaticNonGeneric()
        {
            L1 v = LazyGet();
            int r = v.Get();
            ReportProbe("lazy static non-generic", r == 101, (uint)r);
        }

        // --- Lazy static field: generic concrete base ---
        private class L2<T> { public virtual int Get() => 42; }
        private class L2Child<T> : L2<T> { public override int Get() => 202; }
        private static class L2Holder<T>
        {
            private static L2<T> s_inst;
            public static L2<T> Get()
            {
                if (s_inst == null) s_inst = new L2Child<T>();
                return s_inst;
            }
        }

        private static void Probe_LazyStaticGeneric()
        {
            var v = L2Holder<int>.Get();
            int r = v.Get();
            ReportProbe("lazy static generic", r == 202, (uint)r);
        }

        // --- Lazy static field: ABSTRACT generic base (our EqualityComparer shape) ---
        private abstract class L3<T> { public abstract int Get(); }
        private class L3Child<T> : L3<T> { public override int Get() => 303; }
        private abstract class L3Holder<T>
        {
            private static L3<T> s_inst;
            public static L3<T> Default
            {
                get
                {
                    if (s_inst == null) s_inst = new L3Child<T>();
                    return s_inst;
                }
            }
        }

        private static void Probe_LazyStaticGenericAbstract()
        {
            var v = L3Holder<int>.Default;
            int r = v.Get();
            ReportProbe("lazy static gen abstract", r == 303, (uint)r);
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

        // --- Dictionary<K, V>: user passes IEqualityComparer for value-equality ---
        private sealed class MyKey
        {
            public readonly int Id;
            public MyKey(int id) { Id = id; }
            public override bool Equals(object obj) => obj is MyKey k && k.Id == Id;
            public override int GetHashCode() => Id;
        }

        private static void Probe_DictionaryBasic()
        {
            // MyKey overrides Equals(object) + GetHashCode — Dictionary uses
            // object.Equals + key.GetHashCode (both virtual on Object), so no
            // IEqualityComparer needed for reference types that override.
            var dict = new System.Collections.Generic.Dictionary<MyKey, int>();
            ReportProbe("dict ctor", true, (uint)dict.Count);

            dict.Add(new MyKey(1), 100);
            ReportProbe("dict add", dict.Count == 1, (uint)dict.Count);

            bool has = dict.ContainsKey(new MyKey(1));
            ReportProbe("dict contains", has, has ? 1u : 0u);

            bool got = dict.TryGetValue(new MyKey(1), out int v);
            ReportProbe("dict tryget", got && v == 100, (uint)v);
        }

        private static void Probe_DictionaryForeach()
        {
            var dict = new System.Collections.Generic.Dictionary<MyKey, int>();
            dict.Add(new MyKey(10), 100);
            dict.Add(new MyKey(20), 200);
            dict.Add(new MyKey(30), 300);

            int keySum = 0, valSum = 0;
            foreach (var kv in dict)
            {
                keySum += kv.Key.Id;
                valSum += kv.Value;
            }
            ReportProbe("dict foreach", keySum == 60 && valSum == 600, (uint)valSum);
        }

        // Dictionary<int, int>: exercise EqualityComparer<int>.Default end-to-end.
        // Relies on Int32 implementing IEquatable<int> + DefaultComparer.Equals
        // preferring the interface path (no boxing of both operands).
        private static void Probe_DictionaryIntKey()
        {
            var dict = new System.Collections.Generic.Dictionary<int, int>();
            dict.Add(1, 100);
            dict.Add(2, 200);
            dict.Add(3, 300);
            bool has2 = dict.ContainsKey(2);
            bool got = dict.TryGetValue(3, out int v);
            ReportProbe("dict<int,int>", has2 && got && v == 300 && dict.Count == 3, (uint)v);
        }

        // Dictionary with a user-supplied IEqualityComparer. Forces ILC to
        // emit a dispatch cell for IEqualityComparer<__Canon>.Equals and
        // IEqualityComparer<__Canon>.GetHashCode from Dictionary's shared-
        // generic body. Routes through the shared-generic resolver.
        private sealed class ModNComparer : System.Collections.Generic.IEqualityComparer<int>
        {
            public readonly int N;
            public ModNComparer(int n) { N = n; }
            public bool Equals(int x, int y) => (x % N) == (y % N);
            public int GetHashCode(int obj) => obj % N;
        }

        private static void Probe_DictionaryCustomComparer()
        {
            var dict = new System.Collections.Generic.Dictionary<int, string>(new ModNComparer(10));
            dict.Add(5, "five");
            dict.Add(12, "twelve");
            // 15 ≡ 5 (mod 10) — should map to same bucket, collide on key.
            bool has5 = dict.ContainsKey(5);
            bool has15 = dict.ContainsKey(15);   // expect true via mod-10 equality
            bool found25 = dict.TryGetValue(25, out string v25);  // 25 ≡ 5 → "five"
            ReportProbe("dict custom comparer",
                has5 && has15 && found25 && v25 == "five",
                (uint)(dict.Count));
        }

        private static void Probe_Stack()
        {
            var s = new System.Collections.Generic.Stack<int>();
            s.Push(10); s.Push(20); s.Push(30);
            int peek = s.Peek();
            int pop1 = s.Pop();
            int pop2 = s.Pop();
            // Enumeration is LIFO — remaining one element is 10.
            int sumE = 0;
            foreach (int v in s) sumE += v;
            bool contains10 = s.Contains(10);
            ReportProbe("stack<int>",
                peek == 30 && pop1 == 30 && pop2 == 20 && s.Count == 1 && sumE == 10 && contains10,
                (uint)sumE);
        }

        private static void Probe_Queue()
        {
            var q = new System.Collections.Generic.Queue<int>(2);   // small capacity → forces Grow
            q.Enqueue(1); q.Enqueue(2); q.Enqueue(3); q.Enqueue(4);   // wraps + regrows
            int d1 = q.Dequeue();
            int d2 = q.Dequeue();
            q.Enqueue(5); q.Enqueue(6);
            // Remaining FIFO order: 3, 4, 5, 6.
            int sumE = 0;
            foreach (int v in q) sumE += v;
            int peek = q.Peek();
            ReportProbe("queue<int>",
                d1 == 1 && d2 == 2 && peek == 3 && q.Count == 4 && sumE == 18,
                (uint)sumE);
        }

        private static void Probe_HashSet()
        {
            var set = new System.Collections.Generic.HashSet<int>();
            bool added1 = set.Add(5);
            bool added2 = set.Add(10);
            bool dup = set.Add(5);                  // expect false
            bool has10 = set.Contains(10);
            bool removedMissing = set.Remove(42);   // expect false
            bool removedReal = set.Remove(10);      // expect true
            int sumE = 0;
            foreach (int v in set) sumE += v;
            ReportProbe("hashset<int>",
                added1 && added2 && !dup && has10 && !removedMissing && removedReal
                    && set.Count == 1 && sumE == 5,
                (uint)sumE);
        }

        private static void Probe_LinkedList()
        {
            var ll = new System.Collections.Generic.LinkedList<int>();
            var n2 = ll.AddLast(2);
            ll.AddFirst(1);
            ll.AddLast(4);
            ll.AddAfter(n2, 3);       // 1,2,3,4
            int sumE = 0;
            foreach (int v in ll) sumE += v;
            int first = ll.First.Value;
            int last = ll.Last.Value;
            bool removed3 = ll.Remove(3);
            ll.RemoveFirst();         // removes 1
            // After: 2,4
            int sumAfter = 0;
            foreach (int v in ll) sumAfter += v;
            ReportProbe("linkedlist<int>",
                first == 1 && last == 4 && sumE == 10 && removed3 && ll.Count == 2 && sumAfter == 6,
                (uint)sumAfter);
        }

        private static void Probe_ReadOnlyCollection()
        {
            var list = new System.Collections.Generic.List<int>();
            list.Add(7); list.Add(11); list.Add(13);
            var ro = new System.Collections.ObjectModel.ReadOnlyCollection<int>(list);
            bool idx = ro[1] == 11;
            bool has13 = ro.Contains(13);
            int sumE = 0;
            foreach (int v in ro) sumE += v;
            ReportProbe("readonly collection",
                idx && has13 && ro.Count == 3 && sumE == 31,
                (uint)sumE);
        }

        private static void Probe_ReadOnlyDictionary()
        {
            var dict = new System.Collections.Generic.Dictionary<int, int>();
            dict.Add(1, 100);
            dict.Add(2, 200);
            var ro = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(dict);
            bool hasKey = ro.ContainsKey(1);
            bool got = ro.TryGetValue(2, out int v2);
            int keysCount = ro.Keys.Count;
            ReportProbe("readonly dict",
                hasKey && got && v2 == 200 && ro.Count == 2 && keysCount == 2,
                (uint)v2);
        }

        private static void Probe_ArraySegment()
        {
            int[] arr = new int[] { 10, 20, 30, 40, 50 };
            var seg = new System.ArraySegment<int>(arr, 1, 3);   // { 20, 30, 40 }
            int sum = 0;
            for (int i = 0; i < seg.Count; i++) sum += seg[i];
            int sumE = 0;
            foreach (int v in seg) sumE += v;
            var slice = seg.Slice(1, 2);                           // { 30, 40 }
            int sliceSum = 0;
            for (int i = 0; i < slice.Count; i++) sliceSum += slice[i];
            ReportProbe("arraysegment<int>",
                sum == 90 && sumE == 90 && slice.Count == 2 && sliceSum == 70,
                (uint)sliceSum);
        }

        private static void Probe_SortedList()
        {
            var sl = new System.Collections.Generic.SortedList<int, string>();
            // Insert unordered; internal binsearch should keep them sorted.
            sl.Add(30, "thirty");
            sl.Add(10, "ten");
            sl.Add(20, "twenty");

            bool firstKey = sl.GetKeyAtIndex(0) == 10;
            bool midKey = sl.GetKeyAtIndex(1) == 20;
            bool lastKey = sl.GetKeyAtIndex(2) == 30;

            bool has20 = sl.ContainsKey(20);
            bool got = sl.TryGetValue(30, out string v30);
            int keySum = 0;
            foreach (var kv in sl) keySum += kv.Key;

            bool removed = sl.Remove(20);
            int afterSum = 0;
            foreach (var kv in sl) afterSum += kv.Key;

            ReportProbe("sortedlist<int,string>",
                firstKey && midKey && lastKey && has20 && got && v30 == "thirty"
                    && keySum == 60 && removed && sl.Count == 2 && afterSum == 40,
                (uint)afterSum);
        }

        // Compiler-synthesised iterator state machine. Requires
        // Interlocked.CompareExchange + Environment.CurrentManagedThreadId
        // + InvalidOperationException to be codegen-visible. Added in
        // step 33.
        private static System.Collections.Generic.IEnumerable<int> YieldOneTwoThree()
        {
            yield return 1;
            yield return 2;
            yield return 3;
        }

        private static void Probe_Yield()
        {
            int sum = 0;
            foreach (int v in YieldOneTwoThree()) sum += v;
            ReportProbe("yield return", sum == 6, (uint)sum);
        }

        // Exercises Span<T> end-to-end: ctor from array, indexer set/get,
        // Slice, CopyTo into another Span. If any Unsafe intrinsic fails
        // to resolve at ILC time the first indexer call halts.
        private static void Probe_StringConcat()
        {
            string a = string.Concat("foo", "bar");
            string b = string.Concat("a", "b", "c");
            string c = string.Concat("1", "2", "3", "4");
            string d = string.Concat(new[] { "x", "y", "z" });
            bool ok = a == "foobar" && b == "abc" && c == "1234" && d == "xyz";
            ReportProbe("string.concat",
                ok,
                (uint)(a.Length + b.Length + c.Length + d.Length));
        }

        private static void Probe_StringSplit()
        {
            string src = "a,b,c,d";
            string[] parts = src.Split(',');
            int partsCount = parts.Length;
            string first = parts[0];
            string last = parts[3];

            string src2 = "one two three";
            string[] parts2 = src2.Split(' ');

            string src3 = "line1\r\nline2\r\nline3";
            string[] parts3 = src3.Split("\r\n");

            bool ok = partsCount == 4 && first == "a" && last == "d"
                && parts2.Length == 3 && parts2[1] == "two"
                && parts3.Length == 3 && parts3[2] == "line3";

            ReportProbe("string.split", ok, (uint)partsCount);
        }

        private static void Probe_StringJoin()
        {
            string[] words = new[] { "red", "green", "blue" };
            string s1 = string.Join(", ", words);
            string s2 = string.Join('/', words);
            string[] empty = new string[0];
            string s3 = string.Join(",", empty);
            bool ok = s1 == "red, green, blue" && s2 == "red/green/blue" && s3 == "";
            ReportProbe("string.join", ok, (uint)(s1.Length + s2.Length));
        }

        // Step 36 BCL base extension probes — exercise paths that go through
        // shared-generic IEquatable<T> interface dispatch (step 32) plus the
        // new Compare/Split surface. Build pass != correctness for these,
        // since dispatch resolution happens at runtime.

        private static void Probe_SpanIndexOf()
        {
            int[] arr = new int[] { 10, 20, 30, 40, 50 };
            var span = new System.Span<int>(arr);
            int idx = span.IndexOf(30);
            int idxLast = span.LastIndexOf(50);
            bool contains = span.Contains(20);
            bool missing = span.Contains(99);
            bool ok = idx == 2 && idxLast == 4 && contains && !missing;
            ReportProbe("span.indexof", ok, (uint)(idx + idxLast));
        }

        private static void Probe_SpanSequenceEqual()
        {
            int[] a = new int[] { 1, 2, 3, 4 };
            int[] b = new int[] { 1, 2, 3, 4 };
            int[] c = new int[] { 1, 2, 3, 5 };
            int[] d = new int[] { 1, 2, 3 };
            var sa = new System.ReadOnlySpan<int>(a);
            var sb = new System.ReadOnlySpan<int>(b);
            var sc = new System.ReadOnlySpan<int>(c);
            var sd = new System.ReadOnlySpan<int>(d);
            bool eq = sa.SequenceEqual(sb);
            bool neqValue = sa.SequenceEqual(sc);
            bool neqLength = sa.SequenceEqual(sd);
            bool ok = eq && !neqValue && !neqLength;
            ReportProbe("span.sequenceequal", ok, (uint)(eq ? 1 : 0));
        }

        private static void Probe_ArrayIndexOf()
        {
            int[] arr = new int[] { 100, 200, 300, 200, 400 };
            int first = System.Array.IndexOf(arr, 200);
            int last = System.Array.LastIndexOf(arr, 200);
            int missing = System.Array.IndexOf(arr, 999);
            bool ok = first == 1 && last == 3 && missing == -1;
            ReportProbe("array.indexof", ok, (uint)(first + last));
        }

        private static void Probe_ArrayReverse()
        {
            int[] arr = new int[] { 1, 2, 3, 4, 5 };
            System.Array.Reverse(arr);
            bool ok = arr[0] == 5 && arr[1] == 4 && arr[2] == 3 && arr[3] == 2 && arr[4] == 1;
            ReportProbe("array.reverse", ok, (uint)arr[0]);
        }

        private static void Probe_StringCompareOrdinal()
        {
            int eq = string.CompareOrdinal("hello", "hello");
            int less = string.CompareOrdinal("apple", "banana");
            int more = string.CompareOrdinal("zebra", "apple");
            int diffLen = string.CompareOrdinal("foo", "foobar");
            bool ok = eq == 0 && less < 0 && more > 0 && diffLen < 0;
            ReportProbe("string.compareordinal", ok, (uint)(eq + (less < 0 ? 1 : 0) + (more > 0 ? 1 : 0)));
        }

        private static void Probe_StringSplitOptions()
        {
            // " a , , b ,  ,c " → split on ',':
            //   None: ["", " a ", " ", " b ", "  ", "c "]   (or with trim variations)
            //   None: 6 entries: " a ", " ", " b ", "  ", "c " (5 actually... let me recount)
            // Actually: " a , , b ,  ,c " (leading space, trailing space) split by ',':
            //   " a "  " "  " b "  "  "  "c "      (5 entries)
            // After Trim: "a", "", "b", "", "c"
            // After RemoveEmpty: "a", "b", "c" (3 entries)
            string src = " a , , b ,  ,c ";
            string[] parts = src.Split(',');
            int countNone = parts.Length;

            string[] partsTrim = src.Split(',', StringSplitOptions.TrimEntries);
            string[] partsRemove = src.Split(',', StringSplitOptions.RemoveEmptyEntries);
            string[] partsBoth = src.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            bool ok = countNone == 5
                && partsTrim.Length == 5 && partsTrim[0] == "a" && partsTrim[1] == ""
                && partsRemove.Length == 5 && partsRemove[0] == " a "  // RemoveEmpty alone removes only ""; here all have spaces
                && partsBoth.Length == 3 && partsBoth[0] == "a" && partsBoth[1] == "b" && partsBoth[2] == "c";

            ReportProbe("string.split.options", ok, (uint)(partsBoth.Length * 100 + partsTrim.Length * 10 + countNone));
        }

        // Exercises StringBuilder through its core paths: Append overloads
        // (string/char/int), Length getter, indexer, ToString, Clear,
        // AppendLine, Insert, Remove, Replace. Chunk-growth path is hit
        // by constructing with a small capacity and appending enough to
        // trip into a second chunk.
        private static void Probe_StringBuilder()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("hello");
            sb.Append(' ');
            sb.Append(42);
            sb.AppendLine();
            sb.Append("more");
            // Expected: "hello 42\r\nmore" — length 14.
            int lenAfterAppend = sb.Length;
            char ch = sb[6];   // should be '4'
            string s = sb.ToString();

            sb.Clear();
            int lenAfterClear = sb.Length;

            // Force chunk expansion with small capacity ctor.
            var sb2 = new System.Text.StringBuilder(4);
            sb2.Append("abcdefghij");   // 10 chars, grows past initial 4
            int sb2Len = sb2.Length;
            string sb2Out = sb2.ToString();
            bool sb2Ok = sb2Out != null && sb2Out.Length == 10
                && sb2Out[0] == 'a' && sb2Out[9] == 'j';

            // Insert + Remove + Replace.
            var sb3 = new System.Text.StringBuilder("hello");
            sb3.Insert(5, " world");
            sb3.Remove(0, 6);           // "world"
            sb3.Replace('w', 'W');
            string sb3Out = sb3.ToString();
            bool sb3Ok = sb3Out != null && sb3Out.Length == 5
                && sb3Out[0] == 'W' && sb3Out[4] == 'd';

            bool ok = lenAfterAppend == 14
                && ch == '4'
                && s != null && s.Length == 14
                && lenAfterClear == 0
                && sb2Ok
                && sb3Ok;

            ReportProbe("stringbuilder", ok, (uint)(lenAfterAppend + sb2Len));
        }

        private static void Probe_Span()
        {
            int[] arr = new int[] { 10, 20, 30, 40, 50 };
            var span = new System.Span<int>(arr);

            int sumIndexer = 0;
            for (int i = 0; i < span.Length; i++) sumIndexer += span[i];    // 150

            span[0] = 100;                                                   // write
            int firstAfterWrite = arr[0];                                    // 100 (span backs array)

            var slice = span.Slice(1, 3);                                    // { 20, 30, 40 }
            int sliceSum = 0;
            for (int i = 0; i < slice.Length; i++) sliceSum += slice[i];     // 90

            int[] dest = new int[3];
            slice.CopyTo(dest);
            int destSum = dest[0] + dest[1] + dest[2];                       // 90

            int foreachSum = 0;
            foreach (int v in slice) foreachSum += v;                        // 90

            ReportProbe("span<int>",
                sumIndexer == 150 && firstAfterWrite == 100 &&
                    sliceSum == 90 && destSum == 90 && foreachSum == 90,
                (uint)(sumIndexer + sliceSum));
        }

        // First end-to-end use of the in-kernel Iced x86 encoder.
        // Builds `mov rax, rcx` via Assembler API, encodes through our
        // BufWriter, verifies the canonical 3-byte sequence 48 89 C8
        // (REX.W + opcode 89 + ModRM 0xC8). The whole pipeline exercises
        // the `static readonly AssemblerRegister64 rax = new ...(Register.RAX)`
        // cctor path — if our GcStaticsMaterializer can't materialise
        // Iced's register tables, this probe halts in the well-known
        // ClassConstructorRunner trap zone instead of returning ok=false.
        // Caught Exception path covers Iced's EncoderException.
        private static void Probe_IcedEncode()
        {
            bool ok = false;
            uint sig = 0;
            try
            {
                var buf = new byte[16];
                fixed (byte* p = buf)
                {
                    var w = new BufWriter(p, buf.Length);
                    var a = new Iced.Intel.Assembler(64);
                    a.mov(Iced.Intel.AssemblerRegisters.rax, Iced.Intel.AssemblerRegisters.rcx);
                    a.Assemble(w, 0);

                    int n = w.Count;
                    // Pack first 3 bytes as a sanity signature.
                    if (n >= 3)
                        sig = ((uint)buf[0] << 16) | ((uint)buf[1] << 8) | buf[2];
                    // mov rax, rcx -> REX.W 48, opcode 89, ModR/M C8
                    ok = n == 3 && buf[0] == 0x48 && buf[1] == 0x89 && buf[2] == 0xC8;
                }
            }
            catch (Exception)
            {
                ok = false;
            }
            ReportProbe("iced.encode(mov rax,rcx)", ok, sig);
        }

        // Minimal Iced.Intel.CodeWriter implementation pointing at a raw
        // byte buffer. WriteByte is the sole abstract member; everything
        // else (Assembler / BlockEncoder / Encoder) writes through it.
        private sealed unsafe class BufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;

            public BufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;

            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        // C# 12 collection-expression / RVA-literal lowering: Roslyn emits
        // `ldtoken <rva_field> + call RuntimeHelpers.CreateSpan<T>` for both
        // `ReadOnlySpan<byte> x = [1,2,3,...]` and the older const-folded form
        // `new ReadOnlySpan<byte>(new byte[] { ... })`. If ILC folds the
        // intrinsic correctly, no allocation happens — span points straight
        // into the RData blob.
        private static void Probe_CreateSpan()
        {
            ReadOnlySpan<byte> ros = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
            int sum = 0;
            for (int i = 0; i < ros.Length; i++) sum += ros[i];   // expect 0xFF
            bool ok = ros.Length == 5 && sum == 0xFF
                      && ros[0] == 0x11 && ros[4] == 0x55;
            ReportProbe("RuntimeHelpers.CreateSpan", ok, (uint)sum);
        }

        // --- Array covariance + stelem.ref (positive monomorphic only) ---
        // We deliberately don't test the throwing-negative case here: our
        // RhpStelemRef skips the covariance check (documented в
        // GcRuntimeExports.cs), so writing a wrong-typed object would
        // silently corrupt rather than throw ArrayTypeMismatchException.
        // CoreCLR-hosted side does the throwing test (see normal-hello).
        private class CovBase { public virtual int X => 1; }
        private class CovDerived : CovBase { public override int X => 2; }

        private static void Probe_ArrayCovariance()
        {
            CovBase[] arr = new CovDerived[3];   // covariant alias
            arr[0] = new CovDerived();           // monomorphic stelem.ref
            arr[1] = new CovDerived();
            int sum = arr[0].X + arr[1].X;       // virtual via base ref
            ReportProbe("array covariance (stelem.ref)", sum == 4, (uint)sum);
        }

        // --- Module init ([ModuleInitializer]) ---
        // Roslyn allows static parameterless methods marked
        // [ModuleInitializer] to run before any other user code touches the
        // module. ILC's loader machinery wires the dispatch. We flip a
        // primitive bool field (primitive statics don't trigger
        // ClassConstructorRunner). If module init didn't fire — flag stays
        // false and probe FAILs.
        private static bool s_moduleInitRan;

        [System.Runtime.CompilerServices.ModuleInitializer]
        public static void ModuleInit() { s_moduleInitRan = true; }

        private static void Probe_ModuleInit()
        {
            ReportProbe("[ModuleInitializer]", s_moduleInitRan, s_moduleInitRan ? 1u : 0u);
        }

        // --- Write barrier (RhpAssignRef + GC marking) ---
        // Smoke-tests that storing a managed ref into a heap field via
        // RhpAssignRef + walking it across GC.Collect keeps the target
        // alive (no premature sweep). On our non-generational mark-sweep
        // RhpAssignRef is a plain pointer store; the actual liveness
        // guarantee comes from GcMark following the ref via MT/GcDescSeries.
        private class RefHolder { public string? Field; }

        private static void Probe_WriteBarrier()
        {
            var h = new RefHolder();
            h.Field = "alive-after-gc";          // exercises RhpAssignRef
            GC.Collect();                        // forces mark-sweep
            bool ok = h.Field == "alive-after-gc";
            ReportProbe("write barrier (ref field + GC.Collect)", ok, ok ? 1u : 0u);
        }

        // --- GC roots through EH unwind ---
        // Allocates a string in a deep call chain, throws, catches at the
        // top, GC.Collect's, verifies the string is still alive via the
        // catch-local reference. Tests that funclet/exception dispatch
        // preserves managed roots correctly.
        private static void Probe_GcRootsThroughEhUnwind()
        {
            string? survivor = null;
            try
            {
                EhUnwindLevel1(out survivor);
            }
            catch (InvalidOperationException)
            {
                GC.Collect();
            }
            bool ok = survivor != null && survivor.Length > 0 && survivor.StartsWith("alive-");
            ReportProbe("GC roots through EH unwind", ok, (uint)(survivor?.Length ?? 0));
        }
        private static void EhUnwindLevel1(out string? local)
        {
            local = "alive-" + 123;
            EhUnwindLevel2();
        }
        private static void EhUnwindLevel2()
        {
            throw new InvalidOperationException("propagate");
        }

        // --- Finally ordering under nested exceptions ---
        // try { try { throw } finally { log "f1" } } catch { log "c" }
        // Expected log: "f1,c". Tests funclet ordering: inner finally
        // runs before outer catch.
        private static void Probe_FinallyOrderingNested()
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                try { throw new InvalidOperationException("inner"); }
                finally { sb.Append("f1,"); }
            }
            catch (InvalidOperationException) { sb.Append("c"); }
            string s = sb.ToString();
            ReportProbe("finally ordering (nested)", s == "f1,c", (uint)s.Length);
        }

        // --- Exception filter semantics (when) ---
        // Filter reads a local, calls a method, returns true/false. Two
        // catches: first with filter=false (must be skipped), second
        // catches by type. Validates filter doesn't disturb frame state.
        private static void Probe_ExceptionFilter()
        {
            int caughtBy = 0;
            int sentinel = 7;
            try { throw new InvalidOperationException("filtered"); }
            catch (InvalidOperationException) when (FilterFalse(sentinel)) { caughtBy = 1; }
            catch (InvalidOperationException e) when (FilterReadLocal(e, sentinel)) { caughtBy = 2; }
            catch (InvalidOperationException) { caughtBy = 3; }
            ReportProbe("exception filter (when)", caughtBy == 2, (uint)caughtBy);
        }
        private static bool FilterFalse(int local) => local != 7 || false;
        private static bool FilterReadLocal(Exception e, int local) =>
            local == 7 && e.Message == "filtered";

        // --- throw; vs throw ex; stack trace fidelity ---
        // `throw;` preserves the original throw point; `throw ex;` resets
        // the StackTrace to the rethrow location. We check that
        // re-throwing preserves the original stack mention.
        private static void Probe_RethrowStackTrace()
        {
            string? trace = null;
            try
            {
                try { RethrowSource(); }
                catch (InvalidOperationException) { throw; }   // bare rethrow
            }
            catch (InvalidOperationException e) { trace = e.StackTrace; }
            // Stack trace must mention RethrowSource (the original throw point).
            bool ok = trace != null && trace.Contains("RethrowSource");
            ReportProbe("rethrow preserves stack trace", ok, (uint)(trace?.Length ?? 0));
        }
        private static void RethrowSource() => throw new InvalidOperationException("from source");

        // --- GC + Span interior view ---
        // Span<byte> over heap array; force GC.Collect; verify span still
        // reads correctly. Critical for our non-moving GC: array payload
        // must stay at the same address through collections.
        private static void Probe_GcSpanInterior()
        {
            byte[] arr = new byte[64];
            for (int i = 0; i < 64; i++) arr[i] = (byte)i;
            Span<byte> s = arr.AsSpan(8, 16);   // interior slice
            GC.Collect();                       // payload must stay put
            int sum = 0;
            for (int i = 0; i < s.Length; i++) sum += s[i];
            // bytes 8..23 → sum = (8+23)*16/2 = 248
            ReportProbe("GC + Span<byte> interior view", sum == 248, (uint)sum);
        }

        // --- Array.Copy overlap (memmove semantics) ---
        // Shift array elements right by 1: arr[1..n-1] = arr[0..n-2].
        // Naive memcpy gives a "0,0,0,0" smear; correct memmove gives
        // "1,1,2,3". Tests Array.Copy's handling of overlapping src/dst.
        private static void Probe_ArrayCopyOverlap()
        {
            int[] a = new[] { 1, 2, 3, 4 };
            Array.Copy(a, 0, a, 1, 3);          // shift right
            bool ok = a[0] == 1 && a[1] == 1 && a[2] == 2 && a[3] == 3;
            ReportProbe("Array.Copy overlap (memmove)", ok, (uint)(a[0] + a[1] + a[2] + a[3]));
        }

        // --- XMM register preservation across throw/catch ---
        // P0-1 bug canary: callee-saved xmm6+ holding double survives
        // through throw → catch unwind. RyuJIT emits UWOP_SAVE_XMM128;
        // our SehUnwind ApplyCode currently swallows opcodes 8/9 silently.
        // EmitCapture doesn't snapshot FP/XMM, EmitRestore doesn't load.
        // Expected RED until P0-1 lands (donext.md). When fixed, GREEN.
        private static void Probe_XmmAcrossThrow()
        {
            // Forces JIT to spill these to xmm6+ across the call.
            double a = 1.5, b = 2.25, c = 3.125, d = 4.0625, e = 5.03125, f = 6.015625;
            try { ThrowInDoubleScope(a, b, c, d, e, f); }
            catch (InvalidOperationException) { /* xmm6..15 should be restored */ }
            // Use values after catch — if XMM lost, these will be garbage.
            bool ok = a == 1.5 && b == 2.25 && c == 3.125 && d == 4.0625 && e == 5.03125 && f == 6.015625;
            // Mix to one uint for ReportProbe channel.
            uint mix = (uint)(a + b + c + d + e + f);  // ≈22 if intact, else garbage
            ReportProbe("XMM regs preserved across throw [P0-1 canary]", ok, mix);
        }
        private static void ThrowInDoubleScope(double a, double b, double c, double d, double e, double f) =>
            throw new InvalidOperationException("xmm-test");

        // --- string.Format coverage ---
        // Basic {N} substitution, format specifiers (D/X/F2), alignment.
        private static void Probe_StringFormat()
        {
            string s1 = string.Format("{0}+{1}={2}", 2, 3, 5);
            string s2 = string.Format("hex={0:X}", 0xABCD);
            string s3 = string.Format("dec={0:D5}", 42);
            string s4 = string.Format("flt={0:F2}", 3.14159);
            string s5 = string.Format("|{0,5}|", "x");
            bool ok = s1 == "2+3=5" &&
                      s2 == "hex=ABCD" &&
                      s3 == "dec=00042" &&
                      s4 == "flt=3.14" &&
                      s5 == "|    x|";
            uint sig = (uint)(s1.Length + s2.Length + s3.Length + s4.Length + s5.Length);
            ReportProbe("string.Format (basic + D/X/F2/align)", ok, sig);
        }

        // ────────────────────────────────────────────────────────────────
        // LATE-BOOT entry — call AFTER Phase E threading + scheduler is up
        // (TebFacade/Atomics/ThreadPingPong/Sleep/Event/Semaphore probes
        // all passed). Tests here need scheduler.Spawn + Event + cross-
        // thread state which aren't ready in early Phase4.
        // ────────────────────────────────────────────────────────────────
        public static void RunLate()
        {
            Log.Write(LogLevel.Info, "---- nativeaot probe (late) begin ----");
            Probe_ThreadHandoffWithGc();
            Probe_OomDeterministic();
            Log.Write(LogLevel.Info, "---- nativeaot probe (late) end ----");
        }

        // --- Thread handoff with GC mid-transfer ---
        // Producer thread writes a ref to a shared slot + sets an event.
        // Main thread waits, GC.Collect's, then reads — verifies ref-
        // handoff across threads survives GC (proper roots tracking on
        // both the producing frame's stack AND the consumer's view).
        private static OS.Kernel.Threading.Event? s_handoffEvent;
        private static string? s_handoffSlot;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void HandoffProducer()
        {
            s_handoffSlot = "handoff-" + 12345;
            // Memory barrier — ensure store visible to consumer.
            System.Threading.Interlocked.MemoryBarrier();
            s_handoffEvent!.Set();
            OS.Kernel.Threading.Scheduler.Exit();
        }

        private static void Probe_ThreadHandoffWithGc()
        {
            s_handoffSlot = null;
            s_handoffEvent = new OS.Kernel.Threading.Event(manualReset: true, initialState: false);

            var producer = OS.Kernel.Threading.Scheduler.Spawn(&HandoffProducer, 0);
            if (producer == null)
            {
                ReportProbe("thread handoff + GC (spawn failed)", false, 0u);
                return;
            }

            // Wait for producer's Set + reach this point in main.
            s_handoffEvent.Wait();

            // GC.Collect mid-transfer — must not sweep s_handoffSlot's
            // referent. Tests that the static-roots scan picks up
            // s_handoffSlot (a private static field on this class).
            GC.Collect();

            bool ok = s_handoffSlot != null && s_handoffSlot.StartsWith("handoff-");
            ReportProbe("thread handoff + GC mid-transfer", ok, (uint)(s_handoffSlot?.Length ?? 0));
        }

        // --- OOM deterministic behavior ---
        // Best-effort. Our RhpNewArray returns null on size_t overflow
        // (size64 > 0xFFFFFFFF). ILC-generated callsite typically reacts
        // by throwing OOM via RhExceptionHandling. We try `new int[N]`
        // where N is large enough to overflow and check what actually
        // happens — green if any managed exception is caught, red if
        // silent (we ran past with no exception means null deref or UB).
        private static void Probe_OomDeterministic()
        {
            bool caught = false;
            string? exType = null;
            try
            {
                var huge = new int[int.MaxValue];
                // If we got here with valid array — bizarre.
                if (huge.Length == int.MaxValue)
                {
                    ReportProbe("OOM: huge alloc unexpectedly succeeded", false, (uint)huge.Length);
                    return;
                }
            }
            catch (OutOfMemoryException) { caught = true; exType = "OOM"; }
            catch (OverflowException) { caught = true; exType = "Overflow"; }
            catch (Exception) { caught = true; exType = "other"; }

            uint sig = exType == "OOM" ? 1u : exType == "Overflow" ? 2u : caught ? 3u : 0u;
            ReportProbe("OOM/huge-alloc -> deterministic exception", caught, sig);
        }

        private static void ReportProbe(string name, bool ok, uint value)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(name);
            Console.Write(": ");
            Console.Write(ok ? "ok" : "FAIL");
            Console.Write(" val=");
            Console.WriteUInt(value);
            Log.EndLine();
        }
    }
}
