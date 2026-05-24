using OS.Hal;
using OS.Kernel.Memory;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Diagnostics
{
    // Step 110 Part 8 smoke — KernelGC.CollectPrecise end-to-end test.
    //
    // Holds a sentinel managed object live across the Collect call. If the
    // precise walker discovers it (slot enumeration via NativeAOT GcInfo
    // + frame-by-frame UNWIND), MarkFromRoot will mark it and the
    // subsequent sweep will keep it alive. We verify by reading the
    // sentinel's MT byte AFTER Collect — if Sweep had reclaimed it the
    // memory would now be a FreeMarker MT instead of String MT.
    //
    // Also asserts that LastFramesWalked > 0 and LastRootsMarked > 0 — i.e.
    // the walker actually did something rather than silently bailing.
    //
    // We temporarily flip ReclamationDisabled to false for this single
    // collect, since that's the whole point of precise walking — to make
    // sweep actually run. Restored afterwards so the rest of the boot path
    // is unaffected by this experiment.
    internal static unsafe class KernelGcPreciseSmokeProbe
    {
        private static string s_sentinel;
        private static string s_keepAlive;
        private static char* s_pinnedChars;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- kernel GC precise smoke begin ----");

            if (!KernelGcPreciseWalk.IsAvailable)
            {
                Log.Write(LogLevel.Warn, "precise walker not available (GcContextSpill not initialized)");
                return;
            }

            // Build a sentinel string with a distinctive payload so we
            // can spot-check it survived.
            s_sentinel = new string('Z', 7);

            // Capture the sentinel's MT pointer (first 8 bytes of the
            // object) before Collect. If sweep correctly skipped it,
            // post-Collect MT must equal this.
            fixed (char* p = s_sentinel)
            {
                s_pinnedChars = p;
                byte* objStart = (byte*)p - 12;   // String layout: MT(8) + Length(4) + chars
                ulong mtBefore = *(ulong*)objStart;

                Log.Begin(LogLevel.Info);
                Console.Write("[gcprec] sentinel obj=0x"); Console.WriteHex((ulong)objStart);
                Console.Write(" MT=0x"); Console.WriteHex(mtBefore);
                Log.EndLine();

                // Flip ReclamationDisabled so sweep actually runs. Restore
                // before returning — keep production path untouched.
                bool wasDisabled = GC.ReclamationDisabled;
                GC.ReclamationDisabled = false;
                try
                {
                    KernelGC.CollectPrecise();
                }
                finally
                {
                    GC.ReclamationDisabled = wasDisabled;
                }

                ulong mtAfter = *(ulong*)objStart;

                Log.Begin(LogLevel.Info);
                Console.Write("[gcprec] post-Collect MT=0x"); Console.WriteHex(mtAfter);
                Console.Write(" framesWalked="); Console.WriteInt(KernelGcPreciseWalk.LastFramesWalked);
                Console.Write(" rootsMarked="); Console.WriteInt(KernelGcPreciseWalk.LastRootsMarked);
                Console.Write(" unresolved="); Console.WriteInt(KernelGcPreciseWalk.LastFramesUnresolved);
                Log.EndLine();

                bool survived = mtBefore == mtAfter && mtAfter != 0;
                bool walkerRan = KernelGcPreciseWalk.LastFramesWalked > 0;
                bool foundRoots = KernelGcPreciseWalk.LastRootsMarked > 0;

                Log.Begin(LogLevel.Info);
                Console.Write("[gcprec] verdict: sentinel ");
                Console.Write(survived ? "SURVIVED" : "RECLAIMED");
                Console.Write(", walker ");
                Console.Write(walkerRan ? "ran" : "skipped");
                Console.Write(", roots ");
                Console.Write(foundRoots ? "found" : "missed");
                Console.Write(" — ");
                Console.Write((survived && walkerRan && foundRoots) ? "PASS" : "FAIL");
                Log.EndLine();
            }

            // Keep the sentinel reachable from a known static for any
            // post-mortem inspection.
            s_keepAlive = s_sentinel;

            Log.Write(LogLevel.Info, "---- kernel GC precise smoke end ----");
        }
    }
}
