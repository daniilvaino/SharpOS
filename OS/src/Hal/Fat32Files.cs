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

        /// <summary>
        /// step208: read and written where the position is — the content kept,
        /// the file made when there is none. What FileStream's Open and
        /// OpenOrCreate with write access take.
        /// </summary>
        public const int OpenUpdate = 3;

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

            // step208: where the next write (and, for a writer, read) goes —
            // past the end, the gap fills with zeros; and whether the run
            // holds bytes the disk does not have yet.
            public uint WritePos;
            public bool RunDirty;
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
                if (f == null) return -1;
                return f.Writing ? ReadWriterLocked(f, dst, cap) : ReadLocked(f, dst, cap);
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
                FileChanged(f.First);
                return WriteAtLocked(f, src, length);
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
                FileChanged(f.First);
                bool ok = FlushRun(f);
                ok &= FlushFatCache();
                ok &= UpdateEntry(f);
                if (f.Run != null) OS.Kernel.Memory.NativeArena.FreeLarge(f.Run, BulkBytes);
                f.Run = null;
                // What the program wrote, to the medium (step208).
                FlushDevice();
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

            if (mode < OpenRead || mode > OpenUpdate) return -2;
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
            if (exists) FileChanged(first);

            if (exists)
            {
                f.EntryLba = s_foundEntryLba;
                f.EntryOffset = s_foundEntryOffset;
                if ((mode == OpenAppend || mode == OpenUpdate) && size != 0 && first >= 2)
                {
                    if (!PrepareAppend(f, first, size)) return Fail(f, -4);
                    f.WritePos = mode == OpenAppend ? size : 0;
                }
                else if (mode == OpenUpdate)
                {
                    f.EntryLba = s_foundEntryLba;   // empty: nothing to keep, nothing to cut
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
                if (TryMake83(path, nameStart, nameLength, name83, out byte caseFlags))
                {
                    if (!TryWriteDirectoryEntry(parentCluster, name83, 0, 0, out f.EntryLba, out f.EntryOffset, AttrArchive, caseFlags))
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

        /// <summary>
        /// Moves the handle's position (step208); the new position, or -1. A
        /// writer may go past the end — the next write fills the gap with zeros.
        /// </summary>
        public static long Seek(int handle, long position)
        {
            Enter();
            try
            {
                OpenFile f = Find(handle);
                if (f == null || position < 0 || position > uint.MaxValue) return -1;
                if (f.Writing) { f.WritePos = (uint)position; return position; }
                f.Position = (uint)position;
                f.Cluster = position >= f.Size ? 0 : ClusterAt(f.First, (uint)position);
                return position;
            }
            finally { Leave(); }
        }

        /// <summary>The handle's position: where the next read or write goes.</summary>
        public static long PositionOf(int handle)
        {
            Enter();
            try { OpenFile f = Find(handle); return f == null ? -1 : f.Writing ? f.WritePos : f.Position; }
            finally { Leave(); }
        }

        /// <summary>
        /// A writer's file cut or grown to <paramref name="length"/> bytes
        /// (step208): cut, its clusters past the length go back; grown, the new
        /// bytes are zeros. The position stays where it was.
        /// </summary>
        public static bool SetLength(int handle, long length)
        {
            Enter();
            try
            {
                OpenFile f = Find(handle);
                if (f == null || !f.Writing || length < 0 || length > uint.MaxValue) return false;
                FileChanged(f.First);
                return SetLengthLocked(f, (uint)length);
            }
            finally { Leave(); }
        }

        // The cluster that holds byte `position` of a chain; 0 past its end.
        private static uint ClusterAt(uint first, uint position)
        {
            uint c = first;
            for (uint i = position / ClusterBytes; i > 0 && c >= 2; i--) c = FatNext(c);
            return c >= 2 ? c : 0;
        }

        // A write at the position: past the end the gap first fills with
        // zeros; at the end it is an append (the staging run); inside, the
        // clusters it covers are rewritten on the disk, and the run — which
        // may hold a copy of the last one — is loaded again.
        private static bool WriteAtLocked(OpenFile f, byte* src, int length)
        {
            if (length <= 0) return true;
            if (f.WritePos > f.Size && !Grow(f, f.WritePos)) return false;
            if (f.WritePos == f.Size)
            {
                bool ok = WriteLocked(f, src, length);
                f.WritePos = f.Size;
                return ok;
            }
            uint inside = (uint)length < f.Size - f.WritePos ? (uint)length : f.Size - f.WritePos;
            if (!FlushRun(f)) return false;
            if (!Overwrite(f.First, f.WritePos, src, inside)) return false;
            f.WritePos += inside;
            if (!PrepareAppend(f, f.First, f.Size)) return false;
            if ((uint)length == inside) return true;
            bool rest = WriteLocked(f, src + inside, length - (int)inside);
            f.WritePos = f.Size;
            return rest;
        }

        // Zeros from the end of the file to `size`, through the staging run.
        private static bool Grow(OpenFile f, uint size)
        {
            byte* zeros = stackalloc byte[512];
            for (int i = 0; i < 512; i++) zeros[i] = 0;
            while (f.Size < size)
            {
                uint n = size - f.Size < 512 ? size - f.Size : 512;
                if (!WriteLocked(f, zeros, (int)n)) return false;
            }
            return true;
        }

        // Bytes rewritten where they lie, a cluster at a time (read, change,
        // write back).
        private static bool Overwrite(uint first, uint position, byte* src, uint count)
        {
            uint cb = ClusterBytes;
            uint cluster = ClusterAt(first, position);
            while (count > 0)
            {
                if (cluster < 2) { LastFailure = "a write inside the file past its chain"; return false; }
                uint inCluster = position % cb;
                uint n = cb - inCluster < count ? cb - inCluster : count;
                ulong lba = ClusterLba(cluster);
                if (!s_disk.Read(lba, s_spc, s_bulk)) return false;
                System.Runtime.CompilerServices.Unsafe.CopyBlock(s_bulk + inCluster, src, n);
                if (!s_disk.Write(lba, s_spc, s_bulk)) { LastFailure = "disk write of cluster " + cluster.ToString(); return false; }
                src += n;
                position += n;
                count -= n;
                cluster = FatNext(cluster);
            }
            return true;
        }

        // Reading a writer's file: what the run holds goes to the disk first,
        // then a read at the position as for a reader.
        private static int ReadWriterLocked(OpenFile f, byte* dst, int cap)
        {
            if (!FlushRun(f)) return -1;
            f.Position = f.WritePos;
            f.Cluster = f.WritePos >= f.Size ? 0 : ClusterAt(f.First, f.WritePos);
            int got = ReadLocked(f, dst, cap);
            if (got > 0) f.WritePos += (uint)got;
            return got;
        }

        private static bool SetLengthLocked(OpenFile f, uint length)
        {
            if (!FlushRun(f)) return false;
            if (length == f.Size) return true;
            if (length > f.Size)
            {
                uint position = f.WritePos;
                bool grown = Grow(f, length);
                f.WritePos = position;
                return grown && FlushRun(f) && FlushFatCache() && UpdateEntry(f);
            }

            uint cb = ClusterBytes;
            if (length == 0)
            {
                if (f.First >= 2 && !FreeChainDeferred(f.First)) return false;
                f.First = 0;
                f.Last = 0;
            }
            else
            {
                uint keep = (length + cb - 1) / cb;
                uint last = ClusterAt(f.First, (keep - 1) * cb);
                if (last < 2) return false;
                uint next = FatNext(last);
                if (!FatSetDeferred(last, FatEndOfChain | 0x7)) return false;
                if (next >= 2 && !FreeChainDeferred(next)) return false;
                f.Last = last;
                // The last cluster's bytes past the new end become zeros: a
                // file grown again later must read zeros there, not what was cut.
                uint tail = length % cb;
                if (tail != 0)
                {
                    ulong lba = ClusterLba(last);
                    if (!s_disk.Read(lba, s_spc, s_bulk)) return false;
                    for (uint i = tail; i < cb; i++) s_bulk[i] = 0;
                    if (!s_disk.Write(lba, s_spc, s_bulk)) return false;
                }
            }
            f.Size = length;
            f.RunClusters = 0;
            f.RunFill = 0;
            f.RunDirty = false;
            if (length != 0 && !PrepareAppend(f, f.First, f.Size)) return false;
            return FlushFatCache() && UpdateEntry(f);
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
        // trust (mtools, Windows). Kept since step208 — Fat32FsInfo.cs; a
        // stale count once reported a full volume as 165 MB free.
        private static ulong s_fsInfoLba;

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
                f.RunDirty = true;
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
            // A run loaded to be written on (PrepareAppend) and not written to
            // is what the disk has already.
            if (!f.RunDirty) return true;
            f.RunDirty = false;
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
            f.RunClusters = 0;
            f.RunFill = 0;
            f.RunDirty = false;
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
            FatEntryChanged(existing & 0x0FFFFFFFu, value & 0x0FFFFFFFu);
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

        // The entry's first cluster and size, as the file now has them, and
        // the time it was written (step208: it kept its creation time).
        private static bool UpdateEntry(OpenFile f)
        {
            if (f.EntryLba == 0) return false;
            if (!s_disk.Read(f.EntryLba, 1, s_sec)) return false;
            byte* e = s_sec + f.EntryOffset;
            PackTimestamp(out ushort date, out ushort time);
            e[18] = (byte)date; e[19] = (byte)(date >> 8);    // last access
            e[22] = (byte)time; e[23] = (byte)(time >> 8);    // modified
            e[24] = (byte)date; e[25] = (byte)(date >> 8);
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
