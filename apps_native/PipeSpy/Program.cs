using System;
using System.Runtime;
using System.Runtime.CompilerServices;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // PIPESPY [label] — a stage that shows what a pipe carries (step197).
    // Every message goes on as it came, untranslated; the first three are
    // described on the screen: the address of the object that arrived, the
    // address of an object this program makes itself (its own heap, for
    // contrast), and the fields read where they lie. At the end: how many
    // objects this program allocated while the rest went through.
    //
    // Two of them in one pipeline print the same addresses:
    //
    //     PIPEGEN 1000 | PIPESPY a | PIPESPY b | PIPECNT
    //
    // one object crossing four programs, not a byte string parsed into new
    // objects at every step — which would put each copy in its reader's heap
    // and cost allocations per message.
    internal static unsafe class AppEntry
    {
        private const int Shown = 3;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(StressArgs.Apply(AppHost.Arguments));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static int Run(string[] args)
        {
            string label = Paint(Magenta, "[" + (args.Length > 0 ? args[0] : "spy") + "]") + " ";
            object mine = new object();
            Say(label + "an object of my own is at " + Paint(Blue, Hex(Unsafe.As<object, ulong>(ref mine))) + " (my heap)\n");

            long seen = 0;
            ulong before = 0;
            Pipe.Read().Where(v =>
            {
                seen++;
                if (seen <= Shown)
                    Say(label + "#" + seen.ToString() + " at " + Paint(Yellow, Hex(v.Address))
                        + ", read in place: " + v.ToScreenString() + "\n");
                if (seen == Shown) before = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
                return true;
            }).WriteTo();

            string after = seen > Shown
                ? Paint(Green, (SharpOS.Std.NoRuntime.GcHeap.AllocCount - before).ToString() + " objects") + " allocated here for the "
                  + (seen - Shown).ToString() + " after them"
                : "too few to count allocations";
            Say(label + seen.ToString() + " messages passed on; " + after + "\n");
            return 0;
        }

        // The addresses that should match are bright yellow, this program's own blue.
        private const string Magenta = "\u001b[35m", Blue = "\u001b[1;34m", Yellow = "\u001b[1;33m", Green = "\u001b[1;32m";

        private static string Paint(string sgr, string text) => sgr + text + "\u001b[0m";

        private static string Hex(ulong value) => "0x" + value.ToString("X");

        private static void Say(string text) => AppHost.WriteString(text);
    }
}
