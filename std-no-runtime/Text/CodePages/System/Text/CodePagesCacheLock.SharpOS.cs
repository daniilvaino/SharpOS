// SharpOS (not upstream): the lock CodePagesEncodingProvider and EncodingTable
// keep their caches under, in place of ReaderWriterLockSlim. std has no
// ReaderWriterLockSlim — its per-thread lock counts live in [ThreadStatic]
// storage, which this runtime does not support (docs/nativeaot-nostd-kernel-limits.md).
// The same members over one Monitor: readers no longer run side by side, which
// costs only when several threads look up encodings at once; an upgradeable read
// taking the write lock is the same thread re-entering, which Monitor allows.

using System.Threading;

namespace System.Text
{
    internal sealed class CodePagesCacheLock
    {
        private readonly object _gate = new object();

        public void EnterUpgradeableReadLock() => Monitor.Enter(_gate);

        public void ExitUpgradeableReadLock() => Monitor.Exit(_gate);

        public void EnterWriteLock() => Monitor.Enter(_gate);

        public void ExitWriteLock() => Monitor.Exit(_gate);
    }
}
