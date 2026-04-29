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
            while (true) { }
        }
    }
}
