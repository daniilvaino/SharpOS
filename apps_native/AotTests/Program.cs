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

            // GC.
            //
            // The app has its OWN precise collector (AppGC): its own heap, its
            // own mark and sweep, borrowing only the kernel's stack-root walk.
            // These check both halves — that a collection actually runs, and
            // that it does not eat anything still reachable. A collector that
            // frees live objects passes every "did it run" test ever written,
            // so the survivor checks below are the ones that matter.
            Check("GC walker offered", AppGC.IsAvailable);
            GC.Collect();
            Check("GC.Collect ran", AppGC.Collections > 0 && AppGC.LastWalkOk);

            int[] survivor = new int[64];
            for (int i = 0; i < survivor.Length; i++) survivor[i] = i * 7;
            string survivorText = "keep-me";
            object identity = survivor;

            uint churnOk = 0;
            for (int i = 0; i < 4096; i++)
            {
                byte[] garbage = new byte[64];
                garbage[0] = (byte)i;
                if (garbage[0] == (byte)i) churnOk++;
            }
            Check("alloc churn 4096x64B", churnOk == 4096);

            bool survived = survivor.Length == 64;
            for (int i = 0; i < survivor.Length && survived; i++)
                survived = survivor[i] == i * 7;
            Check("live array survives churn", survived);
            Check("live string survives churn", survivorText == "keep-me");
            Check("reference identity kept", ReferenceEquals(identity, survivor));

            byte[] big = new byte[256 * 1024];
            big[big.Length - 1] = 0xAB;
            Check("256 KiB allocation", big.Length == 256 * 1024 && big[big.Length - 1] == 0xAB);

            // Collect with those locals live, then use them: this is where a
            // walk that misses the stack shows itself. Silence here would mean
            // the roots were found; corruption would mean they were not.
            GC.Collect();
            bool afterCollect = survivor[9] == 63 && survivorText == "keep-me"
                                && big[big.Length - 1] == 0xAB;
            Check("live data survives collect", afterCollect);
            // Fully qualified: a using for the std namespace would make plain
            // `GC` ambiguous against System.GC everywhere else in this file.
            Check("collect reclaimed something",
                SharpOS.Std.NoRuntime.GcSweep.LastSweptCount > 0);

            // Multidimensional arrays.
            //
            // A different allocation path from every array above: ILC turns
            // `new byte[2, 1024]` into a call to
            // Internal.Runtime.CompilerHelpers.ArrayHelpers, and the object it
            // builds carries a bounds block between the length and the
            // elements. Get the layout wrong and indexing writes outside the
            // object — which is why the corner element and the lengths are
            // both checked, not just that the allocation returned.
            //
            // Landed for the Fami emulator port, whose PPU keeps its nametable
            // and pattern tables as byte[2, N] fields.
            byte[,] grid = new byte[2, 1024];
            Check("byte[,] rank/lengths",
                grid.Rank == 2 && grid.GetLength(0) == 2 && grid.GetLength(1) == 1024);

            grid[0, 0] = 0x11;
            grid[1, 1023] = 0x22;
            grid[1, 0] = 0x33;
            Check("byte[,] corner elements",
                grid[0, 0] == 0x11 && grid[1, 1023] == 0x22 && grid[1, 0] == 0x33);

            bool gridZeroed = true;
            for (int i = 1; i < 1024 && gridZeroed; i++) gridZeroed = grid[0, i] == 0;
            Check("byte[,] zero-initialised", gridZeroed);

            int[,] numbers = new int[3, 4];
            for (int y = 0; y < 3; y++)
                for (int x = 0; x < 4; x++)
                    numbers[y, x] = (y * 10) + x;

            bool readBack = numbers.Length == 12;
            for (int y = 0; y < 3 && readBack; y++)
                for (int x = 0; x < 4 && readBack; x++)
                    readBack = numbers[y, x] == (y * 10) + x;
            Check("int[,] round-trip", readBack);

            // Jagged arrays go through the same helper by a different branch.
            int[][] jagged = new int[3][];
            for (int i = 0; i < jagged.Length; i++) jagged[i] = new int[i + 1];
            jagged[2][2] = 99;
            Check("jagged array", jagged[0].Length == 1 && jagged[2].Length == 3
                                  && jagged[2][2] == 99);

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
