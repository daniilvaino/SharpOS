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

        // Files opened for sector writes: their upper-cased paths and sector runs.
        // Made on first use: the volume mounts early, statics with initializers come later.
        private static System.Collections.Generic.List<string> s_linearPaths;
        private static System.Collections.Generic.List<ulong> s_linearRuns;

        /// <summary>
        /// Whether the file was opened for sector writes (TryOpenLinear: the
        /// boot log, a write per line): its content changes without a note in
        /// the journal of changes (<see cref="ChangedSince"/>).
        /// </summary>
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
            if (InSectorWrittenFile(lba, count)) return;
            ContentChanged(AnyFile);
        }

        // Whose content changed, by first cluster (step208): the last
        // ChangedRing changes, numbered by ContentGeneration. The program
        // cache asks about its own files only - before it, every write to the
        // volume dropped the whole cache, and the shell writes its history
        // after every line, so an interactive KQL read its 5.8 MB anew on
        // every start. A sector write from outside a file names no file: it
        // stands for all of them.
        private const int ChangedRing = 256;
        private const uint AnyFile = 0;
        private static ulong s_contentGeneration;
        private static uint[] s_changedFirst;

        // Under the lock. A file with no clusters yet (0) is new or empty: no
        // copy of it can be kept, and a deleted file's clusters are noted when
        // it is deleted, before anything can reuse them.
        private static void ContentChanged(uint first)
        {
            s_changedFirst ??= new uint[ChangedRing];
            s_contentGeneration++;
            s_changedFirst[(int)(s_contentGeneration % ChangedRing)] = first;
        }

        private static void FileChanged(uint first)
        {
            if (first != AnyFile) ContentChanged(first);
        }

        // The file at the path, noted as changed (it is about to be).
        private static void PathChanged(string path)
        {
            if (path != null && Resolve(path, out uint first, out _, out bool isDir) && !isDir) FileChanged(first);
        }

        /// <summary>Where content changes stand now: pass it to <see cref="ChangedSince"/> later.</summary>
        public static ulong ContentGeneration
        {
            get { Enter(); try { return s_contentGeneration; } finally { Leave(); } }
        }

        /// <summary>
        /// Whether the file starting at <paramref name="first"/> may have changed
        /// since <paramref name="generation"/>; when not, <paramref name="generation"/>
        /// moves to now. Older than the journal reaches is "may have".
        /// </summary>
        public static bool ChangedSince(uint first, ref ulong generation)
        {
            Enter();
            try
            {
                ulong now = s_contentGeneration;
                if (generation > now || now - generation > ChangedRing) return true;
                for (ulong g = generation + 1; g <= now; g++)
                {
                    uint c = s_changedFirst[(int)(g % ChangedRing)];
                    if (c == AnyFile || c == first) return true;
                }
                generation = now;
                return false;
            }
            finally { Leave(); }
        }

        /// <summary>A file's first cluster and size; false for a directory or nothing.</summary>
        public static bool Locate(string path, out uint first, out uint size)
        {
            Enter();
            try { return Resolve(path, out first, out size, out bool isDir) && !isDir && s_isFat32; }
            finally { Leave(); }
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
            try
            {
                bool before = s_mounted;
                bool ok = MountLocked(disk);
                if (ok && !before)
                {
                    // Single sectors cached from here on (step208), then the
                    // free-cluster count, once.
                    s_disk = CachedDisk.Over(s_disk, s_bps);
                    if (s_isFat32) ReadFsInfo();
                }
                return ok;
            }
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
            try { PathChanged(path); return WriteFileInPlaceLocked(path, src, len); }
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
            try { PathChanged(path); return TryCreateFileLocked(path, sizeBytes); }
            finally { Leave(); }
        }
    }
}
