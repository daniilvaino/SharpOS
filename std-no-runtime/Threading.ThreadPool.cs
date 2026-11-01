// ThreadPool — BCL's queueing surface (System.Private.CoreLib, Threading/
// ThreadPoolWorkQueue.cs: QueueUserWorkItem, UnsafeQueueUserWorkItem) over
// the pool Task.Run already uses (TaskPool, Threading.Tasks.cs). First caller:
// System.IO.Pipelines' scheduler (step224).
//
// SharpOS cut: no worker/IOCP limits (Get/SetMinThreads, SetMaxThreads,
// ThreadCount), no RegisterWaitForSingleObject, no BindHandle; preferLocal
// has no local queue to prefer; nothing flows (ExecutionContext.cs), so
// Unsafe* is the same as the safe one.

using System.Threading.Tasks;

namespace System.Threading
{
    public delegate void WaitCallback(object? state);

    public static class ThreadPool
    {
        public static bool QueueUserWorkItem(WaitCallback callBack) => QueueUserWorkItem(callBack, null);

        public static bool QueueUserWorkItem(WaitCallback callBack, object? state)
        {
            ArgumentNullException.ThrowIfNull(callBack);
            Task.Run(() => callBack(state));
            return true;
        }

        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            ArgumentNullException.ThrowIfNull(callBack);
            _ = preferLocal;
            Task.Run(() => callBack(state));
            return true;
        }

        public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, object? state) => QueueUserWorkItem(callBack, state);

        public static bool UnsafeQueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal) =>
            QueueUserWorkItem(callBack, state, preferLocal);
    }
}
