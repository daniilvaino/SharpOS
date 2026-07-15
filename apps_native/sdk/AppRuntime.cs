namespace SharpOS.AppSdk
{
    internal static unsafe class AppRuntime
    {
        private static AppStartupBlock* s_startup;
        private static AppServiceTable* s_services;

        public static void Initialize(AppStartupBlock* startup)
        {
            s_startup = startup;
            if (startup == null)
            {
                s_services = null;
                return;
            }

            s_services = (AppServiceTable*)startup->ServiceTableAddress;

            // Bring up the managed GC heap before any `new string` or `new object()`
            // hits its RhNewString / RhpNewFast export. GcMemorySource backing is
            // GcAppPool (1 MB in .bss), provided via GcMemorySource.AppStatic.cs.
            SharpOS.Std.NoRuntime.GcHeap.Init();
        }

        public static AppStartupBlock* Startup => s_startup;

        public static AppServiceTable* Services => s_services;

        public static bool IsInitialized => s_startup != null && s_services != null;
    }
}
