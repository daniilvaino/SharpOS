namespace OS.Kernel.Threading
{
    // A Mutex its owner may take again (step210): a driver's command lock.
    //
    // The bargain the drivers made before interrupts — preemption suppressed
    // for a whole command — nested by itself: a line logged in the middle of a
    // USB transfer goes to the boot log, which is a file on the same stick, and
    // its write ran inside the outer command on the same thread. With a plain
    // Mutex in that place the thread waits for itself (seen: UsbDisk.Read →
    // TryWaitEvent → BootLog.Flush → UsbDisk.Write → Enter, boot stuck). The
    // nested call is sequential, not concurrent, so letting the owner through
    // keeps what the suppression allowed.
    internal sealed class OwnedLock
    {
        private readonly Mutex _mutex = new Mutex();
        private Thread? _owner;
        private int _depth;

        public void Enter()
        {
            Thread? me = Scheduler.Current;
            if (me != null && _owner == me)
            {
                _depth++;
                return;
            }
            _mutex.Acquire();
            _owner = me;
            _depth = 1;
        }

        public void Leave()
        {
            if (--_depth != 0) return;
            _owner = null;
            _mutex.Release();
        }
    }
}
