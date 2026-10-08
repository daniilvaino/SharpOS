using SharpOS.AppSdk;
using System;
using System.Collections.Generic;
using System.Runtime;
using SharpOS.Std.Exchange;
using SharpOS.Std.Exchange.Probe;

namespace AotTests
{
    // A standalone NativeAOT test-battery app (step138): a freestanding win-x64
    // PE that exercises the app-side std surface (GC alloc, arrays, strings,
    // List<T>, Dictionary<T>, EqualityComparer<T>) from an app -- the same way
    // the kernel's NativeAotProbe validates the kernel tier. Prints one line per
    // case and exits with the pass count, so a launcher / harness can read the
    // result. Deliberately uses NO static reference fields (ClassConstructorRunner
    // trap): all test data is local / factory.
    internal static unsafe partial class AppEntry
    {
        private static uint s_pass;
        private static uint s_total;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Main();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main()
        {
            string[] arguments = AppHost.Arguments;

            // `--gc-stress N K [mode...]`: the rest of the command line runs
            // with a collection before every N-th allocation in this app and
            // every K-th in the kernel (0: that side off), the heap walked
            // around each collection and freed blocks poisoned. A broken heap
            // ends the app (134) or the machine, saying where.
            if (arguments.Length > 2 && arguments[0] == "--gc-stress")
            {
                uint app = (uint)int.Parse(arguments[1]);
                uint kernel = (uint)int.Parse(arguments[2]);
                string[] rest = new string[arguments.Length - 3];
                for (int i = 0; i < rest.Length; i++) rest[i] = arguments[i + 3];

                ulong* before = stackalloc ulong[2];
                Probe(11, kernel | (1UL << 32), before);
                SharpOS.Std.NoRuntime.GcSweep.PoisonFreed = true;
                SharpOS.Std.NoRuntime.GcStress.VerifyHeap = true;
                SharpOS.Std.NoRuntime.GcStress.Every = app;
                s_gcStress = true;

                int result = Run(rest);

                SharpOS.Std.NoRuntime.GcStress.Every = 0;
                ulong* kernelRan = stackalloc ulong[2];
                Probe(11, before[1], kernelRan);
                AppHost.WriteString("[gcstress] app every " + app.ToString() + ": "
                                    + SharpOS.Std.NoRuntime.GcStress.Collections.ToString() + " collections; kernel every "
                                    + kernel.ToString() + ": " + (kernelRan[0] - before[0]).ToString() + " collections\n");
                return result;
            }

            return Run(arguments);
        }

        // Under --gc-stress: tests that measure time or count collections
        // say so instead of failing on numbers the mode changes on purpose.
        private static bool s_gcStress;

        // Either side collecting at every few allocations: waits on the
        // other side's work take far longer. The kernel can be stressed from
        // boot (Probes.GcStressEvery) without this app asking for it.
        private static bool s_kernelStressed;
        private static bool SlowRun => s_gcStress || s_kernelStressed;

        // `--concurrent`: another battery runs at the same time (step194 §7,
        // test 1). What talks to the kernel's probes — pipes by fixed names,
        // regions to and from the kernel — is left out, and the exit code is
        // the number of checks that failed: 0 is green.
        private static bool s_concurrent;

        private static int Run(string[] arguments)
        {
            s_pass = 0;
            s_total = 0;
            ulong* kernelEvery = stackalloc ulong[1];
            s_kernelStressed = Probe(13, 0, kernelEvery) == 0 && kernelEvery[0] != 0;

            // Startup data (pipe_plan.md "Подготовить под трубы", item 3):
            // `AOTTESTS.EXE --echo-args ...` prints what arrived and exits with
            // the count, so the shell's `expect N` checks the delivery.
            if (arguments.Length > 0 && arguments[0] == "--echo-args")
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    AppHost.WriteString("[args] ");
                    AppHost.WriteUInt((uint)i);
                    AppHost.WriteString(" '");
                    AppHost.WriteString(arguments[i]);
                    AppHost.WriteString("'\n");
                }

                // autorun.sh passes exactly these; anything else arrived damaged.
                if (arguments.Length == 3 && (arguments[1] != "two words" || arguments[2] != "третий"))
                    return 100;
                return arguments.Length;
            }

            // Interface calls on an untranslated region object, each in a run of
            // its own: one of them may end the program, and the battery must
            // not go with it. Exit 1 — an exception, caught here; 2 — the call
            // returned without a fault (the value is printed); 134 — the
            // kernel ended the program. The digit is how many types implement
            // the interface, which decides what ILC compiles:
            //   --untranslated-interface1..4  ISingle, IRanked, ITriple, ILabelled.
            // A pipe writer that ends with its queue full (pipe test 2): normal
            // exit (0) or an unhandled exception (134).
            if (arguments.Length > 0 && arguments[0] == "--dynamic")
                return RunDynamicOnly();

            if (arguments.Length > 1 && arguments[0] == "--pipe-stress")
                return RunPipeStress(int.Parse(arguments[1]));

            if (arguments.Length > 1 && arguments[0] == "--pipe-writer-dies")
                return RunWriterDies(arguments[1] == "crash");

            if (arguments.Length > 0 && arguments[0].Length == 25 && arguments[0].StartsWith("--untranslated-interface"))
                return RunUntranslatedInterface(arguments[0][24] - '0');

            s_concurrent = arguments.Length > 0 && arguments[0] == "--concurrent";

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
            // Under --gc-stress the allocation before it already collected.
            if (s_gcStress) AppHost.WriteString("[gcstress] reclaimed check not meaningful under stress\n");
            Check("collect reclaimed something",
                SharpOS.Std.NoRuntime.GcSweep.LastSweptCount > 0 || s_gcStress);

            CheckAllocatorShape();
            CheckHeapLookupCost();
            CheckAllocatorSizeClasses();
            CheckCounterReadsAreNotFolded();

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

            // SIMD.
            //
            // Whether ILC turned the vector types in our std into instructions.
            // Correctness alone cannot tell: a scalar fallback computes the same
            // answers. IsHardwareAccelerated is the signal, and the lanes below
            // are mixed on purpose — uniform data makes every lane agree, so a
            // swapped lane order or a half-formed mask would read as correct.
            Check("Vector128 accelerated", System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated);
            Check("Vector128 lane mask", SimdLaneMaskOk());

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

            CheckStelem();
            CheckArrayCasts();
            CheckNumberFormatting();
            CheckThreadsAndTasks();
            CheckPreemption();
            CheckCollectorPaths();
            CheckClockWrap();
            CheckClockAdvances();
            CheckStackTraceText();
            CheckStackTraceOwnership();
            if (!s_concurrent)
            {
                CheckRegion();
                CheckRegionFromKernel();
                CheckPipes();
            }
            CheckDynamic();

            // The error stream (step 167). The check can only see that the
            // kernel offers it; whether the marker reached last_err.log and not
            // last_app.log is read from the logs.
            Check("error stream published", AppHost.HasErrorStream);
            AppHost.WriteError("[stderr] aot marker\n");

            AppHost.WriteString("==== ");
            AppHost.WriteUInt(s_pass);
            AppHost.WriteString("/");
            AppHost.WriteUInt(s_total);
            AppHost.WriteString(" passed ====\n");

            // After the summary: a fault nobody catches stops the machine, and
            // the battery's results must already be in the log by then.
            if (HwFaultProbe != 0)
                RunHwFaultProbe();

            // Exit code = pass count (all-green => equals total); under
            // --concurrent the count of failures.
            return s_concurrent ? (int)(s_total - s_pass) : (int)s_pass;
        }

        // Preemption (pipe_plan.md, item 9). Each check fails rather than hangs
        // without it: the busy wait is bounded by the clock, and the waits that
        // follow block in the kernel, which lets the other thread run anyway.
        private static volatile int s_spinCount;
        private static volatile int s_stopSpin;
        private static volatile int s_allocErrors;
        private static volatile int s_throwErrors;

        private static void CheckPreemption()
        {
            // A thread that never yields, and a main thread that never yields
            // either: under cooperative scheduling the worker never runs.
            s_spinCount = 0;
            s_stopSpin = 0;
            var spinner = System.Threading.Tasks.Task.Run(() =>
            {
                while (s_stopSpin == 0)
                    s_spinCount++;
            });
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (s_spinCount == 0 && clock.ElapsedMilliseconds < 2000) { }
            bool workerRan = s_spinCount != 0;
            int seen = s_spinCount;
            long until = clock.ElapsedMilliseconds + 50;
            while (clock.ElapsedMilliseconds < until) { }
            bool workerKeptRunning = s_spinCount > seen;
            s_stopSpin = 1;
            spinner.Wait();
            Check("a busy thread does not starve another (preemption)", workerRan && workerKeptRunning);

            // Two threads allocating at once, with collections, each checking
            // that what it holds is still what it put there: the allocator and
            // the collector are critical sections against preemption.
            s_allocErrors = 0;
            var allocator = System.Threading.Tasks.Task.Run(() => AllocateAndVerify('w'));
            AllocateAndVerify('m');
            allocator.Wait();
            Check("two threads allocate and collect under preemption", s_allocErrors == 0);

            // Two threads throwing and catching at once: the exception chain
            // has to follow its thread, not stay with the CPU.
            s_throwErrors = 0;
            var thrower = System.Threading.Tasks.Task.Run(() => ThrowAndCatch("w"));
            ThrowAndCatch("m");
            thrower.Wait();
            Check("two threads throw and catch under preemption", s_throwErrors == 0);
        }

        private static void AllocateAndVerify(char tag)
        {
            object[] keep = new object[64];
            for (int i = 0; i < 20000; i++)
            {
                int[] numbers = new int[16];
                numbers[0] = i;
                numbers[15] = i;
                keep[i % 64] = numbers;
                keep[(i + 32) % 64] = (tag == 'w' ? "w" : "m") + i;

                if (keep[(i + 1) % 64] is int[] older && older[0] != older[15])
                    s_allocErrors++;
                if (keep[(i + 33) % 64] is string text && text[0] != tag)
                    s_allocErrors++;

                if (tag == 'm' && i % 2500 == 0)
                    GC.Collect();
            }
        }

        private static void ThrowAndCatch(string tag)
        {
            for (int i = 0; i < 2000; i++)
            {
                try
                {
                    throw new InvalidOperationException(tag);
                }
                catch (InvalidOperationException e)
                {
                    if (e.Message != tag)
                        s_throwErrors++;
                }
            }
        }

        // Array casts (pipe_plan.md, item 6). Through an opaque object, so ILC
        // cannot fold the answer at compile time and the runtime helper runs.
        private class CastBase { }
        private sealed class CastDerived : CastBase { }
        private enum CastTint { A, B }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object Opaque(object o) => o;

        private static void CheckArrayCasts()
        {
            object strings = Opaque(new string[1]);
            object ints = Opaque(new int[1]);
            // With a real CastDerived in it: an array of a type that is never
            // instantiated lets ILC prove no element type derives from CastBase
            // and compile `is CastBase[]` into an exact MethodTable compare,
            // which answered no without ever reaching the runtime.
            object derived = Opaque(new CastDerived[] { new CastDerived() });
            object tints = Opaque(new CastTint[1]);

            Check("string[] is object[] (covariance)", strings is object[]);
            Check("string[] is Array", strings is Array);
            Check("int[] is not ValueType", !(ints is ValueType));
            Check("int[] is uint[] (same-size integers)", ints is uint[]);
            Check("int[] is not long[]", !(ints is long[]));
            Check("enum[] is int[]", tints is int[]);
            Check("Derived[] is Base[]", derived is CastBase[]);
            Check("int[] is not object[]", !(ints is object[]));

            object[] covariant = (object[])Opaque(new string[1]);
            bool refMismatch = false;
            try
            {
                ref object slot = ref covariant[0];
                slot = null;
            }
            catch (ArrayTypeMismatchException) { refMismatch = true; }
            Check("ref into a covariant array throws", refMismatch);

            // BinaryWriter / BinaryReader in std (pipe_plan.md "Подготовить под
            // трубы", item 5): the format the pipes' file bridge will write.
            var stream = new System.IO.MemoryStream();
            var writer = new System.IO.BinaryWriter(stream);
            string longText = new string('ж', 300);
            writer.Write(-123456789);
            writer.Write(0x1122334455667788L);
            writer.Write(2.5);
            writer.Write(true);
            writer.Write("строка");
            writer.Write(longText);
            writer.Write7BitEncodedInt(300);
            stream.Position = 0;
            var reader = new System.IO.BinaryReader(stream);
            bool roundTrip = reader.ReadInt32() == -123456789
                             && reader.ReadInt64() == 0x1122334455667788L
                             && reader.ReadDouble() == 2.5
                             && reader.ReadBoolean()
                             && reader.ReadString() == "строка"
                             && reader.ReadString() == longText
                             && reader.Read7BitEncodedInt() == 300;
            Check("BinaryWriter -> BinaryReader round trip", roundTrip);

            // A length prefix of two gigabytes over a few bytes of data.
            var forged = new System.IO.MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07, (byte)'x' });
            bool forgedRefused = false;
            try { new System.IO.BinaryReader(forged).ReadString(); }
            catch (System.IO.EndOfStreamException) { forgedRefused = true; }
            Check("BinaryReader refuses a forged string length", forgedRefused);

            // The shared generator project reaches every image (pipe_plan.md
            // "Подготовить под трубы", item 4).
            Check("shared generator ran for this image", SharpOS.Generated.ImageInfo.AssemblyName == "AotTests");

            // Interpolation through DefaultInterpolatedStringHandler: a span
            // hole compiles only through the handler.
            int hexValue = 255;
            int negative = -5;
            ReadOnlySpan<char> holeSpan = "xyz".AsSpan(1);
            Check("interpolation (format, alignment, span holes)",
                  $"{hexValue:X}|{negative,4}|[{holeSpan}]" == "FF|  -5|[yz]");

            // File writes (step197): what is written reads back; a name the
            // FAT writer cannot store is refused, not dropped. A file of this
            // process's own: PROCTEST runs two of these at once.
            bool fileRoundTrip = false;
            try
            {
                string mine = "\\aot" + SharpOS.AppSdk.Process.CurrentId.ToString() + ".txt";
                System.IO.File.WriteAllText(mine, "x1");
                System.IO.File.AppendAllText(mine, "y2");
                fileRoundTrip = System.Text.Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(mine)) == "x1y2";
            }
            catch (System.IO.IOException) { }
            Check("File.WriteAllText + AppendAllText read back", fileRoundTrip);

            bool streamRefused = false;
            try { new System.IO.FileStream("\\aottests*bad.bin", System.IO.FileMode.Create, System.IO.FileAccess.Write); }
            catch (System.IO.IOException) { streamRefused = true; }
            Check("FileStream for writing refuses a name FAT cannot hold", streamRefused);
        }

        // Reference-array stores (pipe_plan.md, item 1). The exceptions here
        // are created by this image's own copy of std, so their types are the
        // app's: a catch that misses them is an EH problem, not a type-identity
        // one. That is the contrast for the hardware-fault probe below.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object[] StelemObjects(int n) => n < 0 ? null : new object[n];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object[] StelemStrings(int n) => new string[n];

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int StelemIndex(int i) => i;

        // BCL number formatting (std-no-runtime/Number/). The kernel checks the
        // same engine in StdSurfaceProbe; this is the app tier's compile of it.
        // Expected strings are .NET's invariant-culture output.
        private static void CheckNumberFormatting()
        {
            Check("format int X / x4 / D5", 255.ToString("X") == "FF" && 255.ToString("x4") == "00ff" && 42.ToString("D5") == "00042");
            Check("format int N0 / negative D3", 1234567.ToString("N0") == "1,234,567" && (-5).ToString("D3") == "-005");
            Check("format long.MinValue / ulong X", long.MinValue.ToString() == "-9223372036854775808" && ulong.MaxValue.ToString("X") == "FFFFFFFFFFFFFFFF");
            Check("format double shortest round-trip", 0.1.ToString() == "0.1" && (1.0 / 3).ToString() == "0.3333333333333333" && 1e20.ToString() == "1E+20");
            Check("format double F2 / E3 / N2", 3.14159.ToString("F2") == "3.14" && 123.456.ToString("E3") == "1.235E+002" && 1234.5.ToString("N2") == "1,234.50");
            Check("format float shortest", 0.1f.ToString() == "0.1" && float.MaxValue.ToString() == "3.4028235E+38");
            Check("format NaN / Infinity", double.NaN.ToString() == "NaN" && double.PositiveInfinity.ToString() == "Infinity");
            Check("string.Format {0:X8}|{1,6:F1}|{2:N2}", string.Format("{0:X8}|{1,6:F1}|{2:N2}", 48879, 2.26, 1234.5) == "0000BEEF|   2.3|1,234.50");
            Check("format B / B8 (binary)", 255.ToString("B") == "11111111" && 5.ToString("B8") == "00000101");

            Span<byte> u8 = stackalloc byte[32];
            bool utf8 = (-1234.5).TryFormat(u8, out int written, "N1", null) && written == 8
                        && u8[0] == (byte)'-' && u8[2] == (byte)',' && u8[6] == (byte)'.' && u8[7] == (byte)'5';
            Check("format UTF-8 TryFormat (IUtf8SpanFormattable)", utf8);
        }

        private static void CheckStelem()
        {
            object[] arr = StelemObjects(3);

            bool pastEnd = false;
            try { arr[StelemIndex(3)] = "x"; }
            catch (IndexOutOfRangeException) { pastEnd = true; }
            Check("stelem past end throws", pastEnd);

            bool negative = false;
            try { arr[StelemIndex(-1)] = "x"; }
            catch (IndexOutOfRangeException) { negative = true; }
            Check("stelem negative index throws", negative);

            object[] none = StelemObjects(-1);
            bool nullArray = false;
            try { none[StelemIndex(0)] = "x"; }
            catch (NullReferenceException) { nullArray = true; }
            Check("stelem into null array throws", nullArray);

            arr[StelemIndex(2)] = "in";
            Check("stelem in range", (string)arr[2] == "in" && arr[0] == null);

            object[] strings = StelemStrings(1);
            bool mismatch = false;
            try { strings[StelemIndex(0)] = (object)42; }
            catch (ArrayTypeMismatchException) { mismatch = true; }
            Check("stelem wrong element type throws", mismatch && strings[0] == null);

            strings[StelemIndex(0)] = "same type";
            Check("stelem matching element type stored", (string)strings[0] == "same type");
        }

        // Regions (pipe_plan.md "Проверить опытом"). The app builds the probe
        // graph straight into an exchange block, touches it untranslated, and
        // hands it to the kernel, which prints it by the schema, translates it
        // in place and reads it while its collector runs under load (the
        // kernel's own lines are "[region] …").
        private static void CheckRegion()
        {
            Check("region: kernel takes regions", AppHost.HasRegionToKernel);
            if (!AppHost.HasRegionToKernel)
                return;

            RegionProbeGraph.Declare();
            Check("region: graph types declared", RegionProbeGraph.Problems == 0);

            object graph = RegionProbeGraph.Build(RegionProbeGraph.Numbers);
            Region.Plan plan = Region.Lay(graph, out string complaint);
            Check("region: graph laid out", plan != null);
            if (plan == null)
            {
                AppHost.WriteString("[region] " + complaint + "\n");
                return;
            }

            byte* block = (byte*)AppHost.ExchangeAllocate(plan.Size);
            Check("region: exchange block", block != null);
            if (block == null)
                return;
            Region.Write(plan, block);
            AppHost.WriteString("[region] " + plan.Count.ToString() + " objects, " + plan.Size.ToString() + " bytes\n");

            CheckUntranslated((ulong)block + Region.HeaderSize);

            byte[] schema = RegionSchema.Build(TypeKeys.Declared);
            int failures;
            fixed (byte* schemaBytes = schema)
                failures = AppHost.RegionToKernel(block, plan.Size, schemaBytes, (ulong)schema.Length);
            Check("region: the kernel received, printed, translated and read it under load", failures == 0);
            Check("region: the block is the kernel's now", !AppHost.ExchangeFree(block));
        }

        // The other direction (pipe_plan.md "Проверить опытом", 1): the kernel
        // writes the graph into exchange blocks this run owns; the app prints
        // it by the schema, translates it in place, and reads it while two of
        // its threads allocate and a third runs its collector.
        private static volatile int s_regionStop;
        private static volatile int s_regionRunning;
        private static volatile int s_regionAllocations;
        private static volatile int s_regionCorruptions;
        private static volatile int s_regionCollections;

        private static void CheckRegionFromKernel()
        {
            int given = AppHost.RegionFromKernel(out byte* region, out ulong length, out byte* schema, out ulong schemaLength);
            Check("region from kernel: two exchange blocks of ours", given == 0 && region != null && schema != null);
            if (given != 0 || region == null || schema == null)
                return;

            byte[] schemaBytes = new byte[(int)schemaLength];
            for (int i = 0; i < schemaBytes.Length; i++)
                schemaBytes[i] = schema[i];
            int printed = RegionSchema.Print(region, length, schemaBytes,
                line => AppHost.WriteString("[region-print] " + line + "\n"), out string printComplaint);
            Check("region from kernel: printed by the schema alone", printed > 0);

            RegionProbeGraph.Declare();
            bool resolved = Region.Resolve(region, length, out object root, out string complaint);
            if (!resolved)
                AppHost.WriteString("[region] resolve refused: " + complaint + "\n");
            Check("region from kernel: translated in place", resolved);
            if (!resolved)
                return;
            Check("region from kernel: graph checks out",
                  RegionProbeGraph.Check(root, RegionProbeGraph.Numbers,
                      line => AppHost.WriteString("[region] " + line + "\n")) == 0);

            ulong before = RegionChecksum(region, length);
            object own = RegionProbeGraph.Build(RegionProbeGraph.Numbers);
            uint collectionsBefore = AppGC.Collections;

            s_regionStop = 0;
            s_regionRunning = 3;
            s_regionAllocations = 0;
            s_regionCorruptions = 0;
            s_regionCollections = 0;
            var ring0 = new object[64];
            var ring1 = new object[64];
            System.Threading.Tasks.Task.Run(() => RegionAllocate(ring0, 0x11));
            System.Threading.Tasks.Task.Run(() => RegionAllocate(ring1, 0x77));
            System.Threading.Tasks.Task.Run(() =>
            {
                while (s_regionStop == 0)
                {
                    GC.Collect();
                    s_regionCollections++;
                    System.Threading.Thread.Sleep(1);
                }
                System.Threading.Interlocked.Decrement(ref s_regionRunning);
            });

            var watch = System.Diagnostics.Stopwatch.StartNew();
            int reads = 0;
            int readFailures = 0;
            // The workers can take longer than the second to start at all —
            // under --gc-stress, and whenever the kernel collects meanwhile (it
            // collects before growing since step194): read on until they have.
            while (watch.ElapsedMilliseconds < 1000
                   || ((s_regionAllocations == 0 || s_regionCollections == 0)
                       && watch.ElapsedMilliseconds < (SlowRun ? 60000 : 10000)))
            {
                readFailures += RegionProbeGraph.Check(root, RegionProbeGraph.Numbers, null);
                reads++;
            }
            s_regionStop = 1;
            var wait = System.Diagnostics.Stopwatch.StartNew();
            // A collection per allocation makes each of the workers' last
            // iterations take seconds, not microseconds.
            long waitLimit = SlowRun ? 60000 : 2000;
            while (s_regionRunning > 0 && wait.ElapsedMilliseconds < waitLimit)
                System.Threading.Thread.Sleep(1);

            AppHost.WriteString("[region] app load: " + reads.ToString() + " reads, "
                                + s_regionAllocations.ToString() + " allocations, "
                                + (AppGC.Collections - collectionsBefore).ToString() + " collections" + "\n");
            Check("region from kernel: reader, allocators and collector all ran",
                  s_regionRunning == 0 && reads > 0 && s_regionAllocations > 0 && s_regionCollections > 0
                  && AppGC.Collections > collectionsBefore);
            Check("region from kernel: every read checked out, bytes unchanged",
                  readFailures == 0 && RegionChecksum(region, length) == before);
            Check("region from kernel: the app's own objects intact",
                  s_regionCorruptions == 0 && RegionProbeGraph.Check(own, RegionProbeGraph.Numbers, null) == 0);
            Check("region from kernel: both blocks given back", AppHost.ExchangeFree(region) && AppHost.ExchangeFree(schema));
        }

        private static void RegionAllocate(object[] ring, byte seed)
        {
            int n = 0;
            while (s_regionStop == 0)
            {
                int slot = n % ring.Length;
                if (ring[slot] is byte[] old)
                {
                    for (int i = 1; i < old.Length; i++)
                        if (old[i] != (byte)(old[0] ^ i)) { s_regionCorruptions++; break; }
                }
                byte[] block = new byte[40];
                block[0] = (byte)(seed + n);
                for (int i = 1; i < block.Length; i++) block[i] = (byte)(block[0] ^ i);
                ring[slot] = block;
                string garbage = new string('g', 8 + n % 24);
                if (garbage.Length == 0) s_regionCorruptions++;
                n++;
                s_regionAllocations++;
            }
            System.Threading.Interlocked.Decrement(ref s_regionRunning);
        }

        private static ulong RegionChecksum(byte* p, ulong length)
        {
            ulong h = 0xCBF29CE484222325UL;
            for (ulong i = 0; i < length; i++)
            {
                h ^= p[i];
                h *= 0x100000001B3UL;
            }
            return h;
        }

        // An object of the region before translation: its table word holds an
        // odd type key. Each access that reads the table must end in an
        // exception this app catches, and the app must carry on (pipe_plan.md
        // "Проверить опытом", 3). A cast ILC reduced to a pointer compare
        // answers "no" without reading anything — also fine; "yes" is not.
        private static void CheckUntranslated(ulong objectAt)
        {
            object untranslated = System.Runtime.CompilerServices.Unsafe.As<ulong, object>(ref objectAt);

            string how = "returned";
            try { VirtualOnUntranslated(untranslated); }
            catch (Exception e) { how = ExceptionName(e); }
            AppHost.WriteString("[untranslated] virtual call: " + how + "\n");
            Check("untranslated: virtual call throws, caught here", how != "returned");

            how = "no fault";
            bool answer = false;
            try { answer = CastOnUntranslated(untranslated); }
            catch (Exception e) { how = ExceptionName(e); }
            AppHost.WriteString("[untranslated] cast to a class: " + how + (how == "no fault" ? (answer ? ", yes" : ", no") : "") + "\n");
            Check("untranslated: cast throws or answers no", how != "no fault" || !answer);

            // The key-form object is still in a local: the app's collector
            // must pass it by (it is outside every segment), and a wait after
            // the catches must still return.
            GC.Collect();
            System.Threading.Thread.Sleep(20);
            Check("untranslated: collect and sleep after the catches", AppGC.LastWalkOk && untranslated != null);
        }

        private static int RunUntranslatedInterface(int implementations)
        {
            if (implementations < 1 || implementations > 4)
                return 4;
            RegionProbeGraph.Declare();
            object graph = RegionProbeGraph.Build(4);
            Region.Plan plan = Region.Lay(graph, out _);
            byte* block = plan == null ? null : (byte*)AppHost.ExchangeAllocate(plan.Size);
            if (block == null)
                return 3;
            Region.Write(plan, block);
            ulong objectAt = (ulong)block + Region.HeaderSize;
            object untranslated = System.Runtime.CompilerServices.Unsafe.As<ulong, object>(ref objectAt);

            int result = 2;
            string how;
            try
            {
                int value = implementations == 1 ? SingleOnUntranslated(untranslated)
                          : implementations == 2 ? DevirtualizedOnUntranslated(untranslated)
                          : implementations == 3 ? TripleOnUntranslated(untranslated)
                          : InterfaceOnUntranslated(untranslated);
                how = "returned " + value.ToString();
            }
            catch (Exception e)
            {
                how = ExceptionName(e);
                result = 1;
            }
            AppHost.WriteString("[untranslated] interface call, " + implementations.ToString()
                                + " implementation(s): " + how + "\n");
            return result;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int SingleOnUntranslated(object o)
            => System.Runtime.CompilerServices.Unsafe.As<SharpOS.Std.Exchange.Probe.ISingle>(o).Single();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int TripleOnUntranslated(object o)
            => System.Runtime.CompilerServices.Unsafe.As<SharpOS.Std.Exchange.Probe.ITriple>(o).Triple();

        private static string ExceptionName(Exception e)
            => e is AccessViolationException ? "AccessViolationException"
             : e is NullReferenceException ? "NullReferenceException"
             : "another exception";

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int VirtualOnUntranslated(object o)
            => System.Runtime.CompilerServices.Unsafe.As<SharpOS.Std.Exchange.Probe.Base>(o).Rank();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool CastOnUntranslated(object o) => o is SharpOS.Std.Exchange.Probe.Base;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int InterfaceOnUntranslated(object o)
            => System.Runtime.CompilerServices.Unsafe.As<SharpOS.Std.Exchange.Probe.ILabelled>(o).Label();

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int DevirtualizedOnUntranslated(object o)
            => System.Runtime.CompilerServices.Unsafe.As<SharpOS.Std.Exchange.Probe.IRanked>(o).Sides();

        // Hardware fault in app code (pipe_plan.md, item 3).
        //   0 — off;
        //   1 — fault inside try { } catch (Exception) { } catch { };
        //   2 — fault with no handler at all;
        //   3 — a managed throw with no handler (the app's own exception type).
        // Each mode ends this app, so one mode per run. The address is a pipe
        // region's type key: odd, non-canonical, so the CPU raises #GP and the
        // kernel asks this app's factory for the AccessViolationException, so
        // the type is the app's own (pipe_plan.md, items 3 and 4a). Expected:
        // mode 1 — "caught by catch (Exception)" and the sleep after it
        // returning; modes 2 and 3 — "[app] unhandled exception: app ended,
        // exit code 134" in the kernel log and the launcher carrying on.
        private const int HwFaultProbe = 0;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong FaultAddress() => 0xfbfbbc32146b4e5bUL;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong ReadAt(ulong address) => *(ulong*)address;

        private static void RunHwFaultProbe()
        {
            AppHost.WriteString("[hwfault] mode ");
            AppHost.WriteUInt((uint)HwFaultProbe);
            AppHost.WriteString(HwFaultProbe == 3 ? ": throwing with no handler\n" : ": reading a non-canonical address\n");

            if (HwFaultProbe == 3)
                throw new InvalidOperationException("unhandled on purpose (HwFaultProbe = 3)");

            if (HwFaultProbe == 2)
            {
                ulong value = ReadAt(FaultAddress());
                AppHost.WriteString("[hwfault] read returned, no fault: ");
                AppHost.WriteUInt((uint)value);
                AppHost.WriteString("\n");
                return;
            }

            int how = 0;
            try
            {
                ulong value = ReadAt(FaultAddress());
                AppHost.WriteString("[hwfault] read returned, no fault: ");
                AppHost.WriteUInt((uint)value);
                AppHost.WriteString("\n");
            }
            catch (Exception)
            {
                how = 1;
            }
            catch
            {
                how = 2;
            }

            // No virtual call on the caught object: if it is the kernel's, its
            // slots are not ours.
            AppHost.WriteString(how == 1 ? "[hwfault] caught by catch (Exception)\n"
                              : how == 2 ? "[hwfault] caught by catch-all only\n"
                              : "[hwfault] not caught, execution continued\n");

            // After a caught fault the code must still be able to wait: a
            // handler left with interrupts off never sees the timer again
            // (pipe_plan.md, item 5).
            System.Threading.Thread.Sleep(50);
            AppHost.WriteString("[hwfault] a sleep after the catch returned\n");
        }

        // Threads and tasks in an app (ABI V3).
        //
        // Terminal.Gui keeps input decoding and the resize watch on background
        // loops, so a task there is a thread and nothing more. Until this
        // passes, that library cannot run here at all — an inline Task.Run
        // would enter a loop that never returns.
        private static volatile int s_threadRan;

        // The clock must survive its own counter wrapping.
        //
        // This laptop's HPET reports 64bit=no: a 32-bit main counter at
        // 14.318 MHz, which comes back to zero every 300 seconds. Every
        // "deadline = now + delta" in the system is then set past a value the
        // counter can no longer reach, and the wait never ends — the machine
        // stops after ~5 minutes with every thread Waiting and nothing able to
        // wake them. Five hundred and ninety five identical thread dumps over
        // fourteen hours said exactly that, and nothing else did: the code is
        // healthy, the arithmetic is not.
        //
        // Driven by hand rather than by waiting: `s_counterAddress` is where
        // Stopwatch reads its ticks from, so pointing it at a local and
        // stepping that local reproduces a wrap in microseconds instead of
        // five minutes, and does it identically on QEMU, which has a 64-bit
        // HPET and can never show the bug on its own.
        // Can a counter read either side of an allocation be trusted?
        //
        // AllocCount goes up on every single allocation, so both differences
        // below must be positive. They are measured identically and differ in
        // one thing: the first reads through a getter marked NoInlining, the
        // second through one that is not.
        //
        // ILC models an allocation as something that does not write user
        // statics — true of every allocator but ours, which is written in the
        // same language, in the same image, and keeps these very counters. So
        // the unguarded pair can be folded into one read, and its difference
        // comes out zero while the allocation plainly happened.
        //
        // Printed rather than asserted for the unguarded half: it is a
        // property of the compiler, not a defect to fix, and a red check
        // would say the opposite. The guarded half IS asserted — that one is
        // ours to keep working, and three wrong diagnoses in step178 came
        // from its absence.
        // The allocation must be one that cannot be removed. The first
        // version of this used `new object()` checked only for null, and both
        // differences came out zero — which said nothing, because ILC is free
        // to delete an allocation whose result never escapes and never
        // matters. Storing into a static escapes it beyond any analysis, and
        // reading the contents back makes the store matter.
        private static byte[] s_foldSinkA;
        private static byte[] s_foldSinkB;

        private static unsafe void CheckCounterReadsAreNotFolded()
        {
            ulong guardedBefore = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
            s_foldSinkA = new byte[64];
            s_foldSinkA[0] = 0xA5;
            ulong guardedAfter = SharpOS.Std.NoRuntime.GcHeap.AllocCount;

            ulong plainBefore = SharpOS.Std.NoRuntime.GcHeap.AllocCountFoldable;
            s_foldSinkB = new byte[64];
            s_foldSinkB[0] = 0x5A;
            ulong plainAfter = SharpOS.Std.NoRuntime.GcHeap.AllocCountFoldable;

            ulong guarded = guardedAfter - guardedBefore;
            ulong plain = plainAfter - plainBefore;

            AppHost.WriteString("[folding] guarded=");
            AppHost.WriteUInt((uint)guarded);
            AppHost.WriteString(" plain=");
            AppHost.WriteUInt((uint)plain);
            AppHost.WriteString("\n");

            Check("a guarded counter read is taken again after an allocation",
                  guarded >= 1UL
                  && s_foldSinkA[0] == 0xA5 && s_foldSinkB[0] == 0x5A);
        }

        // A fragmented heap, which joining neighbours cannot help with.
        //
        // Coalescing fixes the case where everything died together. It does
        // nothing when live objects sit between the dead ones: the free
        // blocks are genuinely separate, genuinely small, and genuinely many.
        // First-fit down that list costs the whole list to answer "none of
        // these holds four kilobytes" — and answering no is the expensive
        // case, because it is the one that walks to the end.
        //
        // Size classes make the answer free: the request skips every class
        // that cannot hold it and takes the head of the first that can.
        private static unsafe void CheckAllocatorSizeClasses()
        {
            const int Pairs = 512;

            AllocateInterleavedGarbage(Pairs, 64);
            GC.Collect();

            SharpOS.Std.NoRuntime.GcHeap.GetFreeStats(
                out ulong freeBytes, out uint blocks, out uint largest);

            AppHost.WriteString("[allocclass] free=");
            AppHost.WriteUInt((uint)freeBytes);
            AppHost.WriteString(" blocks=");
            AppHost.WriteUInt(blocks);
            AppHost.WriteString(" largest=");
            AppHost.WriteUInt(largest);
            AppHost.WriteString("\n");

            // The point of the interleaving: many small free blocks, which is
            // what makes the next check mean something. If they all merged
            // anyway, the check below would pass for the wrong reason.
            // Under --gc-stress the garbage is collected at the next
            // allocation and its blocks reused: the precondition never forms.
            if (s_gcStress) AppHost.WriteString("[gcstress] small-blocks check not meaningful under stress\n");
            Check("live objects between dead ones leave many small blocks",
                  blocks >= 64 || s_gcStress);

            // Nothing between the snapshot and the request, so the deltas
            // belong to the array and to nothing else.
            //
            // That this measures anything at all depends on the counter
            // getters being NoInlining. ILC models an allocation as something
            // that does not write user statics — true of every allocation but
            // ours, which IS the allocator keeping these counters — so two
            // plain reads either side of `new byte[4096]` fold into one and
            // the difference comes out zero whatever happened. Three wrong
            // diagnoses came from that before the cause was found.
            ulong probesBefore = SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount;
            ulong reuseBefore = SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount;
            uint maskBefore = SharpOS.Std.NoRuntime.GcHeap.NonEmptyBucketMask();

            byte[] mid = new byte[4096];
            mid[0] = 7;
            mid[mid.Length - 1] = 9;

            ulong probes = SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount - probesBefore;
            ulong reused = SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount - reuseBefore;

            AppHost.WriteString("[allocclass] mask=");
            AppHost.WriteHex(maskBefore);
            AppHost.WriteString(" bucketOfLargest=");
            AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcHeap.BucketIndexOf(largest));
            AppHost.WriteString(" bucketOfRequest=");
            AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcHeap.BucketIndexOf(4128));
            AppHost.WriteString(" probes=");
            AppHost.WriteUInt((uint)probes);
            AppHost.WriteString(" reused=");
            AppHost.WriteUInt((uint)reused);
            AppHost.WriteString("\n");

            // The same request a second time, with nothing beside it at all.
            //
            // This is the shape that used to report zero while the free list
            // was serving the request perfectly: no call between the reads,
            // so the compiler folded them. It stays as a regression test for
            // the folding, not for the allocator — if the counter getters
            // ever lose NoInlining, this is what goes quietly wrong.
            // The mask and the node count FIRST: both are calls, and a call
            // between the counter reads and the request would break exactly
            // the folding this repeat exists to detect. Read here, they are
            // out of the way.
            uint maskBefore2 = SharpOS.Std.NoRuntime.GcHeap.NonEmptyBucketMask();
            uint nodesBefore2 = SharpOS.Std.NoRuntime.GcHeap.FreelistNodes;

            ulong probesBefore2 = SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount;
            ulong reuseBefore2 = SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount;
            byte[] again = new byte[4096];
            ulong probes2 = SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount - probesBefore2;
            ulong reused2 = SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount - reuseBefore2;

            AppHost.WriteString("[allocclass] repeat probes=");
            AppHost.WriteUInt((uint)probes2);
            AppHost.WriteString(" reused=");
            AppHost.WriteUInt((uint)reused2);
            AppHost.WriteString(" len=");
            AppHost.WriteUInt((uint)again.Length);
            AppHost.WriteString("\n");

            // The bucket layout either side of the printing between the two
            // requests. It answered the question it was added for: the mask
            // does not change, so no class is being drained and the splitting
            // policy is not at fault. Kept because "where the free memory is"
            // is the one thing the other numbers cannot say.
            AppHost.WriteString("[allocclass] mask before=");
            AppHost.WriteHex(maskBefore);
            AppHost.WriteString(" after=");
            AppHost.WriteHex(maskBefore2);
            AppHost.WriteString(" nodes=");
            AppHost.WriteUInt(nodesBefore2);
            AppHost.WriteString(" splits=");
            AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcHeap.FreelistSplitCount);
            AppHost.WriteString("\n");

            // And the three addresses. One table or three is a fact, not a
            // matter of opinion about how ILC expands Unsafe.AsPointer.
            AppHost.WriteString("[allocclass] heads alloc=");
            AppHost.WriteHex((ulong)SharpOS.Std.NoRuntime.GcHeap.HeadsAddrAlloc);
            AppHost.WriteString(" link=");
            AppHost.WriteHex((ulong)SharpOS.Std.NoRuntime.GcHeap.HeadsAddrLink);
            AppHost.WriteString(" stats=");
            AppHost.WriteHex((ulong)SharpOS.Std.NoRuntime.GcHeap.HeadsAddrStats);
            AppHost.WriteString("\n");

            Check("the bucket table is one table, not one per call site",
                  SharpOS.Std.NoRuntime.GcHeap.HeadsAddrAlloc
                      == SharpOS.Std.NoRuntime.GcHeap.HeadsAddrLink
                  && SharpOS.Std.NoRuntime.GcHeap.HeadsAddrLink
                      == SharpOS.Std.NoRuntime.GcHeap.HeadsAddrStats);

            // Both halves, and the first one is the one that was missing.
            // A cost check on its own passes when the work never happened:
            // zero blocks examined reads as "found it instantly" and as
            // "never looked" alike, and the first run of this check reported
            // zero while a 256 KiB block sat in the list unclaimed.
            Check("a large request is served from the free list, not by bumping",
                  reused == 1UL);
            Check("a request skips the classes that cannot hold it",
                  probes <= 64UL && mid[0] == 7 && mid[mid.Length - 1] == 9);

            // Nothing held on to: the array above is the only thing this
            // method leaves behind, and the next collection takes it.
            s_keepAlive = null;
        }

        // Every other block stays reachable, so the dead ones cannot merge
        // with their neighbours. Held in a static rather than a local: a
        // local would die with the frame and the whole run would coalesce.
        private static object[] s_keepAlive;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AllocateInterleavedGarbage(int pairs, int payload)
        {
            s_keepAlive = new object[pairs];
            for (int i = 0; i < pairs; i++)
            {
                byte[] dead = new byte[payload];
                dead[0] = 1;
                s_keepAlive[i] = new byte[payload];
            }
        }

        // What it costs to ask "is this address in the heap".
        //
        // The marker asks it twice about every candidate it pops — once for
        // the candidate, once for the MethodTable the candidate claims — and
        // nearly every answer is no: stack words, pointers into .rdata,
        // kernel structures. Each no used to cost a walk of the whole segment
        // list, and the comment above the walk said the linear search was
        // "OK for small segment count", which was true about the count and
        // false about the number of questions.
        private static unsafe void CheckHeapLookupCost()
        {
            byte[] inHeap = new byte[64];
            nint heapAddress = *(nint*)System.Runtime.CompilerServices.Unsafe
                .AsPointer(ref inHeap);

            // A fixed low address rather than a stack one: the stack is in
            // fact outside the heap, but "outside" is what the test is
            // supposed to be asserting, not assuming. Page one is below every
            // segment on any tier.
            nint definitelyOutside = (nint)0x1000;

            ulong before = SharpOS.Std.NoRuntime.GcHeap.SegmentScanSteps;
            nuint sink = (nuint)SharpOS.Std.NoRuntime.GcHeap
                .FindSegmentContaining(definitelyOutside);
            Check("an address outside the heap is rejected without a scan",
                  SharpOS.Std.NoRuntime.GcHeap.SegmentScanSteps == before && sink == 0);

            // Warm the cache first. The first lookup of an address in a
            // different segment than the previous one pays the walk, and that
            // one walk is not what is being measured here.
            sink += (nuint)SharpOS.Std.NoRuntime.GcHeap.FindSegmentContaining(heapAddress);

            // A thousand lookups of the same heap address. One step each is
            // the cache answering; more than that means the list is being
            // walked again for a question already answered.
            before = SharpOS.Std.NoRuntime.GcHeap.SegmentScanSteps;
            for (int i = 0; i < 1000; i++)
                sink += (nuint)SharpOS.Std.NoRuntime.GcHeap.FindSegmentContaining(heapAddress);
            ulong steps = SharpOS.Std.NoRuntime.GcHeap.SegmentScanSteps - before;

            AppHost.WriteString("[seglookup] steps=");
            AppHost.WriteUInt((uint)steps);
            AppHost.WriteString(" per=1000 segments=");
            AppHost.WriteUInt(SharpOS.Std.NoRuntime.GcHeap.SegmentCount);
            AppHost.WriteString("\n");

            Check("a heap address is found without rescanning the segment list",
                  steps <= 1000UL && sink != 0 && inHeap.Length == 64);
        }

        // The SHAPE of free memory, not the amount of it.
        //
        // The launcher died with 63 MB free and no room for a List<T> to
        // double. Sweep puts one free marker on every dead object and never
        // joins neighbours, so a heap that has been used and released comes
        // back as thousands of separate small pieces. "Free bytes" says
        // everything is fine; the only number that answers whether a doubling
        // array can be served is the size of the LARGEST piece.
        //
        // Placed before the threading checks on purpose: a pool thread
        // allocating in the middle of the churn would leave a live object
        // between the dead ones, and the run of free blocks would be split by
        // something that is nobody's bug.
        private static unsafe void CheckAllocatorShape()
        {
            const int Count = 256;
            const int Payload = 232;   // 256 bytes once the array header is on

            AllocateAdjacentGarbage(Count, Payload);
            GC.Collect();

            SharpOS.Std.NoRuntime.GcHeap.GetFreeStats(
                out ulong freeBytes, out uint blocks, out uint largest);

            // Printed as well as asserted: the numbers are the evidence, and
            // a log from before the sweep learned to join blocks can be held
            // next to one from after.
            AppHost.WriteString("[allocshape] free=");
            AppHost.WriteUInt((uint)freeBytes);
            AppHost.WriteString(" blocks=");
            AppHost.WriteUInt(blocks);
            AppHost.WriteString(" largest=");
            AppHost.WriteUInt(largest);
            AppHost.WriteString("\n");

            // 64 KiB went in; half of it in one piece is a generous floor that
            // still cannot be met by a heap that never joins anything.
            Check("sweep joins adjacent free blocks", largest >= 32u * 1024u);
            Check("free memory is in few pieces, not thousands",
                  freeBytes == 0 || blocks <= Count / 4);

            // A large request must come out of what was just freed. Counting
            // segments would not prove it — the current segment has room to
            // bump into — so the question asked is whether the FREELIST served
            // it.
            //
            // The state right before the request is printed rather than
            // inferred: the numbers above are separated from the allocation by
            // service calls and checks, and the first run of this test failed
            // while they said a 256 KiB block was there.
            SharpOS.Std.NoRuntime.GcHeap.GetFreeStats(
                out ulong preFree, out uint preBlocks, out uint preLargest);
            AppHost.WriteString("[allocshape] pre-big free=");
            AppHost.WriteUInt((uint)preFree);
            AppHost.WriteString(" blocks=");
            AppHost.WriteUInt(preBlocks);
            AppHost.WriteString(" largest=");
            AppHost.WriteUInt(preLargest);
            AppHost.WriteString("\n");

            // Snapshotted LAST, with nothing between them and the request.
            // Taken any earlier, an allocation made by the printing above
            // would raise the counter and the check would pass on somebody
            // else's reuse while the array came from a bump. A test that can
            // pass for the wrong reason answers nothing.
            ulong reuseBefore = SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount;
            ulong probesBefore = SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount;

            byte[] big = new byte[48 * 1024];
            big[0] = 1;
            big[big.Length - 1] = 2;

            AppHost.WriteString("[allocshape] post-big reuse=");
            AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount);
            AppHost.WriteString(" probes=");
            AppHost.WriteUInt((uint)SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount);
            AppHost.WriteString(" segments=");
            AppHost.WriteUInt(SharpOS.Std.NoRuntime.GcHeap.SegmentCount);
            AppHost.WriteString("\n");

            Check("a large request is served from freed memory",
                  SharpOS.Std.NoRuntime.GcHeap.FreelistReuseCount > reuseBefore
                  && big[0] == 1 && big[big.Length - 1] == 2);

            // Cost, as a count of blocks examined rather than as time: the
            // same number on a loaded host and an idle one. First-fit down a
            // list where nothing is big enough costs the whole list and
            // answers "no".
            //
            // Meaningful only next to the check above, which establishes that
            // the free list served the request at all. On its own a budget is
            // met most easily by not doing the work.
            Check("finding a block does not walk the whole heap",
                  SharpOS.Std.NoRuntime.GcHeap.FreelistProbeCount - probesBefore <= 64UL);
        }

        // In its own frame, and returning nothing: locals of the caller can
        // stay live in a slot the precise walker still reports, and then the
        // garbage this makes would not be garbage at all.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void AllocateAdjacentGarbage(int count, int payload)
        {
            object[] hold = new object[count];
            for (int i = 0; i < count; i++)
                hold[i] = new byte[payload];
            if (hold[count - 1] == null) s_allocatorSink++;
        }

        private static int s_allocatorSink;

        // Who owns the buffer the trace is written into, and what it says
        // when it runs out.
        //
        // Ownership is the one that bites: applications share the KERNEL's EH
        // engine, so the code appending frames is kernel code even for an
        // application's exception. While the buffer was allocated there, it
        // sat in the kernel heap under an object in the application heap -
        // reachable from neither collector, and free for the kernel's sweep
        // to reclaim while the application still pointed at it.
        private static unsafe void CheckStackTraceOwnership()
        {
            Exception caught = null;
            try { TraceLevel1(); } catch (Exception e) { caught = e; }

            IntPtr[] buffer = caught == null ? null : caught.StackTraceBuffer;
            Check("trace buffer exists", buffer != null);
            if (buffer != null)
            {
                nint address = *(nint*)System.Runtime.CompilerServices.Unsafe
                    .AsPointer(ref buffer);
                Check("trace buffer lives in the app heap",
                      SharpOS.Std.NoRuntime.GcHeap.FindSegmentContaining(address) != null);
            }

            Exception deep = null;
            try { DeepThrow(80); } catch (Exception e) { deep = e; }
            string deepTrace = deep == null ? null : deep.StackTrace;
            Check("a cut-off trace says it was cut off",
                  deep != null && deep.DroppedStackFrames > 0 &&
                  deepTrace != null && deepTrace.Contains("more frames"));

            int inner = 0, outer = 0;
            try
            {
                try { TraceLevel1(); }
                catch (Exception e) { inner = e.GetStackIPs().Length; throw; }
            }
            catch (Exception e) { outer = e.GetStackIPs().Length; }
            Check("rethrow appends frames rather than stopping",
                  inner > 0 && outer > inner);
        }

        // The addition after the recursive call keeps each level a real
        // frame: a tail call would collapse the depth this is measuring.
        private static int s_deepSink;

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void DeepThrow(int depth)
        {
            if (depth == 0) throw new InvalidOperationException("deep-trace");
            DeepThrow(depth - 1);
            s_deepSink += depth;
        }

        // The stack trace as text, on this side of the boundary.
        //
        // An application catches, reports and dies by the same Exception type
        // the kernel uses - one file in std, compiled into both. Until
        // step177 its StackTrace was the literal "[trace]", so an app that
        // printed a trace printed seven characters, and the launcher's own
        // crash reports said nothing about where.
        //
        // No image base here: the resolver is a kernel hook and an app has no
        // copy of it, so the lines carry addresses alone. Apps load at a fixed
        // base, which makes that a symbolizer's problem rather than a reader's.
        private static void CheckStackTraceText()
        {
            string trace = null;
            int recorded = 0;
            try
            {
                TraceLevel1();
            }
            catch (Exception e)
            {
                trace = e.StackTrace;
                recorded = e.GetStackIPs().Length;
            }

            Check("stack trace is text, not a marker",
                  trace != null && trace.Length > 32);
            Check("stack trace lines name a frame each",
                  trace != null && trace.Contains("   at 0x"));

            int lines = 0;
            if (trace != null)
                for (int i = 0; i < trace.Length; i++)
                    if (trace[i] == '\n') lines++;
            Check("every recorded frame produced a line",
                  recorded > 0 && lines == recorded);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TraceLevel1() => TraceLevel2();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TraceLevel2() => TraceLevel3();

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void TraceLevel3()
            => throw new InvalidOperationException("trace-text");

        /// <summary>That DateTime.UtcNow moves, and moves at the right rate.</summary>
        /// <remarks>
        /// The battery had a clock test before this one and it did not cover
        /// this: CheckClockWrap exercises Stopwatch, with a counter of its own
        /// making, and never touches DateTime. So a DateTime whose source was
        /// never installed - UtcNow answering the start of the epoch on every
        /// call, for the whole life of the tree - passed every test there was.
        ///
        /// What it cost: Terminal.Gui times its timeouts by comparing UtcNow
        /// against a deadline computed from UtcNow, so with a clock that never
        /// moves no timeout in any program here ever fired. Silently - the
        /// callback is simply not called.
        ///
        /// The rate is checked, not just the direction. A source installed with
        /// the wrong scale still advances, and "it moves" would pass while every
        /// deadline in the system came out a hundred times too long.
        /// </remarks>
        private static void CheckClockAdvances()
        {
            Check("DateTime has a time source", System.DateTime.Clock.IsRealClock);

            System.DateTime first = System.DateTime.UtcNow;
            AppThreads.Sleep(20);
            System.DateTime second = System.DateTime.UtcNow;

            Check("DateTime.UtcNow advances", second.Ticks > first.Ticks);

            // Generous at the top because a sleep here is quantised by the
            // timer tick and the thread may wait behind others; tight enough
            // at the bottom to catch a scale that is off by any factor.
            long elapsedMs = (second.Ticks - first.Ticks) / System.TimeSpan.TicksPerMillisecond;
            Check("DateTime.UtcNow advances at about a millisecond per millisecond",
                  elapsedMs >= 10 && elapsedMs < 2000);
        }

        private static unsafe void CheckClockWrap()
        {
            ulong savedAddress = System.Diagnostics.Stopwatch.s_counterAddress;
            ulong savedLatch = System.Diagnostics.Stopwatch.s_latchAddress;
            bool savedNarrow = System.Diagnostics.Stopwatch.s_counterIsNarrow;

            ulong counter = 0, latch = 0;
            System.Diagnostics.Stopwatch.s_counterAddress = (ulong)(nuint)(&counter);
            System.Diagnostics.Stopwatch.s_latchAddress = (ulong)(nuint)(&latch);
            System.Diagnostics.Stopwatch.s_counterIsNarrow = true;
            try
            {
                // 256 ticks short of the 32-bit boundary, then 256 past it:
                // 512 ticks of elapsed time across the wrap. Reading the
                // counter as a plain 64-bit value answers 4294966784 ticks in
                // the other direction, which is the whole bug in one line.
                counter = 0xFFFFFF00UL;
                latch = 0xFFFFFF00UL;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                counter = 0x00000100UL;
                sw.Stop();

                Check("stopwatch survives a 32-bit counter wrap", sw.ElapsedTicks == 512);
                Check("elapsed across a wrap is never negative", sw.ElapsedTicks >= 0);

                // Three wraps in a row must accumulate, not reset: an epoch
                // counted once is worse than one never counted, because the
                // error is silent and permanent.
                //
                // The epoch moves between steps rather than at the end,
                // because that is how it really moves — the kernel refreshes
                // it on its timer tick, and an app that reads the clock twice
                // across a wrap relies on a tick having happened in between.
                // Steps are under half the range for the same reason the real
                // refresh runs at 15 Hz: a longer step is genuinely ambiguous,
                // indistinguishable from a step backwards.
                counter = 0x10000000UL;
                latch = 0x10000000UL;
                var multi = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 12; i++)
                {
                    counter = (counter + 0x40000000UL) & 0xFFFFFFFFUL;
                    latch = System.Diagnostics.Stopwatch.ReadCounter();
                }
                multi.Stop();
                Check("wraps accumulate rather than reset",
                      multi.ElapsedTicks == (long)(12UL * 0x40000000UL));

                // A reading that lands slightly BEHIND the epoch is a step
                // back, not a wrap. It happens whenever the tick refreshes
                // between an app's read of the epoch and its read of the
                // counter; calling it a wrap would answer with a timestamp
                // 300 s in the future and hang every deadline built on it.
                counter = 0x20000000UL;
                latch = 0x20000040UL;
                Check("a reading behind the epoch is not a wrap",
                      System.Diagnostics.Stopwatch.ReadCounter() == 0x20000000UL);
            }
            finally
            {
                System.Diagnostics.Stopwatch.s_counterAddress = savedAddress;
                System.Diagnostics.Stopwatch.s_latchAddress = savedLatch;
                System.Diagnostics.Stopwatch.s_counterIsNarrow = savedNarrow;
            }
        }

        private static void CheckThreadsAndTasks()
        {
            Check("threads available (abi v3)", SharpOS.AppSdk.AppThreads.IsAvailable);
            if (!SharpOS.AppSdk.AppThreads.IsAvailable) return;

            Check("task backend installs", SharpOS.AppSdk.TaskBackendInstaller.Install());

            // The body must run somewhere else: if Task.Run were inline, the
            // sleep below would happen before Wait was ever reached and the
            // check would pass for the wrong reason. Sleeping inside the task
            // and reading the flag before Wait is what tells them apart.
            s_threadRan = 0;
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                SharpOS.AppSdk.AppThreads.Sleep(20);
                s_threadRan = 1;
            });

            bool startedElsewhere = s_threadRan == 0;

            // Waits the way a caller actually would. This is the shape that
            // hung: the waiter enters Wait before the worker finishes, so it
            // has to be woken by work completing on another thread. Polling
            // first would let the flag be observed before Wait ever ran, and
            // prove nothing about the case that failed.
            task.Wait();

            Check("task ran on its own thread", startedElsewhere && s_threadRan == 1);
            Check("task wait returns", task.IsCompleted);

            // async/await end to end. The stage counter only advances past 1
            // if the state machine resumed after an await, which is the half
            // that compiling cannot prove.
            AsyncShape.Stage = 0;
            var asyncTask = AsyncShape.RunAsync();

            // Bounded, unlike an ordinary Wait: a state machine that never
            // resumes leaves this task unfinished forever, and waiting properly
            // on it takes the whole battery down with it, which is exactly how
            // this failed the first time, and silently.
            for (int waited = 0; waited < 500 && !asyncTask.IsCompleted; waited++)
                AppThreads.Sleep(10);
            Check("async method starts", AsyncShape.Stage >= 1);
            Check("await resumes", AsyncShape.Stage >= 2);
            Check("async runs to completion", AsyncShape.Stage == 3);
            Check("async task completes", asyncTask.IsCompleted);

            // Locks. Compare-and-swap has to be a real instruction, threads
            // need distinct ids, and `lock` has to keep two of them from losing
            // an update — each fails silently on its own.
            // Dictionary enumeration follows insertion.
            //
            // Not a nicety: code written against the BCL relies on it without
            // saying so, and our old storage (a chain per bucket) returned hash
            // order. Terminal.Gui renders a tree's roots straight from a
            // Dictionary, so a sorted list of folders came back shuffled.
            var ordered = new Dictionary<string, int>();
            ordered["first"] = 1;
            ordered["second"] = 2;
            ordered["third"] = 3;
            ordered["fourth"] = 4;

            string seen = "";
            foreach (var pair in ordered) seen = seen + pair.Key + ",";
            Check("dictionary keeps insertion order", seen == "first,second,third,fourth,");

            ordered.Remove("second");
            ordered["fifth"] = 5;
            seen = "";
            foreach (var pair in ordered) seen = seen + pair.Key + ",";
            Check("dictionary survives remove+add", ordered.Count == 4 && seen.Length > 0);
            Check("removed key is gone", !ordered.ContainsKey("second"));
            Check("re-added key is found", ordered["fifth"] == 5);

            // ToString(). Both of these returned null until step163, and null
            // from ToString is the worst kind of wrong: it reads as a no-op at
            // the call site and faults far away, where something indexes it.
            string sample = "text";
            Check("string.ToString() is itself", sample.ToString() == "text");
            Check("object.ToString() is not null", new object().ToString() != null);

            Check("compare-and-swap is atomic", System.Threading.Interlocked.IsAtomic);
            Check("thread ids are per-thread", System.Threading.ManagedThreadIds.IsPerThread);

            int mainId = System.Threading.ManagedThreadIds.Current;
            s_otherId = 0;
            var idTask = System.Threading.Tasks.Task.Run(
                () => { s_otherId = System.Threading.ManagedThreadIds.Current; });
            idTask.Wait();
            Check("threads have distinct ids", s_otherId != 0 && s_otherId != mainId);

            s_gate = new object();
            s_guarded = 0;
            var h1 = System.Threading.Tasks.Task.Run(Hammer);
            var h2 = System.Threading.Tasks.Task.Run(Hammer);
            h1.Wait();
            h2.Wait();
            Check("lock keeps updates", s_guarded == HammerIterations * 2);

            // Blocking waits (step174). Each of these used to poll with
            // Sleep(1); now the waiter sleeps in the kernel until the word it
            // waits on changes. An event set from another thread has to wake
            // its waiter, a timed wait has to give up, and more tasks than the
            // kernel once had room to queue (32) all have to run.
            Check("waits block in the kernel", AppThreads.CanWaitOnAddress);

            var ready = new System.Threading.ManualResetEventSlim();
            var setter = System.Threading.Tasks.Task.Run(() =>
            {
                AppThreads.Sleep(5);
                ready.Set();
            });
            ready.Wait();
            setter.Wait();
            Check("event set on another thread wakes its waiter", ready.IsSet);

            var never = new System.Threading.ManualResetEventSlim();
            Check("event timed wait times out", !never.Wait(20));

            s_counted = 0;
            var many = new System.Threading.Tasks.Task[100];
            for (int i = 0; i < many.Length; i++)
                many[i] = System.Threading.Tasks.Task.Run(() => System.Threading.Interlocked.Increment(ref s_counted));
            for (int i = 0; i < many.Length; i++)
                many[i].Wait();
            Check("100 tasks at once all run", s_counted == 100);

            // A collection has to see every thread's stack, not only the
            // collecting one's. The holder keeps an array in a local and
            // nowhere else, then sleeps; this thread collects and allocates
            // arrays of the same size with a different pattern, which lands
            // them on whatever the collection freed. A missed root shows up as
            // the holder's pattern overwritten.
            s_holderState = 0;
            s_holderVerdict = 0;
            var holder = System.Threading.Tasks.Task.Run(HoldOnStack);
            for (int waited = 0; waited < 500 && s_holderState == 0; waited++)
                AppThreads.Sleep(1);

            GC.Collect();

            int reused = 0;
            for (int i = 0; i < 256; i++)
            {
                byte[] other = new byte[HeldBytes];
                for (int k = 0; k < other.Length; k++) other[k] = 0xA5;
                if (other[HeldBytes - 1] == 0xA5) reused++;
            }

            s_holderState = 2;
            holder.Wait();
            Check("holder thread parked and reported", s_holderVerdict != 0 && reused == 256);
            Check("other thread's stack roots survive collect", s_holderVerdict == 1);
        }

        private const int HeldBytes = 64;
        private static volatile int s_holderState;
        private static volatile int s_holderVerdict;

        private static void HoldOnStack()
        {
            byte[] mine = new byte[HeldBytes];
            for (int i = 0; i < mine.Length; i++) mine[i] = 0x5A;

            s_holderState = 1;
            for (int waited = 0; waited < 2000 && s_holderState == 1; waited++)
                AppThreads.Sleep(1);

            bool intact = mine.Length == HeldBytes;
            for (int i = 0; i < HeldBytes && intact; i++)
                intact = mine[i] == 0x5A;
            s_holderVerdict = intact ? 1 : 2;
        }

        private static int s_counted;

        private const int HammerIterations = 5000;
        private static object s_gate = null!;
        private static volatile int s_guarded;
        private static volatile int s_otherId;

        private static void Hammer()
        {
            for (int i = 0; i < HammerIterations; i++)
            {
                lock (s_gate)
                {
                    int current = s_guarded;
                    s_guarded = current + 1;
                }
            }
        }

        private static unsafe bool SimdLaneMaskOk()
        {
            char* text = stackalloc char[8];
            text[0] = 'a'; text[1] = 'b'; text[2] = '<'; text[3] = 'd';
            text[4] = 'e'; text[5] = '&'; text[6] = 'g'; text[7] = 'h';

            var data = System.Runtime.CompilerServices.Unsafe
                .ReadUnaligned<System.Runtime.Intrinsics.Vector128<ushort>>(text);

            var hits = System.Runtime.Intrinsics.Vector128.Equals(
                           data, System.Runtime.Intrinsics.Vector128.Create((ushort)'<'))
                     | System.Runtime.Intrinsics.Vector128.Equals(
                           data, System.Runtime.Intrinsics.Vector128.Create((ushort)'&'));

            ushort* lanes = stackalloc ushort[8];
            System.Runtime.CompilerServices.Unsafe.WriteUnaligned(lanes, hits);

            uint mask = 0;
            for (int i = 0; i < 8; i++)
            {
                if (lanes[i] != 0) mask |= 1u << i;
            }

            return mask == ((1u << 2) | (1u << 5));
        }

        private static void Check(string name, bool ok)
        {
            s_total++;
            if (ok) s_pass++;
            AppHost.WriteString(ok ? "  ok   " : "  FAIL ");
            AppHost.WriteString(name);
            AppHost.WriteString("\n");

            // A failure goes to the error stream as well, so the error log is
            // the list of what broke — while the output keeps every line in order.
            if (!ok)
                AppHost.WriteError("[aot] FAIL " + name + "\n");
        }
    }
}
