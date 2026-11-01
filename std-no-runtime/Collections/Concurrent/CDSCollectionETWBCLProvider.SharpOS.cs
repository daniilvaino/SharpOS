// The event source System.Collections.Concurrent logs to (BCL:
// CDSCollectionETWBCLProvider.cs, an EventSource). SharpOS: no ETW — the
// provider is never enabled, its events are no-ops, and the concurrent
// collections' code stays as BCL wrote it.

namespace System.Collections.Concurrent
{
    internal sealed class CDSCollectionETWBCLProvider
    {
        public static readonly CDSCollectionETWBCLProvider Log = new CDSCollectionETWBCLProvider();

        public bool IsEnabled() => false;

        public void ConcurrentStack_FastPushFailed(int spinCount) { }

        public void ConcurrentStack_FastPopFailed(int spinCount) { }

        public void ConcurrentDictionary_AcquiringAllLocks(int numOfBuckets) { }

        public void ConcurrentBag_TryTakeSteals() { }

        public void ConcurrentBag_TryPeekSteals() { }
    }
}
