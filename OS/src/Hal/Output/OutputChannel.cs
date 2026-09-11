namespace OS.Hal
{
    /// <summary>
    /// Where a piece of output comes from.
    /// </summary>
    /// <remarks>
    /// A channel says nothing about where the output goes — that is
    /// <see cref="OutputRouting"/>'s job. Keeping the two apart is the point:
    /// before this every character went to the screen, the UART and the disk
    /// log alike, so an application printing a line paid for a port write per
    /// byte and a sector write per line, and a benchmark measured its own
    /// output. With the source known, each can be sent only where it is read.
    ///
    /// Passed explicitly down the write path rather than held in a "current
    /// channel" static. Output is serialised per call, not per scope, so a
    /// static set around a whole application write would be read by any other
    /// thread's line landing in between — and tag it with the wrong source.
    /// </remarks>
    internal enum OutputChannel : byte
    {
        /// <summary>The kernel's own log: Console, Log, UiText.</summary>
        Kernel = 0,

        /// <summary>The panic path. Must reach the machine's operator whatever the routing says.</summary>
        Panic,

        /// <summary>Verbose diagnostics — [seh-step], [prof], [thunk] and friends.</summary>
        Trace,

        /// <summary>A native PE application's ordinary output.</summary>
        AppOut,

        /// <summary>A native PE application's error output.</summary>
        AppErr,

        /// <summary>The hosted runtime writing to its standard output handle.</summary>
        HostedOut,

        /// <summary>The hosted runtime writing to its standard error handle.</summary>
        HostedErr,

        /// <summary>Frames of a full-screen interface: escape sequences, not messages.</summary>
        Ui,

        /// <summary>Measurements — <c>[perf] name=value</c> lines for benchmarks.</summary>
        Perf,
    }

    /// <summary>
    /// The physical places output can land.
    /// </summary>
    [System.Flags]
    internal enum OutputSink : byte
    {
        None = 0,

        /// <summary>The terminal engine and framebuffer (UEFI ConOut before ExitBootServices).</summary>
        Screen = 1 << 0,

        /// <summary>COM1 — the kernel log the host tees into last_build.log.</summary>
        Com1 = 1 << 1,

        /// <summary>COM3 — program output, on machines that have the port.</summary>
        Com3 = 1 << 2,

        /// <summary>COM4 — programs' error streams, on machines that have the port.</summary>
        Com4 = 1 << 3,

        /// <summary>The file on the ESP that survives a machine with no serial port.</summary>
        DiskLog = 1 << 4,
    }

    /// <summary>
    /// Which sinks each channel reaches.
    /// </summary>
    /// <remarks>
    /// The kernel's own channels go to COM1, which is what the host records
    /// as the boot log and what the tools parse. What programs print goes to
    /// COM3 instead, when there is one: a PowerShell session, the census or an
    /// emulator's status lines are a program's output, and interleaved with the
    /// kernel's they made both harder to read and the kernel's slower to
    /// write. Where there is no COM3 they stay on COM1, as before — a machine
    /// with one serial port keeps a complete log on it.
    ///
    /// A program's error stream goes to COM4, apart from its ordinary output
    /// as it would be with <c>2&gt;err.txt</c>: the screen still shows both in
    /// order, the logs let a failure be found without reading everything
    /// around it. Without COM4 it falls back to wherever the program's output
    /// goes, so nothing is lost on a machine with fewer ports.
    ///
    /// The disk log still takes everything but frames: on real hardware it is
    /// the only record there is, and splitting it needs one file per channel,
    /// which is a later step. Full-screen frames stay screen-only (step165).
    ///
    /// Only the channel decides, never the caller: code does not pick a port,
    /// it says whose output this is.
    ///
    /// A switch rather than a table: a static array would need a class
    /// constructor to fill it, and those do not run here (limits §1).
    /// </remarks>
    internal static class OutputRouting
    {
        private const OutputSink KernelLog = OutputSink.Screen | OutputSink.Com1 | OutputSink.DiskLog;

        public static OutputSink SinksFor(OutputChannel channel)
        {
            switch (channel)
            {
                case OutputChannel.Ui:
                    return OutputSink.Screen;

                case OutputChannel.AppOut:
                case OutputChannel.HostedOut:
                    return OutputSink.Screen | ProgramPort | OutputSink.DiskLog;

                case OutputChannel.AppErr:
                case OutputChannel.HostedErr:
                    return OutputSink.Screen | ErrorPort | OutputSink.DiskLog;

                // Measurements are for the tools that compare runs, not for
                // whoever is watching the screen — and drawing them would be one
                // more cost inside the thing being measured.
                case OutputChannel.Perf:
                    return OutputSink.Com1 | OutputSink.DiskLog;

                default:
                    return KernelLog;
            }
        }

        private static OutputSink ProgramPort
            => Serial.Com3Present ? OutputSink.Com3 : OutputSink.Com1;

        private static OutputSink ErrorPort
            => Serial.Com4Present ? OutputSink.Com4 : ProgramPort;
    }
}
