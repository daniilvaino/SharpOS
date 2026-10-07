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

        /// <summary>
        /// Grows with every write to the volume that may change a file read
        /// from it (step196). A sector write inside a file opened for sector
        /// writes (TryOpenLinear: the boot log, a write per line) does not
        /// count — only that file changed, and <see cref="IsSectorWritten"/>
        /// names it.
        /// </summary>
        public static ulong WriteGeneration;

        // Files opened for sector writes: their upper-cased paths and sector runs.
        // Made on first use: the volume mounts early, statics with initializers come later.
        private static System.Collections.Generic.List<string> s_linearPaths;
        private static System.Collections.Generic.List<ulong> s_linearRuns;

        /// <summary>Whether the file was opened for sector writes: its content changes without <see cref="WriteGeneration"/>.</summary>
        public static bool IsSectorWritten(string upperPath)
        {
            Enter();
            try { return s_linearPaths != null && s_linearPaths.Contains(upperPath); }
            finally { Leave(); }
        }

        // Sectors [lba, lba + count) all inside one file opened for sector writes.
        private static bool InSectorWrittenFile(ulong lba, uint count)
        {
            if (s_linearRuns == null) return false;
            for (int i = 0; i < s_linearRuns.Count; i += 2)
                if (lba >= s_linearRuns[i] && lba + count <= s_linearRuns[i] + s_linearRuns[i + 1]) return true;
            return false;
        }

        private static void Wrote(ulong lba, uint count)
        {
            if (!InSectorWrittenFile(lba, count)) WriteGeneration++;
        }

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
            try
            {
                if (!TryOpenLinearLocked(path, out startLba, out sectors)) return false;
                string key = path.ToUpperInvariant();
                s_linearPaths ??= new System.Collections.Generic.List<string>();
                s_linearRuns ??= new System.Collections.Generic.List<ulong>();
                if (!s_linearPaths.Contains(key))
                {
                    s_linearPaths.Add(key);
                    s_linearRuns.Add(startLba);
                    s_linearRuns.Add(sectors);
                }
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>
        /// WriteSectorAt that does not wait (step196): false at once when the
        /// volume is busy. For a writer inside a critical section — the boot
        /// log written from code that suppressed preemption — where waiting
        /// means yielding, and yielding there lets another thread into the
        /// section: an app's collector logging while another thread of it
        /// held the volume on a long read let its allocator be entered twice.
        /// </summary>
        public static bool TryWriteSectorAt(ulong lba, byte* src, out bool busy)
        {
            busy = false;
            int me = Scheduler.Current?.Id ?? 0;
            if (!(s_lockDepth > 0 && s_lockOwner == me))
            {
                fixed (ulong* flag = &s_lock)
                {
                    if (X64Asm.CmpXchg64(flag, value: 1UL, comparand: 0UL) != 0UL)
                    {
                        busy = true;
                        return false;
                    }
                }
                s_lockOwner = me;
                s_lockDepth = 1;
            }
            else s_lockDepth++;
            try { Wrote(lba, 1); return WriteSectorAtLocked(lba, src); }
            finally { Leave(); }
        }

        public static bool WriteSectorAt(ulong lba, byte* src)
        {
            Enter();
            try { Wrote(lba, 1); return WriteSectorAtLocked(lba, src); }
            finally { Leave(); }
        }

        public static bool BlankSectors(ulong lba, uint count)
        {
            Enter();
            try { Wrote(lba, count); return BlankSectorsLocked(lba, count); }
            finally { Leave(); }
        }

        public static int WriteFileInPlace(string path, byte* src, int len)
        {
            Enter();
            try { WriteGeneration++; return WriteFileInPlaceLocked(path, src, len); }
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
            try { WriteGeneration++; return TryCreateFileLocked(path, sizeBytes); }
            finally { Leave(); }
        }
    }
}
