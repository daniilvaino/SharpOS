// System.Environment, the app tier's part: Exit, through the kernel's exit
// service (AppHost.Exit) — the process ends with that code, as the BCL's.

using SharpOS.AppSdk;

namespace System
{
    public static partial class Environment
    {
        /// <summary>Terminates this process and returns <paramref name="exitCode"/> to the kernel.</summary>
        public static void Exit(int exitCode)
        {
            AppHost.Exit(AppRuntime.Finish(exitCode));

            // The service is missing (an old kernel) or returned: Exit does not return in the BCL either.
            throw new PlatformNotSupportedException("Environment.Exit: the kernel offers no exit service.");
        }
    }
}
