namespace OS.Boot.EH
{
    // First-chance / unhandled / FailFast hooks. Stock NativeAOT routes
    // these through ClasslibProvider callbacks; we provide minimal direct
    // function pointers. Hooks default к null (no-op) and may be set by
    // higher-level kernel code (e.g., a future CrashReporter).
    //
    // Invocation contract:
    //   NotifyFirstChance(ex) — called BEFORE search for handler. If hook
    //     throws, behavior undefined (probably recursive #GP). Don't throw.
    //   NotifyUnhandled(ex) — called when no catch matched. Hook may print
    //     diagnostics, log к serial, etc. Returns voidly; caller follows
    //     с FailFast.
    //   FailFast() — final escape. Default impl spins forever (kernel halts).
    //     Replaceable so tests can capture failure без halting QEMU.
    internal static unsafe class ExceptionHooks
    {
        public static delegate*<System.Exception, void> FirstChanceHandler;
        public static delegate*<System.Exception, void> UnhandledHandler;
        public static delegate*<void> FailFastHandler;

        public static void NotifyFirstChance(System.Exception ex)
        {
            var h = FirstChanceHandler;
            if (h != null && ex != null) h(ex);
        }

        public static void NotifyUnhandled(System.Exception ex)
        {
            var h = UnhandledHandler;
            if (h != null && ex != null) h(ex);
        }

        public static void FailFast()
        {
            var h = FailFastHandler;
            if (h != null) { h(); return; }

            // Was a silent `while (true) { }`. Two things were wrong with it,
            // and preemption turned both into a puzzle: it said nothing, so a
            // fatal error looked like a mysterious stall; and it spun as a
            // runnable thread instead of stopping, so the collector asked it
            // thousands of times to reach a safe point, never got one, and the
            // whole system waited on a thread that was already dead.
            OS.Hal.Console.WriteLine("[failfast] unhandled managed exception — no handler matched");
            OS.Kernel.Panic.Fail("FailFast: unhandled managed exception");
        }
    }
}
