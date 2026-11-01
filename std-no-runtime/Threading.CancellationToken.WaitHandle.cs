// CancellationToken.WaitHandle (step223, BlockingCollection's WaitAny): a
// ManualResetEvent the source sets when it cancels, made on first ask.
// A token without a source can never be canceled, so its handle never is set.

namespace System.Threading
{
    public readonly partial struct CancellationToken
    {
        public WaitHandle WaitHandle => _source == null ? new ManualResetEvent(false) : _source.WaitHandle;
    }

    public sealed partial class CancellationTokenSource
    {
        private ManualResetEvent? _waitHandle;

        public WaitHandle WaitHandle
        {
            get
            {
                ManualResetEvent? handle = _waitHandle;
                if (handle != null) return handle;
                handle = new ManualResetEvent(IsCancellationRequested);
                if (Interlocked.CompareExchange(ref _waitHandle, handle, null) != null)
                    return _waitHandle!;
                Token.Register(static state => ((ManualResetEvent)state!).Set(), handle);
                return handle;
            }
        }
    }
}
