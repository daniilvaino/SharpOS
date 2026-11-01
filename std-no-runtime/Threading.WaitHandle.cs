// WaitHandle, EventWaitHandle, ManualResetEvent, AutoResetEvent — BCL's
// surface (dotnet/runtime v8.0.27, System.Private.CoreLib/src/System/
// Threading/WaitHandle.cs, EventWaitHandle.cs, ManualResetEvent.cs,
// AutoResetEvent.cs, EventResetMode.cs; MIT) over a word instead of an OS
// handle: the event is the word, and a wait blocks on it through
// ThreadBackend.WaitWhile (WaitOnAddress where the tier has it).
//
// Written for SemaphoreSlim.AvailableWaitHandle and BlockingCollection's
// AddToAny/TakeFromAny (step223).
//
// SharpOS cut: no Handle / SafeWaitHandle, no named events, no
// SignalAndWait — there is no OS handle behind these.
// SharpOS cut: WaitAny / WaitAll over more than one handle poll their words
// a millisecond at a time — one wait cannot block on several words.

namespace System.Threading
{
    public enum EventResetMode
    {
        AutoReset = 0,
        ManualReset = 1,
    }

    public abstract class WaitHandle : IDisposable
    {
        public const int WaitTimeout = 0x102;

        // Set (1) or not (0). The object never moves, so its address is safe
        // to block on.
        private protected int _signaled;
        private protected bool _autoReset;
        private bool _disposed;

        protected WaitHandle() { }

        // Takes the signal when it is there: an auto-reset event hands it to
        // one waiter only.
        private protected bool TryAcquire()
        {
            if (_autoReset)
                return Interlocked.CompareExchange(ref _signaled, 0, 1) == 1;
            return Volatile.Read(ref _signaled) != 0;
        }

        public virtual bool WaitOne() => WaitOneCore(Timeout.Infinite);

        public virtual bool WaitOne(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return WaitOneCore(millisecondsTimeout);
        }

        public virtual bool WaitOne(TimeSpan timeout) => WaitOneCore(ToTimeoutMilliseconds(timeout));

        public virtual bool WaitOne(int millisecondsTimeout, bool exitContext) => WaitOne(millisecondsTimeout);

        public virtual bool WaitOne(TimeSpan timeout, bool exitContext) => WaitOne(timeout);

        private bool WaitOneCore(int millisecondsTimeout)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryAcquire()) return true;
            if (millisecondsTimeout == 0) return false;

            if (millisecondsTimeout == Timeout.Infinite)
            {
                while (!TryAcquire())
                    ThreadBackend.WaitWhile(ref _signaled, 0, ThreadBackend.Infinite);
                return true;
            }

            for (int waited = 0; waited < millisecondsTimeout; waited++)
            {
                ThreadBackend.WaitWhile(ref _signaled, 0, (uint)(millisecondsTimeout - waited));
                if (TryAcquire()) return true;
            }
            return false;
        }

        public static int WaitAny(WaitHandle[] waitHandles) => WaitAny(waitHandles, Timeout.Infinite);

        public static int WaitAny(WaitHandle[] waitHandles, TimeSpan timeout) => WaitAny(waitHandles, ToTimeoutMilliseconds(timeout));

        public static int WaitAny(WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext) => WaitAny(waitHandles, millisecondsTimeout);

        public static int WaitAny(WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext) => WaitAny(waitHandles, timeout);

        public static int WaitAny(WaitHandle[] waitHandles, int millisecondsTimeout)
        {
            ValidateHandles(waitHandles, millisecondsTimeout);
            if (waitHandles.Length == 1)
                return waitHandles[0].WaitOneCore(millisecondsTimeout) ? 0 : WaitTimeout;

            for (int waited = 0; ; waited++)
            {
                for (int i = 0; i < waitHandles.Length; i++)
                {
                    if (waitHandles[i].TryAcquire()) return i;
                }
                if (millisecondsTimeout != Timeout.Infinite && waited >= millisecondsTimeout)
                    return WaitTimeout;
                ThreadBackend.WaitWhile(ref waitHandles[0]._signaled, 0, 1);
            }
        }

        public static bool WaitAll(WaitHandle[] waitHandles) => WaitAll(waitHandles, Timeout.Infinite);

        public static bool WaitAll(WaitHandle[] waitHandles, TimeSpan timeout) => WaitAll(waitHandles, ToTimeoutMilliseconds(timeout));

        public static bool WaitAll(WaitHandle[] waitHandles, int millisecondsTimeout, bool exitContext) => WaitAll(waitHandles, millisecondsTimeout);

        public static bool WaitAll(WaitHandle[] waitHandles, TimeSpan timeout, bool exitContext) => WaitAll(waitHandles, timeout);

        // Waits until every handle is signaled at the same time, then takes
        // the auto-reset ones together — as Win32's WaitForMultipleObjects.
        // Taking them is not atomic across handles here: another waiter may
        // win one in between, and then this goes round again.
        public static bool WaitAll(WaitHandle[] waitHandles, int millisecondsTimeout)
        {
            ValidateHandles(waitHandles, millisecondsTimeout);
            for (int waited = 0; ; waited++)
            {
                bool all = true;
                for (int i = 0; i < waitHandles.Length && all; i++)
                    all = Volatile.Read(ref waitHandles[i]._signaled) != 0;
                if (all)
                {
                    int taken = 0;
                    while (taken < waitHandles.Length && waitHandles[taken].TryAcquire()) taken++;
                    if (taken == waitHandles.Length) return true;
                    for (int i = 0; i < taken; i++)
                        if (waitHandles[i]._autoReset) Volatile.Write(ref waitHandles[i]._signaled, 1);
                }
                if (millisecondsTimeout != Timeout.Infinite && waited >= millisecondsTimeout)
                    return false;
                ThreadBackend.WaitWhile(ref waitHandles[0]._signaled, 0, 1);
            }
        }

        private static void ValidateHandles(WaitHandle[] waitHandles, int millisecondsTimeout)
        {
            ArgumentNullException.ThrowIfNull(waitHandles);
            if (waitHandles.Length == 0)
                throw new ArgumentException(SR.Argument_EmptyWaithandleArray, nameof(waitHandles));
            if (millisecondsTimeout < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            for (int i = 0; i < waitHandles.Length; i++)
            {
                if (waitHandles[i] == null)
                    throw new ArgumentNullException("waitHandles[" + i.ToString() + "]", SR.ArgumentNull_ArrayElement);
                ObjectDisposedException.ThrowIf(waitHandles[i]._disposed, waitHandles[i]);
            }
        }

        private static int ToTimeoutMilliseconds(TimeSpan timeout)
        {
            long ms = (long)timeout.TotalMilliseconds;
            if (ms < -1 || ms > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return (int)ms;
        }

        public virtual void Close() => Dispose();

        protected virtual void Dispose(bool explicitDisposing) => _disposed = true;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }

    public class EventWaitHandle : WaitHandle
    {
        public EventWaitHandle(bool initialState, EventResetMode mode)
        {
            if (mode != EventResetMode.AutoReset && mode != EventResetMode.ManualReset)
                throw new ArgumentException(SR.Argument_InvalidFlag, nameof(mode));
            _signaled = initialState ? 1 : 0;
            _autoReset = mode == EventResetMode.AutoReset;
        }

        public bool Set()
        {
            Volatile.Write(ref _signaled, 1);
            ThreadBackend.WakeAll(ref _signaled);
            return true;
        }

        public bool Reset()
        {
            Volatile.Write(ref _signaled, 0);
            return true;
        }
    }

    public sealed class ManualResetEvent : EventWaitHandle
    {
        public ManualResetEvent(bool initialState) : base(initialState, EventResetMode.ManualReset) { }
    }

    public sealed class AutoResetEvent : EventWaitHandle
    {
        public AutoResetEvent(bool initialState) : base(initialState, EventResetMode.AutoReset) { }
    }
}
