// ExecutionContext — BCL's surface (System.Private.CoreLib, Threading/
// ExecutionContext.cs) for code that captures a context and runs a callback
// in it (System.IO.Pipelines, step224).
//
// SharpOS cut: there is nothing to flow — no AsyncLocal<T>, no culture or
// security state per logical call. Capture hands back one shared context,
// Run invokes the callback on the calling thread, SuppressFlow only counts.

namespace System.Threading
{
    public delegate void ContextCallback(object? state);

    public sealed class ExecutionContext : IDisposable
    {
        private static ExecutionContext s_default = null!;
        private static int s_suppressed;

        private ExecutionContext() { }

        private static ExecutionContext Default => s_default ??= new ExecutionContext();

        public static ExecutionContext? Capture() => s_suppressed > 0 ? null : Default;

        public static bool IsFlowSuppressed() => s_suppressed > 0;

        public static AsyncFlowControl SuppressFlow()
        {
            Interlocked.Increment(ref s_suppressed);
            return default;
        }

        public static void RestoreFlow()
        {
            if (Interlocked.Decrement(ref s_suppressed) < 0)
            {
                Interlocked.Increment(ref s_suppressed);
                throw new InvalidOperationException("Cannot restore context flow when it is not suppressed.");
            }
        }

        public static void Run(ExecutionContext executionContext, ContextCallback callback, object? state)
        {
            ArgumentNullException.ThrowIfNull(callback);
            callback(state);
        }

        public static void Restore(ExecutionContext executionContext) => ArgumentNullException.ThrowIfNull(executionContext);

        public ExecutionContext CreateCopy() => this;

        public void Dispose() { }
    }

    public struct AsyncFlowControl : IDisposable
    {
        public void Undo() => ExecutionContext.RestoreFlow();

        public void Dispose() => Undo();
    }
}
