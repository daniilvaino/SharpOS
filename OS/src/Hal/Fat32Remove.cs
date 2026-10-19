namespace OS.Hal
{
    // Deleting files, making and removing directories, renaming and moving
    // (step208).
    //
    // A deleted entry is marked 0xE5 — the 8.3 entry and every long-name
    // entry before it (FindIn notes where they lie): left behind, the pieces
    // of a long name would be read as the name of whatever entry follows.
    // The entries go first, the clusters after: a failure in between leaves
    // clusters nobody owns, never an entry that points at free ones.
    //
    // A directory is a cluster of entries that starts with "." (itself) and
    // ".." (its parent; 0 for the root, as FAT32 has it), and an entry in its
    // parent with the directory attribute — short name or long, as for a
    // file. Only an empty directory is removed. Neither the new directory
    // nor its parent grows past its clusters, as for files.
    internal static unsafe partial class Fat32
    {
        /// <summary>Results of <see cref="DeleteFile"/>, <see cref="CreateDirectory"/>, <see cref="RemoveDirectory"/>.</summary>
        public const int ChangeOk = 0, ChangeNotFound = -1, ChangeBadName = -2, ChangeNoRoom = -3,
                         ChangeDiskError = -4, ChangeBusy = -5, ChangeIsDirectory = -6,
                         ChangeNotDirectory = -7, ChangeNotEmpty = -8, ChangeExists = -9;

        /// <summary>Deletes a file: its entries and its clusters. Busy while it is open, or written by sectors (the boot log).</summary>
        public static int DeleteFile(string path)
        {
            Enter();
            try
            {
                PathChanged(path);   // its clusters may be the next file's
                int result = DeleteFileLocked(path);
                if (result == ChangeOk) FlushDevice();
                return result;
            }
            finally { Leave(); }
        }

        /// <summary>Makes one directory; its parent must exist. <see cref="ChangeExists"/> when a directory of the name is there.</summary>
        public static int CreateDirectory(string path)
        {
            Enter();
            try
            {
                int result = CreateDirectoryLocked(path);
                if (result == ChangeOk) FlushDevice();
                return result;
            }
            finally { Leave(); }
        }

        /// <summary>Removes an empty directory.</summary>
        public static int RemoveDirectory(string path)
        {
            Enter();
            try
            {
                int result = RemoveDirectoryLocked(path);
                if (result == ChangeOk) FlushDevice();
                return result;
            }
            finally { Leave(); }
        }

        /// <summary>Renames or moves a file or a directory; the target must not exist.</summary>
        public static int Rename(string from, string to)
        {
            Enter();
            try
            {
                int result = RenameLocked(from, to);
                if (result == ChangeOk) FlushDevice();
                return result;
            }
            finally { Leave(); }
        }

        /// <summary>
        /// What a path is (step208): size, a directory or not, and when it was
        /// last written — FAT's date and time as one number, the date in the
        /// high 16 bits (years since 1980, month, day), the time in the low
        /// (hours, minutes, two-second units).
        /// </summary>
        public static bool StatEntry(string path, out uint size, out bool isDir, out uint written)
        {
            Enter();
            try
            {
                written = 0;
                if (!Resolve(path, out _, out size, out isDir)) return false;
                if (!s_disk.Read(s_foundEntryLba, 1, s_sec)) return true;
                byte* e = s_sec + s_foundEntryOffset;
                written = ((uint)RdU16(e, 24) << 16) | RdU16(e, 22);
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>
        /// The volume's size and its free space, in bytes (step208): the kept
        /// free-cluster count (Fat32FsInfo.cs), counted in the FAT the first
        /// time when FSInfo did not have it.
        /// </summary>
        public static bool VolumeSpace(out ulong total, out ulong free)
        {
            Enter();
            try
            {
                total = 0;
                free = 0;
                if (!s_mounted || s_disk == null || !s_isFat32) return false;
                if (!s_freeKnown && !CountFree()) return false;
                total = (ulong)(s_clusterCount - 2) * ClusterBytes;
                free = (ulong)s_freeClusters * ClusterBytes;
                return true;
            }
            finally { Leave(); }
        }

        private static bool Writable => s_mounted && s_disk != null && s_isFat32;

        // The new entry first, the old one after: a failure in between leaves
        // two names for the same clusters, never clusters no name reaches.
        // The new entry takes the old one's attributes, times and size; a
        // directory's ".." then names its new parent.
        private static int RenameLocked(string from, string to)
        {
            if (!Writable || from == null || to == null) return ChangeDiskError;
            if (!Resolve(from, out uint first, out _, out bool isDir)) return ChangeNotFound;
            if (isDir && (first < 2 || first == s_rootClus)) return ChangeBadName;
            ulong oldLba = s_foundEntryLba;
            uint oldOffset = s_foundEntryOffset;
            LfnPlaces oldLfn = s_foundLfn;
            // A writer updates its entry at close — the old one, after a rename.
            if (WrittenNow(oldLba, oldOffset, first)) return ChangeBusy;

            if (!s_disk.Read(oldLba, 1, s_sec)) return ChangeDiskError;
            byte* saved = stackalloc byte[32];
            for (int i = 0; i < 32; i++) saved[i] = s_sec[oldOffset + i];

            // The target may be the same entry under another case: that one is renamed.
            if (Resolve(to, out _, out _, out _) && (s_foundEntryLba != oldLba || s_foundEntryOffset != oldOffset))
                return ChangeExists;
            if (!TrySplitParent(to, out uint parent, out int nameStart, out int nameLength)) return ChangeNotFound;
            if (to[nameStart] == '.' && (nameLength == 1 || (nameLength == 2 && to[nameStart + 1] == '.')))
                return ChangeBadName;
            if (isDir && IsWithin(parent, first)) return ChangeBadName;

            byte* name83 = stackalloc byte[11];
            ulong newLba;
            uint newOffset;
            bool written = TryMake83(to, nameStart, nameLength, name83, out byte caseFlags)
                ? TryWriteDirectoryEntry(parent, name83, first, 0, out newLba, out newOffset, saved[11], caseFlags)
                : TryCreateLongEntry(parent, to, nameStart, nameLength, out newLba, out newOffset, first, saved[11]);
            if (!written) return ChangeNoRoom;
            if (!s_disk.Read(newLba, 1, s_sec)) return ChangeDiskError;
            for (int i = 13; i < 32; i++) s_sec[newOffset + i] = saved[i];      // 12: the new name's case
            if (!s_disk.Write(newLba, 1, s_sec)) return ChangeDiskError;

            s_cursorValid = false;
            for (int i = 0; i < oldLfn.Count; i++)
                if (!MarkDeleted(oldLfn.Lba[i], oldLfn.Offset[i])) return ChangeDiskError;
            if (!MarkDeleted(oldLba, oldOffset)) return ChangeDiskError;

            if (isDir)
            {
                ulong lba = ClusterLba(first);
                if (!s_disk.Read(lba, 1, s_sec)) return ChangeDiskError;
                byte* dotdot = s_sec + 32;
                if (dotdot[0] == (byte)'.' && dotdot[1] == (byte)'.')
                {
                    uint p = parent == s_rootClus ? 0 : parent;
                    dotdot[20] = (byte)(p >> 16);
                    dotdot[21] = (byte)(p >> 24);
                    dotdot[26] = (byte)p;
                    dotdot[27] = (byte)(p >> 8);
                    if (!s_disk.Write(lba, 1, s_sec)) return ChangeDiskError;
                }
            }
            return ChangeOk;
        }

        // Whether the directory at `cluster` is `ancestor` or lies inside it:
        // up the ".." entries to the root.
        private static bool IsWithin(uint cluster, uint ancestor)
        {
            uint guard = 0;
            while (cluster >= 2 && cluster != s_rootClus && guard++ < 10000)
            {
                if (cluster == ancestor) return true;
                if (!s_disk.Read(ClusterLba(cluster), 1, s_sec)) return true;   // unknown: refuse
                byte* dotdot = s_sec + 32;
                if (dotdot[0] != (byte)'.' || dotdot[1] != (byte)'.') return false;
                cluster = ((uint)RdU16(dotdot, 20) << 16) | RdU16(dotdot, 26);
            }
            return false;
        }

        // Open for writing by a handle, or written by sectors.
        private static bool WrittenNow(ulong entryLba, uint entryOffset, uint first)
        {
            if (s_open != null)
                foreach (OpenFile o in s_open)
                    if (o != null && o.Writing && o.EntryLba == entryLba && o.EntryOffset == entryOffset) return true;
            if (s_linearRuns != null && first >= 2)
                for (int i = 0; i < s_linearRuns.Count; i += 2)
                    if (s_linearRuns[i] == ClusterLba(first)) return true;
            return false;
        }

        // ---- the device's cache (step208) ----

        /// <summary>Whether the device refused a flush; it is not asked again.</summary>
        public static bool FlushRefused;

        // The device's write cache to the medium, after a file closed or a
        // directory changed: what a program finished writing survives a power
        // cut from then on. A device that refuses the command (some sticks
        // do) is not asked again — and a refusal is no failure of the write.
        private static void FlushDevice()
        {
            if (s_disk == null) return;
            WriteFsInfo();
            if (FlushRefused) return;
            if (s_disk.Flush()) DeviceFlushes++;
            else FlushRefused = true;
        }

        /// <summary>Flushes the device took — said in the log at power-off.</summary>
        public static uint DeviceFlushes;

        /// <summary>
        /// Before the machine powers off: the FAT sector held in the cache and
        /// the device's cache to the medium. Does not wait for the volume — a
        /// panic in the middle of a write holds it, and would wait forever.
        /// </summary>
        public static void FlushForShutdown()
        {
            if (!s_mounted || s_disk == null) return;
            int me = OS.Kernel.Threading.Scheduler.Current?.Id ?? 0;
            if (!(s_lockDepth > 0 && s_lockOwner == me))
            {
                fixed (ulong* flag = &s_lock)
                    if (X64Asm.CmpXchg64(flag, value: 1UL, comparand: 0UL) != 0UL) return;
                s_lockOwner = me;
                s_lockDepth = 1;
            }
            else s_lockDepth++;
            try
            {
                FlushFatCache();
                FlushDevice();
                DebugLog.Begin(LogLevel.Info);
                Console.Write("[fs] device cache flushed ");
                Console.WriteUInt(DeviceFlushes);
                Console.Write(FlushRefused ? " time(s), then the device refused the command" : " time(s)");
                if (s_disk is CachedDisk cache)
                {
                    Console.Write("; sector cache ");
                    Console.WriteULong(cache.Hits);
                    Console.Write(" hits, ");
                    Console.WriteULong(cache.Misses);
                    Console.Write(" misses");
                }
                DebugLog.EndLine();
            }
            finally { Leave(); }
        }

        private static int DeleteFileLocked(string path)
        {
            if (!Writable || path == null) return ChangeDiskError;
            if (!Resolve(path, out uint first, out _, out bool isDir)) return ChangeNotFound;
            if (isDir) return ChangeIsDirectory;
            if (InUse(s_foundEntryLba, s_foundEntryOffset, first)) return ChangeBusy;
            return RemoveEntry(first) ? ChangeOk : ChangeDiskError;
        }

        private static int CreateDirectoryLocked(string path)
        {
            if (!Writable || path == null) return ChangeDiskError;
            if (Resolve(path, out _, out _, out bool isDir)) return isDir ? ChangeExists : ChangeNotDirectory;
            if (!TrySplitParent(path, out uint parent, out int nameStart, out int nameLength)) return ChangeNotFound;
            if (path[nameStart] == '.' && (nameLength == 1 || (nameLength == 2 && path[nameStart + 1] == '.')))
                return ChangeBadName;

            if (!TryAllocateChain(1, out uint cluster)) return ChangeNoRoom;
            if (!TryWriteDotEntries(cluster, parent))
            {
                FreeChain(cluster);
                return ChangeDiskError;
            }

            byte* name83 = stackalloc byte[11];
            bool written = TryMake83(path, nameStart, nameLength, name83, out byte caseFlags)
                ? TryWriteDirectoryEntry(parent, name83, cluster, 0, out _, out _, AttrDirectory, caseFlags)
                : TryCreateLongEntry(parent, path, nameStart, nameLength, out _, out _, cluster, AttrDirectory);
            if (!written)
            {
                FreeChain(cluster);
                return ChangeNoRoom;
            }
            return ChangeOk;
        }

        private static int RemoveDirectoryLocked(string path)
        {
            if (!Writable || path == null) return ChangeDiskError;
            if (!Resolve(path, out uint first, out _, out bool isDir)) return ChangeNotFound;
            if (!isDir) return ChangeNotDirectory;
            if (first < 2 || first == s_rootClus) return ChangeBadName;
            // Where the entry lies, before the emptiness walk reads other sectors.
            ulong entryLba = s_foundEntryLba;
            uint entryOffset = s_foundEntryOffset;
            LfnPlaces lfn = s_foundLfn;

            int empty = IsEmptyDirectory(first);
            if (empty < 0) return ChangeDiskError;
            if (empty == 0) return ChangeNotEmpty;

            s_foundEntryLba = entryLba;
            s_foundEntryOffset = entryOffset;
            s_foundLfn = lfn;
            return RemoveEntry(first) ? ChangeOk : ChangeDiskError;
        }

        // The entry FindIn found last, and its long-name entries, marked
        // deleted; then its clusters freed.
        private static bool RemoveEntry(uint first)
        {
            s_cursorValid = false;
            for (int i = 0; i < s_foundLfn.Count; i++)
                if (!MarkDeleted(s_foundLfn.Lba[i], s_foundLfn.Offset[i])) return false;
            if (!MarkDeleted(s_foundEntryLba, s_foundEntryOffset)) return false;
            if (first >= 2 && !FreeChainDeferred(first)) return false;
            return FlushFatCache();
        }

        private static bool MarkDeleted(ulong lba, uint offset)
        {
            if (!s_disk.Read(lba, 1, s_sec)) return false;
            s_sec[offset] = 0xE5;
            return s_disk.Write(lba, 1, s_sec);
        }

        // Open by a handle (a reader by its clusters, a writer by its entry),
        // or a file written by sectors.
        private static bool InUse(ulong entryLba, uint entryOffset, uint first)
        {
            if (s_open != null)
                foreach (OpenFile o in s_open)
                    if (o != null && ((o.Writing && o.EntryLba == entryLba && o.EntryOffset == entryOffset)
                                      || (first >= 2 && o.First == first)))
                        return true;
            if (s_linearRuns != null && first >= 2)
                for (int i = 0; i < s_linearRuns.Count; i += 2)
                    if (s_linearRuns[i] == ClusterLba(first)) return true;
            return false;
        }

        // "." and ".." in the new directory's first sector; the rest of its
        // cluster is zero (TryAllocateChain zeroes it): the end of the list.
        private static bool TryWriteDotEntries(uint cluster, uint parent)
        {
            for (uint i = 0; i < s_bps; i++) s_sec[i] = 0;
            byte* name = stackalloc byte[11];
            for (int i = 0; i < 11; i++) name[i] = 0x20;
            name[0] = (byte)'.';
            FillEntry(s_sec, name, cluster, 0, AttrDirectory);
            name[1] = (byte)'.';
            FillEntry(s_sec + 32, name, parent == s_rootClus ? 0 : parent, 0, AttrDirectory);
            return s_disk.Write(ClusterLba(cluster), 1, s_sec);
        }

        // 1 when the directory holds nothing but "." and "..", 0 when it holds
        // anything else (a long-name piece counts), -1 on a disk error.
        private static int IsEmptyDirectory(uint first)
        {
            uint cluster = first;
            uint guard = 0;
            while (cluster >= 2 && guard++ < 1_000_000)
            {
                ulong lba = ClusterLba(cluster);
                for (uint s = 0; s < s_spc; s++)
                {
                    if (!ReadAbs(lba + s)) return -1;
                    for (uint off = 0; off < s_bps; off += 32)
                    {
                        byte* e = s_sec + off;
                        if (e[0] == 0x00) return 1;
                        if (e[0] == 0xE5) continue;
                        bool dot = e[0] == (byte)'.' && (e[11] & AttrDirectory) != 0
                                   && (e[1] == 0x20 || (e[1] == (byte)'.' && e[2] == 0x20));
                        if (!dot) return 0;
                    }
                }
                cluster = FatNext(cluster);
            }
            return 1;
        }
    }
}
