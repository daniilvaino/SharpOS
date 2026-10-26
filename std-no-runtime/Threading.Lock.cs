// System.Threading.Lock (.NET 9) — the BCL's API over std's Monitor.
//
// API from dotnet/runtime (MIT), src/libraries/System.Private.CoreLib/src/
// System/Threading/Lock.cs. The BCL's own body is a spin-then-wait lock word
// with its own thread ids; here the lock is the Monitor of the Lock object
// (Threading.Monitor.cs: recursive, owner-checked, as Lock is), which is
// what `lock (obj)` already uses. C# 13 lowers `lock (aLock)` to EnterScope.
//
// Cuts: no LockRecursionException on overflow; TryEnter(TimeSpan) rounds to
// whole milliseconds as Monitor.TryEnter takes them.

namespace System.Threading
{
    public sealed class Lock
    {
        public Lock()
        {
        }

        public bool IsHeldByCurrentThread => Monitor.IsEntered(this);

        public void Enter() => Monitor.Enter(this);

        public bool TryEnter() => Monitor.TryEnter(this);

        public bool TryEnter(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout));
            return Monitor.TryEnter(this, millisecondsTimeout);
        }

        public bool TryEnter(TimeSpan timeout) => TryEnter((int)timeout.TotalMilliseconds);

        public void Exit()
        {
            if (!Monitor.IsEntered(this))
                throw new SynchronizationLockException("The calling thread does not hold the lock.");
            Monitor.Exit(this);
        }

        public Scope EnterScope()
        {
            Enter();
            return new Scope(this);
        }

        public ref struct Scope
        {
            private Lock? _lockObj;

            internal Scope(Lock lockObj) => _lockObj = lockObj;

            public void Dispose()
            {
                Lock? lockObj = _lockObj;
                if (lockObj != null)
                {
                    _lockObj = null;
                    lockObj.Exit();
                }
            }
        }
    }
}
