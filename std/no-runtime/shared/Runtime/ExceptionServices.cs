// System.Runtime.ExceptionServices.ExceptionDispatchInfo — API shape from
// dotnet/runtime v8.0.27 (MIT).
//
// Cut vs original: captured-stack preservation. The real EDI snapshots the
// exception's stack trace / watson buckets at Capture() and splices them
// back on Throw() so the rethrow keeps the original origin. Our EH pipeline
// has no stack-splicing hook yet, so Throw() is a plain `throw` — the trace
// restarts at the rethrow site. Callers (catch → cleanup →
// ExceptionDispatchInfo.Throw(e)) keep working; only the reported origin
// differs.

namespace System.Runtime.ExceptionServices
{
    public sealed class ExceptionDispatchInfo
    {
        private readonly Exception _exception;

        private ExceptionDispatchInfo(Exception exception)
        {
            _exception = exception;
        }

        public static ExceptionDispatchInfo Capture(Exception source)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            return new ExceptionDispatchInfo(source);
        }

        public Exception SourceException => _exception;

        public void Throw() => throw _exception;

        public static void Throw(Exception source) => Capture(source).Throw();
    }
}
