using System;

namespace SharpOS.Std.NoRuntime
{
    /// <summary>
    /// The kernel's own code reads and sets the boot environment — the one
    /// the kernel gives what it starts by itself (step207). It has no working
    /// directory: "\".
    /// </summary>
    public static class EnvironmentStore
    {
        public static string[] GetAll() => global::OS.Kernel.Process.ProcessEnvironment.Boot;

        public static void SetAll(string[] environment) => global::OS.Kernel.Process.ProcessEnvironment.Boot = environment;

        public static string GetDirectory() => "\\";

        public static void SetDirectory(string path)
            => throw new PlatformNotSupportedException("kernel code has no working directory");
    }
}
