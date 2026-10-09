using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using SharpOS.AppSdk;

namespace GvmApps
{
    // GVMTEST.EXE — generic virtual methods (step198): the shapes a library
    // like Kusto's parser combinators uses. Exit code = checks passed.
    //
    //  1. a virtual generic method overridden, called through the base,
    //     instantiated over a value type (exact code) and a reference type
    //     (shared code + a generic dictionary);
    //  2. an abstract generic method (the visitor's Accept<TResult>);
    //  3. a generic method of an interface;
    //  4. an override on a generic class (the implementer is an instantiation);
    //  5. the call made from shared generic code, the method's argument
    //     coming from the caller's dictionary;
    //  6. a type that does not override: the base's implementation.
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int s_passed, s_failed;

        private static void Check(string name, Func<bool> test)
        {
            bool ok;
            string why = null;
            try { ok = test(); }
            catch (Exception e) { ok = false; why = e.Message; }
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name + (why != null ? " — " + why : ""));
        }

        private static int Run()
        {
            Base onDerived = new Derived();
            Check("1 override, value type (exact)", () => onDerived.Describe<int>(7) == "Derived 7");
            Check("1 override, reference type (shared)", () => onDerived.Describe<string>("s") == "Derived s");

            Shape circle = new Circle();
            Check("2 abstract, visitor", () => circle.Accept(new NameVisitor()) == "circle");
            Check("2 abstract, visitor over int", () => circle.Accept(new SizeVisitor()) == 3);

            IGreeter greeter = new Greeter();
            Check("3 interface, value type", () => greeter.Greet<int>(1) == "hi 1");
            Check("3 interface, reference type", () => greeter.Greet<string>("x") == "hi x");

            Base onBox = new Box<long>();
            Check("4 generic implementer", () => onBox.Describe<string>("b") == "Box b");

            Check("5 from shared code", () => Through<string>(onDerived, "t") == "Derived t");
            Check("5 from shared code, value type", () => Through<int>(onDerived, 5) == "Derived 5");

            Base plain = new Plain();
            Check("6 not overridden: the base", () => plain.Describe<int>(2) == "Base 2");

            Console.WriteLine("[gvmtest] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string Through<T>(Base b, T value) => b.Describe<T>(value);
    }

    internal class Base
    {
        public virtual string Describe<T>(T value) => "Base " + value.ToString();
    }

    internal sealed class Derived : Base
    {
        public override string Describe<T>(T value) => "Derived " + value.ToString();
    }

    internal sealed class Plain : Base { }

    internal sealed class Box<X> : Base
    {
        public override string Describe<T>(T value) => "Box " + value.ToString();
    }

    internal abstract class Visitor<TResult>
    {
        public abstract TResult VisitCircle(Circle c);
    }

    internal sealed class NameVisitor : Visitor<string>
    {
        public override string VisitCircle(Circle c) => "circle";
    }

    internal sealed class SizeVisitor : Visitor<int>
    {
        public override int VisitCircle(Circle c) => 3;
    }

    internal abstract class Shape
    {
        public abstract TResult Accept<TResult>(Visitor<TResult> visitor);
    }

    internal sealed class Circle : Shape
    {
        public override TResult Accept<TResult>(Visitor<TResult> visitor) => visitor.VisitCircle(this);
    }

    internal interface IGreeter
    {
        string Greet<T>(T value);
    }

    internal sealed class Greeter : IGreeter
    {
        public string Greet<T>(T value) => "hi " + value.ToString();
    }
}
