using SharpOS.AppSdk;
using System;
using System.Collections.Generic;
using System.Runtime;

namespace AotTests
{
    // A standalone NativeAOT test-battery app (step138): a freestanding win-x64
    // PE that exercises the app-side std surface (GC alloc, arrays, strings,
    // List<T>, Dictionary<T>, EqualityComparer<T>) from an app -- the same way
    // the kernel's NativeAotProbe validates the kernel tier. Prints one line per
    // case and exits with the pass count, so a launcher / harness can read the
    // result. Deliberately uses NO static reference fields (ClassConstructorRunner
    // trap): all test data is local / factory.
    internal static unsafe class AppEntry
    {
        private static uint s_pass;
        private static uint s_total;

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

        private static int Run()
        {
            s_pass = 0;
            s_total = 0;

            AppHost.WriteString("==== AOT app test battery ====\n");

            // GC allocation.
            Check("new object()", new object() != null);
            int[] arr = new int[5];
            arr[2] = 42;
            Check("new int[5] + index", arr.Length == 5 && arr[2] == 42);
            char[] chars = new char[] { 'S', 'h', 'a', 'r', 'p' };
            string s = new string(chars);
            Check("new string(char[])", s.Length == 5 && s[0] == 'S');

            // Strings.
            Check("string concat", ("a" + "b" + "c") == "abc");
            Check("string equality", "Sharp" == s);
            Check("string PadRight", "hi".PadRight(4).Length == 4);

            // List<T>.
            var list = new List<int>();
            for (int i = 1; i <= 5; i++) list.Add(i * 10);
            Check("List<int> add/count", list.Count == 5);
            Check("List<int> indexer", list[3] == 40);
            Check("List<int> Contains", list.Contains(30) && !list.Contains(99));
            int[] listArr = list.ToArray();
            Check("List<int>.ToArray", listArr.Length == 5 && listArr[0] == 10);

            // Dictionary<K,V>.
            var dict = new Dictionary<int, int>();
            for (int i = 0; i < 8; i++) dict.Add(i, i * i);
            Check("Dictionary add/count", dict.Count == 8);
            bool got = dict.TryGetValue(7, out int sq);
            Check("Dictionary TryGetValue", got && sq == 49);
            Check("Dictionary missing key", !dict.TryGetValue(99, out _));

            // EqualityComparer<T>.Default (interface dispatch on a value type).
            var cmp = EqualityComparer<int>.Default;
            Check("EqualityComparer<int>", cmp.Equals(5, 5) && !cmp.Equals(5, 6));

            // throw / catch through the shared kernel EH engine (step140):
            // app throw -> kernel RhpThrowEx -> DispatchEx walks app .pdata ->
            // app catch funclet.
            bool threw = false;
            try
            {
                throw new InvalidOperationException("boom");
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Check("throw/catch", threw);

            // try/finally runs the finally block (no throw).
            int fin = 0;
            try { fin = 1; } finally { fin += 10; }
            Check("try/finally", fin == 11);

            // finally runs during unwind; exception propagates to an outer catch.
            bool outerCaught = false; int finOnUnwind = 0;
            try
            {
                try { throw new InvalidOperationException("x"); }
                finally { finOnUnwind = 1; }
            }
            catch (InvalidOperationException) { outerCaught = true; }
            Check("finally-on-unwind", outerCaught && finOnUnwind == 1);

            // catch by a base type (throw derived, catch System.Exception).
            bool baseCaught = false;
            try { throw new InvalidOperationException("y"); }
            catch (Exception) { baseCaught = true; }
            Check("catch-by-base", baseCaught);

            // exception object survives into the catch; Message round-trips.
            string msg = null;
            try { throw new InvalidOperationException("hello"); }
            catch (InvalidOperationException e) { msg = e.Message; }
            Check("exception message", msg == "hello");

            // non-matching catch skipped, matching catch selected.
            int which = 0;
            try { throw new FormatException("z"); }
            catch (InvalidOperationException) { which = 1; }
            catch (FormatException) { which = 2; }
            Check("multi-catch select", which == 2);

            AppHost.WriteString("==== ");
            AppHost.WriteUInt(s_pass);
            AppHost.WriteString("/");
            AppHost.WriteUInt(s_total);
            AppHost.WriteString(" passed ====\n");

            // Exit code = pass count (all-green => equals total).
            return (int)s_pass;
        }

        private static void Check(string name, bool ok)
        {
            s_total++;
            if (ok) s_pass++;
            AppHost.WriteString(ok ? "  ok   " : "  FAIL ");
            AppHost.WriteString(name);
            AppHost.WriteString("\n");
        }
    }
}
