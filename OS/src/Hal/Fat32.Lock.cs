using OS.Kernel.Threading;

namespace OS.Hal
{
    // One file-system operation at a time.
    //
    // The volume's state is shared: one DMA sector buffer, one bulk buffer,
    // one cached FAT sector, one directory-enumeration cursor. Every public
    // operation touches some of them, and with preemption a second thread
    // entering mid-operation reads a buffer the first is halfway through
    // (step174 named this race; pipe_plan.md item 9). A lock rather than
    // suppressed preemption: an operation waits on the disk, and holding the
    // whole machine for that is what preemption exists to avoid. Re-entrant
    // for the owning thread, because the operations call each other.
    internal static unsafe partial class Fat32
    {
        private static ulong s_lock;
        private static int s_lockOwner;
        private static int s_lockDepth;

        private static void Enter()
        {
            int me = Scheduler.Current?.Id ?? 0;
            if (s_lockDepth > 0 && s_lockOwner == me)
            {
                s_lockDepth++;
                return;
            }
            fixed (ulong* flag = &s_lock)
                SpinYieldLock.Acquire(flag);
            s_lockOwner = me;
            s_lockDepth = 1;
        }

        private static void Leave()
        {
            if (--s_lockDepth != 0)
                return;
            s_lockOwner = 0;
            fixed (ulong* flag = &s_lock)
                SpinYieldLock.Release(flag);
        }

        public static bool Mount(Disk disk)
        {
            Enter();
            try { return MountLocked(disk); }
            finally { Leave(); }
        }

        public static bool Exists(string path)
        {
            Enter();
            try { return ExistsLocked(path); }
            finally { Leave(); }
        }

        public static bool Stat(string path, out uint size, out bool isDir)
        {
            Enter();
            try { return StatLocked(path, out size, out isDir); }
            finally { Leave(); }
        }

        public static bool EnumDir(string path, uint index,
            char* nameOut, uint nameCap, out uint nameLen, out ulong attrs)
        {
            Enter();
            try { return EnumDirLocked(path, index, nameOut, nameCap, out nameLen, out attrs); }
            finally { Leave(); }
        }

        public static bool TryOpenLinear(string path, out ulong startLba, out uint sectors)
        {
            Enter();
            try { return TryOpenLinearLocked(path, out startLba, out sectors); }
            finally { Leave(); }
        }

        public static bool WriteSectorAt(ulong lba, byte* src)
        {
            Enter();
            try { return WriteSectorAtLocked(lba, src); }
            finally { Leave(); }
        }

        public static bool BlankSectors(ulong lba, uint count)
        {
            Enter();
            try { return BlankSectorsLocked(lba, count); }
            finally { Leave(); }
        }

        public static int WriteFileInPlace(string path, byte* src, int len)
        {
            Enter();
            try { return WriteFileInPlaceLocked(path, src, len); }
            finally { Leave(); }
        }

        public static int ReadFile(string path, byte* dst, int cap, out uint fileSize)
        {
            Enter();
            try { return ReadFileLocked(path, dst, cap, out fileSize); }
            finally { Leave(); }
        }

        public static bool TryCreateFile(string path, uint sizeBytes)
        {
            Enter();
            try { return TryCreateFileLocked(path, sizeBytes); }
            finally { Leave(); }
        }
    }
}
