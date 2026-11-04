using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Process
{
    /// <summary>
    /// Whose environment (step207): a process has its own
    /// (<see cref="AppProcess.Environment"/>) — a copy of its starter's, or
    /// what the start asked for; what the kernel starts by itself — the
    /// launcher, the shell at boot, the hosted runtime — gets <see cref="Boot"/>.
    /// The entries and the block are EnvironmentBlock's (std).
    /// </summary>
    internal static class ProcessEnvironment
    {
        internal const int MaxChars = EnvironmentBlock.MaxChars;

        /// <summary>The environment of what the kernel starts by itself; kept by CoreLib (EnvironmentStore).</summary>
        internal static string[] Boot
        {
            get => EnvironmentStore.GetAll();
            set => EnvironmentStore.SetAll(value);
        }

        /// <summary>The environment of <paramref name="process"/>; the boot one for kernel code.</summary>
        internal static string[] Of(AppProcess process) => process?.Environment ?? Boot;

        internal static string Get(string[] environment, string name) => EnvironmentBlock.Get(environment, name);

        internal static unsafe int Encode(string[] environment, char* into, int capacity)
            => EnvironmentBlock.Encode(environment, into, capacity);

        internal static unsafe string[] Decode(char* block, int length) => EnvironmentBlock.Decode(block, length);
    }
}
