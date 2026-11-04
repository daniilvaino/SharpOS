using System;

namespace SharpOS.Std.NoRuntime
{
    /// <summary>
    /// The kernel's own code reads and sets the boot environment — the one
    /// the kernel gives what it starts by itself (step207). It has no working
    /// directory: "/". Kept here, in CoreLib; the kernel's
    /// ProcessEnvironment.Boot reads and writes it.
    /// </summary>
    public static class EnvironmentStore
    {
        private static string[] s_boot;

        public static string[] GetAll() => s_boot ??= new string[0];

        public static void SetAll(string[] environment) => s_boot = environment ?? new string[0];

        public static string GetDirectory() => "/";

        public static void SetDirectory(string path)
            => throw new PlatformNotSupportedException("kernel code has no working directory");
    }
}
