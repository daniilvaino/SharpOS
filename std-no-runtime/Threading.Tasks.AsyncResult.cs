// Task as IAsyncResult, as in BCL — what TaskToAsyncResult (Stream's
// BeginRead/EndRead) hands back a wait handle through.

namespace System.Threading.Tasks
{
    public partial class Task : IAsyncResult
    {
        private ManualResetEvent? _asyncWaitHandle;

        bool IAsyncResult.CompletedSynchronously => false;

        WaitHandle IAsyncResult.AsyncWaitHandle
        {
            get
            {
                ManualResetEvent? handle = _asyncWaitHandle;
                if (handle != null) return handle;

                handle = new ManualResetEvent(IsCompleted);
                if (Interlocked.CompareExchange(ref _asyncWaitHandle, handle, null) != null)
                    return _asyncWaitHandle!;
                if (!IsCompleted)
                    ContinueWith(_ => handle.Set(), TaskContinuationOptions.ExecuteSynchronously);
                return handle;
            }
        }
    }
}
