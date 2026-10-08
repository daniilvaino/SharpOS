namespace OS.Hal
{
    // Open files (step197): a program reads a file in pieces and writes one
    // that grows — READ streams a file of any size, WRITE makes one from a
    // pipe. Until now a file was read whole from its start, and written only
    // in place, never past its size.
    //
    // A file open for reading keeps a cursor: its position and the cluster
    // under it, so the next read continues without walking the chain again.
    // Reads take a run of contiguous clusters per disk command, as ReadFile
    // does.
    //
    // A file open for writing is created (an 8.3 name, or a long one with an
    // alias: Fat32LongNames.cs) or
    // cut to nothing, or kept and appended to. Data gathers in a staging
    // buffer of up to 64 KiB of contiguous clusters and goes to the disk in
    // one command; clusters are linked in the cached FAT sector, which is
    // written when the cache moves on or the file closes (s_fatDirty). The
    // directory entry gets the size at close — a writer that dies is closed
    // by its process's end with what it wrote.
    internal static unsafe partial class Fat32
    {
        public const int OpenRead = 0, OpenWrite = 1, OpenAppend = 2;

        private sealed class OpenFile
        {
            public bool Writing;
            public uint First;
            public uint Size;
            public ulong EntryLba;
            public uint EntryOffset;

            // Reading: the position and the cluster that holds it (0 past the end).
            public uint Position;
            public uint Cluster;

            // Writing: the chain's last cluster, and the staging run — the
            // clusters it covers (contiguous, from RunStart) and the bytes
            // in it.
            public uint Last;
            public byte* Run;
            public uint RunStart;
            public uint RunClusters;
            public uint RunFill;
        }

        private static System.Collections.Generic.List<OpenFile> s_open;

        private static uint ClusterBytes => s_bps * s_spc;

        /// <summary>Opens a file. Returns a handle (1…), or a negative status: -1 not found, -2 bad name or directory, -3 no room, -4 disk error, -5 open for writing by another handle.</summary>
        public static int Open(string path, int mode)
        {
            Enter();
            try { return OpenLocked(path, mode); }
            finally { Leave(); }
        }

        /// <summary>Bytes read into <paramref name="dst"/> (0 at the end), or -1.</summary>
        public static int Read(int handle, byte* dst, int cap)
        {
            Enter();
            try
            {
                OpenFile f = Find(handle);
                return f == null || f.Writing ? -1 : ReadLocked(f, dst, cap);
            }
            finally { Leave(); }
        }

        /// <summary>Appends <paramref name="length"/> bytes. False when the disk is full or failed.</summary>
        public static bool Write(int handle, byte* src, int length)
        {
            Enter();
            try
            {
                OpenFile f = Find(handle);
                if (f == null || !f.Writing) return false;
                WriteGeneration++;
                return WriteLocked(f, src, length);
            }
            finally { Leave(); }
        }

        /// <summary>The file's size now (bytes written so far, for a writer).</summary>
        public static long SizeOf(int handle)
        {
            Enter();
            try { OpenFile f = Find(handle); return f == null ? -1 : f.Size; }
            finally { Leave(); }
        }

        /// <summary>Closes the handle: a writer's last data, the FAT and its size go to the disk.</summary>
        public static bool Close(int handle)
        {
            Enter();
            try
            {
                OpenFile f = Find(handle);
                if (f == null) return false;
                s_open[handle - 1] = null;
                if (!f.Writing) return true;
                WriteGeneration++;
                bool ok = FlushRun(f);
                ok &= FlushFatCache();
                ok &= UpdateEntry(f);
                if (f.Run != null) OS.Kernel.Memory.NativeArena.FreeLarge(f.Run, BulkBytes);
                f.Run = null;
                return ok;
            }
            finally { Leave(); }
        }

        private static OpenFile Find(int handle)
            => s_open != null && handle >= 1 && handle <= s_open.Count ? s_open[handle - 1] : null;

        private static int OpenLocked(string path, int mode)
        {
            if (!s_mounted || s_disk == null || !s_isFat32 || path == null) return -4;
            var f = new OpenFile();
            bool exists = Resolve(path, out uint first, out uint size, out bool isDir);
            if (exists && isDir) return -2;

            if (mode == OpenRead)
            {
                if (!exists) return -1;
                f.First = first;
                f.Size = size;
                f.Cluster = size == 0 ? 0 : first;
                return Keep(f);
            }

            // One writer at a time: a second would build its own chain, and
            // the close that comes last would leave the other's clusters lost.
            if (exists && IsOpenForWriting(s_foundEntryLba, s_foundEntryOffset)) return -5;

            f.Writing = true;
            f.Run = (byte*)OS.Kernel.Memory.NativeArena.Allocate(BulkBytes);
            if (f.Run == null) return -3;
            WriteGeneration++;

            if (exists)
            {
                f.EntryLba = s_foundEntryLba;
                f.EntryOffset = s_foundEntryOffset;
                if (mode == OpenAppend && size != 0 && first >= 2)
                {
                    if (!PrepareAppend(f, first, size)) return Fail(f, -4);
                }
                else
                {
                    // Cut to nothing: the chain goes, the entry says so now —
                    // a writer that dies leaves an empty file, not the old one.
                    if (first >= 2 && !FreeChainDeferred(first)) return Fail(f, -4);
                    f.First = 0;
                    f.Size = 0;
                    if (!FlushFatCache() || !UpdateEntry(f)) return Fail(f, -4);
                }
            }
            else
            {
                if (!TrySplitParent(path, out uint parentCluster, out int nameStart, out int nameLength))
                    return Fail(f, -1);
                byte* name83 = stackalloc byte[11];
                if (TryMake83(path, nameStart, nameLength, name83))
                {
                    if (!TryWriteDirectoryEntry(parentCluster, name83, 0, 0, out f.EntryLba, out f.EntryOffset))
                        return Fail(f, -3);
                }
                else if (!TryCreateLongEntry(parentCluster, path, nameStart, nameLength, out f.EntryLba, out f.EntryOffset))
                {
                    return Fail(f, -2);
                }
            }
            return Keep(f);
        }

        private static bool IsOpenForWriting(ulong entryLba, uint entryOffset)
        {
            if (s_open == null) return false;
            foreach (OpenFile o in s_open)
                if (o != null && o.Writing && o.EntryLba == entryLba && o.EntryOffset == entryOffset)
                    return true;
            return false;
        }

        private static int Fail(OpenFile f, int status)
        {
            if (f.Run != null) OS.Kernel.Memory.NativeArena.FreeLarge(f.Run, BulkBytes);
            f.Run = null;
            return status;
        }

        private static int Keep(OpenFile f)
        {
            s_open ??= new System.Collections.Generic.List<OpenFile>();
            for (int i = 0; i < s_open.Count; i++)
            {
                if (s_open[i] != null) continue;
                s_open[i] = f;
                return i + 1;
            }
            s_open.Add(f);
            return s_open.Count;
        }

        // ---- reading ----

        private static int ReadLocked(OpenFile f, byte* dst, int cap)
        {
            uint cb = ClusterBytes;
            uint maxRun = (uint)BulkBytes / cb;
            if (maxRun == 0) maxRun = 1;
            int got = 0;
            while (got < cap && f.Position < f.Size && f.Cluster >= 2)
            {
                uint inCluster = f.Position % cb;
                uint run = 1;
                uint c = f.Cluster;
                uint next = FatNext(c);
                while (run < maxRun && next == c + 1)
                {
                    c = next;
                    run++;
                    next = FatNext(c);
                }

                uint skip = inCluster % s_bps;
                uint want = run * cb - inCluster;
                if (want > (uint)(cap - got)) want = (uint)(cap - got);
                if (want > f.Size - f.Position) want = f.Size - f.Position;
                uint sectors = (skip + want + s_bps - 1) / s_bps;
                if (!s_disk.Read(ClusterLba(f.Cluster) + inCluster / s_bps, sectors, s_bulk)) return got > 0 ? got : -1;
                System.Runtime.CompilerServices.Unsafe.CopyBlock(dst + got, s_bulk + skip, want);
                got += (int)want;
                f.Position += want;

                uint advance = (inCluster + want) / cb;
                f.Cluster = advance >= run ? next : f.Cluster + advance;
            }
            return got;
        }

        // ---- writing ----

        /// <summary>Why the last write or close failed, for the log.</summary>
        public static string LastFailure;

        // FSInfo keeps a free-cluster count and a next-free hint that tools
        // trust (mtools, Windows). Not kept up to date here: the first change
        // of the FAT after mounting marks both unknown (0xFFFFFFFF, as the
        // FAT32 specification allows), and the tools count again. A stale
        // count reported a full volume as 165 MB free.
        private static ulong s_fsInfoLba;
        private static bool s_fsInfoUnknown;

        private static void FreeCountUnknown()
        {
            if (s_fsInfoUnknown || s_fsInfoLba == 0) return;
            s_fsInfoUnknown = true;
            if (!ReadAbs(s_fsInfoLba)) return;
            if (RdU32(s_sec, 0) != 0x41615252u || RdU32(s_sec, 484) != 0x61417272u) return;
            for (int i = 488; i < 496; i++) s_sec[i] = 0xFF;
            s_disk.Write(s_fsInfoLba, 1, s_sec);
        }

        private static bool WriteLocked(OpenFile f, byte* src, int length)
        {
            uint cb = ClusterBytes;
            uint maxRun = (uint)BulkBytes / cb;
            if (maxRun == 0) maxRun = 1;
            while (length > 0)
            {
                if (f.RunFill == f.RunClusters * cb)
                {
                    // The run is full: one more cluster, contiguous when it can be.
                    if (f.RunClusters == maxRun && !FlushRun(f)) return false;
                    uint c = AllocateAfter(f.Last);
                    if (c == 0) return false;
                    if (!FatSetDeferred(c, FatEndOfChain | 0x7) || (f.Last != 0 && !FatSetDeferred(f.Last, c)))
                    {
                        LastFailure = "FAT sector read";
                        return false;
                    }
                    if (f.Last == 0) f.First = c;
                    if (f.RunClusters > 0 && c != f.RunStart + f.RunClusters && !FlushRun(f)) return false;
                    if (f.RunClusters == 0) f.RunStart = c;
                    f.RunClusters++;
                    f.Last = c;
                }
                uint room = f.RunClusters * cb - f.RunFill;
                uint n = (uint)length < room ? (uint)length : room;
                System.Runtime.CompilerServices.Unsafe.CopyBlock(f.Run + f.RunFill, src, n);
                f.RunFill += n;
                f.Size += n;
                src += n;
                length -= (int)n;
            }
            return true;
        }

        // The staging run to the disk, in one command; the tail of its last
        // cluster is zero. Empties the run.
        private static bool FlushRun(OpenFile f)
        {
            if (f.RunClusters == 0) return true;
            uint bytes = f.RunClusters * ClusterBytes;
            for (uint i = f.RunFill; i < bytes; i++) f.Run[i] = 0;
            System.Runtime.CompilerServices.Unsafe.CopyBlock(s_bulk, f.Run, bytes);
            bool ok = s_disk.Write(ClusterLba(f.RunStart), f.RunClusters * s_spc, s_bulk);
            if (!ok) LastFailure = "disk write of clusters " + f.RunStart.ToString() + "+" + f.RunClusters.ToString();
            // A partly filled last cluster stays in the run, to be filled on:
            // its bytes move to the front.
            uint tail = f.RunFill % ClusterBytes;
            if (tail != 0 && f.RunFill == bytes - ClusterBytes + tail)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlock(f.Run, f.Run + bytes - ClusterBytes, tail);
                f.RunStart = f.Last;
                f.RunClusters = 1;
                f.RunFill = tail;
            }
            else
            {
                f.RunClusters = 0;
                f.RunFill = 0;
            }
            return ok;
        }

        // Appending: find the last cluster; a partly filled one comes back
        // into the run so that writing continues inside it.
        private static bool PrepareAppend(OpenFile f, uint first, uint size)
        {
            uint cb = ClusterBytes;
            uint clusters = (size + cb - 1) / cb;
            uint c = first;
            for (uint i = 1; i < clusters; i++)
            {
                c = FatNext(c);
                if (c < 2) return false;
            }
            f.First = first;
            f.Size = size;
            f.Last = c;
            uint tail = size % cb;
            if (tail != 0)
            {
                if (!s_disk.Read(ClusterLba(c), s_spc, s_bulk)) return false;
                System.Runtime.CompilerServices.Unsafe.CopyBlock(f.Run, s_bulk, cb);
                f.RunStart = c;
                f.RunClusters = 1;
                f.RunFill = tail;
            }
            return true;
        }

        // A free cluster: the one after `previous` if free, else the first
        // free one after it, wrapping round. 0 when the volume is full.
        private static uint s_allocationHint = 2;

        private static uint AllocateAfter(uint previous)
        {
            uint total = s_clusterCount;
            uint start = previous >= 2 ? previous + 1 : s_allocationHint;
            if (start < 2 || start >= total) start = 2;
            uint c = start;
            for (uint scanned = 0; scanned < total; scanned++)
            {
                if (c >= total) c = 2;
                if (!FatGetRaw(c, out uint value))
                {
                    LastFailure = "FAT read at cluster " + c.ToString();
                    return 0;
                }
                if (value == 0)
                {
                    s_allocationHint = c + 1;
                    FreeCountUnknown();
                    return c;
                }
                c++;
            }
            LastFailure = "no free cluster among " + total.ToString() + " from " + start.ToString();
            return 0;
        }

        private static bool FatGetRaw(uint cluster, out uint value)
        {
            value = 0;
            ulong byteOffset = (ulong)cluster * 4UL;
            if (!ReadFatSector(s_fatLba + byteOffset / s_bps)) return false;
            value = RdU32(s_fatCache, (int)(byteOffset % s_bps)) & 0x0FFFFFFFu;
            return true;
        }

        // A FAT entry changed in the cached sector only; FlushFatCache writes it.
        private static bool FatSetDeferred(uint cluster, uint value)
        {
            ulong byteOffset = (ulong)cluster * 4UL;
            if (!ReadFatSector(s_fatLba + byteOffset / s_bps)) return false;
            uint offset = (uint)(byteOffset % s_bps);
            uint existing = RdU32(s_fatCache, (int)offset);
            uint merged = (existing & 0xF0000000u) | (value & 0x0FFFFFFFu);
            s_fatCache[offset] = (byte)merged;
            s_fatCache[offset + 1] = (byte)(merged >> 8);
            s_fatCache[offset + 2] = (byte)(merged >> 16);
            s_fatCache[offset + 3] = (byte)(merged >> 24);
            s_fatDirty = true;
            return true;
        }

        /// <summary>The cached FAT sector to every FAT copy, if it holds changes.</summary>
        private static bool FlushFatCache()
        {
            if (!s_fatDirty) return true;
            s_fatDirty = false;
            for (uint copy = 0; copy < s_numFats; copy++)
                if (!s_disk.Write(s_fatCachedLba + (ulong)copy * s_fatSectors, 1, s_fatCache))
                    return false;
            return true;
        }

        private static bool FreeChainDeferred(uint cluster)
        {
            FreeCountUnknown();
            uint guard = 0;
            while (cluster >= 2 && cluster < s_clusterCount && guard++ < 0x10000000u)
            {
                if (!FatGetRaw(cluster, out uint next)) return false;
                if (!FatSetDeferred(cluster, 0)) return false;
                if (cluster < s_allocationHint) s_allocationHint = cluster;
                if (next >= 0x0FFFFFF8u) break;
                cluster = next;
            }
            return true;
        }

        // The entry's first cluster and size, as the file now has them.
        private static bool UpdateEntry(OpenFile f)
        {
            if (f.EntryLba == 0) return false;
            if (!s_disk.Read(f.EntryLba, 1, s_sec)) return false;
            byte* e = s_sec + f.EntryOffset;
            e[20] = (byte)(f.First >> 16);
            e[21] = (byte)(f.First >> 24);
            e[26] = (byte)f.First;
            e[27] = (byte)(f.First >> 8);
            e[28] = (byte)f.Size;
            e[29] = (byte)(f.Size >> 8);
            e[30] = (byte)(f.Size >> 16);
            e[31] = (byte)(f.Size >> 24);
            return s_disk.Write(f.EntryLba, 1, s_sec);
        }
    }
}
