using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Probe for the canonical `static readonly T x = new T();` pattern.
    // Verifies the full pipeline:
    //
    //   1. ILC's TypePreinit interpreter evaluates `new Box()` at compile time
    //      and emits a tagged GC statics descriptor cell in the binary's
    //      GCStaticRegion section (ReadyToRunSectionType 201).
    //   2. At boot, GcStaticsMaterializer walks this section: for each
    //      Uninitialized entry, allocates a GC object of the encoded EEType,
    //      bulk-copies the preInit blob into the object's raw data, replaces
    //      the tagged pointer with the object reference.
    //   3. `__GetGCStaticBase_*` helpers return real object references.
    //   4. Field reads work — `s_default.Value == 42` for any T pre-initialized.
    //
    // Before step 40-41 this pattern crashed with #GP (sentinel
    // 0xFFFF000000000010 from unmaterialized descriptor cell). With our
    // ClassConstructorRunner port + recursion-safe Check + materialization
    // pass, the canonical pattern now works exactly like in stock .NET.
    internal static class CctorProbe
    {
        private sealed class Box
        {
            public int Value;
            public Box() { Value = 42; }
        }

        // Canonical pattern — implicit cctor from field initializer.
        private static readonly Box s_default = new Box();
        private static readonly Box s_default2 = new Box();
        private static int _counter = 7;

        public static void Run()
        {
            int counter = _counter;
            ReportProbe("cctor implicit-int-field", counter == 7, (uint)counter);

            int v = s_default.Value;
            ReportProbe("cctor implicit-ref-field", v == 42, (uint)v);

            int v2 = s_default2.Value;
            ReportProbe("cctor second-ref-field", v2 == 42, (uint)v2);

            // Two reads — second hits "already initialized" fast path.
            int v3 = s_default.Value;
            ReportProbe("cctor ref-field repeat", v3 == 42, (uint)v3);
        }

        private static void ReportProbe(string label, bool ok, uint val)
        {
            Log.Begin(ok ? LogLevel.Info : LogLevel.Error);
            Console.Write(label);
            Console.Write(": ");
            Console.Write(ok ? "ok" : "FAIL");
            Console.Write(" val=");
            Console.WriteUInt(val);
            Log.EndLine();
        }
    }
}
