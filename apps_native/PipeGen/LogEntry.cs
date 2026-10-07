using SharpOS.Std.Pipes;

namespace PipeApps
{
    /// <summary>
    /// The record PIPEGEN writes and PIPEFILT filters (step194). The same file
    /// in both: a pipe's two ends agree on a type by its name and layout, and
    /// PIPECNT, which has no such class, reads it through views.
    /// </summary>
    [Message]
    public sealed class LogEntry
    {
        public int Level;
        public string Text;
    }
}
