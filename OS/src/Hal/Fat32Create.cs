namespace OS.Hal
{
    // Creating files: allocate a cluster chain, then publish a directory entry.
    //
    // Short names only, deliberately. The reader refuses to fabricate an 8.3
    // key from a long name, because the ~N alias protocol is the writer's
    // private business — so a writer that invented one could produce a file
    // its own reader cannot find, or worse, alias someone else's. Storing a
    // real 8.3 name keeps both halves honest; a name that will not fit is
    // rejected rather than mangled.
    internal static unsafe partial class Fat32
    {
        private const byte AttrDirectory = 0x10;
        private const byte AttrArchive = 0x20;
        private const uint FatEndOfChain = 0x0FFFFFF8u;

        /// <summary>
        /// Create an empty file of <paramref name="sizeBytes"/>, with its
        /// clusters allocated and zeroed. Fails if the name is not 8.3, the
        /// file already exists, or the volume has no room.
        /// </summary>
        public static bool TryCreateFile(string path, uint sizeBytes)
        {
            if (!s_mounted || s_disk == null || !s_isFat32) return false;
            if (Exists(path)) return false;

            if (!TrySplitParent(path, out uint parentCluster, out int nameStart, out int nameLength))
                return false;

            byte* name83 = stackalloc byte[11];
            if (!TryMake83(path, nameStart, nameLength, name83)) return false;

            uint clusterBytes = s_bps * s_spc;
            uint needed = (sizeBytes + clusterBytes - 1) / clusterBytes;
            if (needed == 0) needed = 1;

            if (!TryAllocateChain(needed, out uint firstCluster)) return false;

            if (!TryWriteDirectoryEntry(parentCluster, name83, firstCluster, sizeBytes))
            {
                FreeChain(firstCluster);
                return false;
            }
            return true;
        }

        // Walk every component but the last, which is the name to create.
        private static bool TrySplitParent(string path, out uint parentCluster,
                                           out int nameStart, out int nameLength)
        {
            parentCluster = 0;          // root
            nameStart = 0; nameLength = 0;

            int n = path.Length;
            int start = 0;
            while (start < n)
            {
                while (start < n && (path[start] == '/' || path[start] == '\\')) start++;
                int end = start;
                while (end < n && path[end] != '/' && path[end] != '\\') end++;
                if (end == start) break;

                // Is there another component after this one?
                int probe = end;
                while (probe < n && (path[probe] == '/' || path[probe] == '\\')) probe++;

                if (probe >= n)
                {
                    nameStart = start;
                    nameLength = end - start;
                    return nameLength > 0;
                }

                if (!FindIn(parentCluster, path, start, end - start,
                            out uint clus, out _, out bool isDir) || !isDir)
                    return false;
                parentCluster = clus;
                start = end;
            }
            return false;
        }

        // Pack "NAME.EXT" into the 11-byte on-disk field. Anything that does
        // not fit is refused: silently truncating would create a file under a
        // name the caller never asked for.
        private static bool TryMake83(string path, int start, int length, byte* out83)
        {
            for (int i = 0; i < 11; i++) out83[i] = 0x20;

            int dot = -1;
            for (int i = 0; i < length; i++)
                if (path[start + i] == '.') { dot = i; break; }

            int baseLen = dot < 0 ? length : dot;
            int extLen = dot < 0 ? 0 : length - dot - 1;
            if (baseLen == 0 || baseLen > 8 || extLen > 3) return false;

            for (int i = 0; i < baseLen; i++)
            {
                if (!TryUpper(path[start + i], out byte c)) return false;
                out83[i] = c;
            }
            for (int i = 0; i < extLen; i++)
            {
                if (!TryUpper(path[start + dot + 1 + i], out byte c)) return false;
                out83[8 + i] = c;
            }
            return true;
        }

        private static bool TryUpper(char ch, out byte value)
        {
            value = 0;
            if (ch >= 'a' && ch <= 'z') ch = (char)(ch - 32);
            bool ok = (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                      || ch == '_' || ch == '-' || ch == '~';
            if (!ok) return false;
            value = (byte)ch;
            return true;
        }

        // ---- FAT ----

        private static bool TryReadWriteFat(uint cluster, bool write, ref uint value)
        {
            ulong byteOffset = (ulong)cluster * 4UL;
            ulong lba = s_fatLba + byteOffset / s_bps;
            uint offset = (uint)(byteOffset % s_bps);

            // Honour the cache on reads. It was only ever set here, never
            // checked, so a scan over the table re-read the same sector once
            // per cluster — on a USB stick that is millions of round trips,
            // and it looks like a hang rather than a slow loop.
            if (lba != s_fatCachedLba)
            {
                if (!s_disk.Read(lba, 1, s_fatCache)) return false;
                s_fatCachedLba = lba;
            }

            if (!write)
            {
                value = RdU32(s_fatCache, (int)offset) & 0x0FFFFFFFu;
                return true;
            }

            // The top four bits are reserved and must be preserved, not
            // overwritten with our value.
            uint existing = RdU32(s_fatCache, (int)offset);
            uint merged = (existing & 0xF0000000u) | (value & 0x0FFFFFFFu);
            s_fatCache[offset] = (byte)merged;
            s_fatCache[offset + 1] = (byte)(merged >> 8);
            s_fatCache[offset + 2] = (byte)(merged >> 16);
            s_fatCache[offset + 3] = (byte)(merged >> 24);

            // Mirror into every FAT copy. Only the first is ever read back
            // here, but leaving the others stale is exactly the inconsistency
            // a host fsck reports after we hand the stick over.
            for (uint copy = 0; copy < s_numFats; copy++)
            {
                ulong target = lba + (ulong)copy * s_fatSectors;
                if (!s_disk.Write(target, 1, s_fatCache))
                {
                    s_fatCachedLba = ulong.MaxValue;
                    return false;
                }
            }
            return true;
        }

        // Prefer a contiguous run, fall back to whatever is free.
        //
        // Contiguity is not a nicety for the boot log: it is read back as a
        // flat range of sectors, and a fragmented file is refused outright
        // rather than risk writing into somebody else's clusters. On a stick
        // that has been used, first-fit picks scattered clusters and the log
        // silently never starts — which is exactly what happened.
        // How many clusters a search will look at. With the FAT sector cache
        // in play this is ~1 disk read per 128 clusters, so a quarter million
        // clusters is a few thousand reads — bounded, and far more space than
        // any file created here needs.
        private const uint ScanLimit = 256 * 1024;

        private static bool TryAllocateChain(uint count, out uint firstCluster)
        {
            if (TryAllocateContiguous(count, out firstCluster)) return true;
            return TryAllocateScattered(count, out firstCluster);
        }

        private static bool TryAllocateContiguous(uint count, out uint firstCluster)
        {
            firstCluster = 0;
            uint total = TotalClusters();
            uint runStart = 0;
            uint run = 0;

            // Bounded: a big volume has millions of clusters, and a log file
            // is not worth walking all of them on a slow medium. Give up and
            // let the scattered path try instead.
            uint limit = total < ScanLimit ? total : ScanLimit;

            for (uint cluster = 2; cluster < limit; cluster++)
            {
                uint value = 0;
                if (!TryReadWriteFat(cluster, false, ref value)) return false;

                if (value != 0) { run = 0; continue; }
                if (run == 0) runStart = cluster;
                run++;
                if (run < count) continue;

                // Link the run and terminate it.
                for (uint i = 0; i < count; i++)
                {
                    uint c = runStart + i;
                    uint link = i + 1 < count ? c + 1 : (FatEndOfChain | 0x7);
                    if (!TryReadWriteFat(c, true, ref link)) return false;
                    if (!TryZeroCluster(c)) return false;
                }

                firstCluster = runStart;
                return true;
            }
            return false;
        }

        private static bool TryAllocateScattered(uint count, out uint firstCluster)
        {
            firstCluster = 0;
            uint previous = 0;
            uint allocated = 0;

            // Cluster 2 is the first usable one; the two below it are reserved.
            uint totalClusters = TotalClusters();
            if (totalClusters > ScanLimit) totalClusters = ScanLimit;
            for (uint cluster = 2; cluster < totalClusters && allocated < count; cluster++)
            {
                uint value = 0;
                if (!TryReadWriteFat(cluster, false, ref value)) return false;
                if (value != 0) continue;

                uint marker = FatEndOfChain | 0x7;      // 0x0FFFFFFF
                if (!TryReadWriteFat(cluster, true, ref marker)) return false;

                if (previous != 0)
                {
                    uint link = cluster;
                    if (!TryReadWriteFat(previous, true, ref link)) return false;
                }
                else
                {
                    firstCluster = cluster;
                }

                if (!TryZeroCluster(cluster)) return false;

                previous = cluster;
                allocated++;
            }

            if (allocated < count)
            {
                if (firstCluster != 0) FreeChain(firstCluster);
                return false;
            }
            return true;
        }

        private static void FreeChain(uint cluster)
        {
            uint guard = 0;
            while (cluster >= 2 && guard++ < 1_000_000)
            {
                uint next = FatNext(cluster);
                uint zero = 0;
                if (!TryReadWriteFat(cluster, true, ref zero)) return;
                cluster = next;
            }
        }

        private static uint TotalClusters() => s_clusterCount;

        private static bool TryZeroCluster(uint cluster)
        {
            for (uint i = 0; i < s_bps; i++) s_sec[i] = 0;
            ulong lba = ClusterLba(cluster);
            for (uint s = 0; s < s_spc; s++)
                if (!s_disk.Write(lba + s, 1, s_sec)) return false;
            return true;
        }

        // ---- directory ----

        private static bool TryWriteDirectoryEntry(uint dirCluster, byte* name83,
                                                   uint firstCluster, uint size)
        {
            uint cluster = dirCluster == 0 ? s_rootClus : dirCluster;
            uint guard = 0;

            while (cluster >= 2 && guard++ < 1_000_000)
            {
                ulong lba = ClusterLba(cluster);
                for (uint s = 0; s < s_spc; s++)
                {
                    if (!s_disk.Read(lba + s, 1, s_sec)) return false;

                    for (uint offset = 0; offset < s_bps; offset += 32)
                    {
                        byte first = s_sec[offset];
                        if (first != 0x00 && first != 0xE5) continue;

                        FillEntry(s_sec + offset, name83, firstCluster, size);
                        return s_disk.Write(lba + s, 1, s_sec);
                    }
                }

                uint next = FatNext(cluster);
                if (next == 0) break;      // no room and no more clusters
                cluster = next;
            }

            // Growing the directory would mean allocating and chaining another
            // cluster into it; not needed while directories have free slots,
            // and pretending otherwise would silently drop the file.
            return false;
        }

        // FAT packs a date as year-since-1980 / month / day and a time as
        // hours / minutes / two-second units.
        private static void PackTimestamp(out ushort date, out ushort time)
        {
            date = (1 << 5) | 1;      // 1980-01-01
            time = 0;

            if (!Rtc.TryRead(out Rtc.Snapshot now)) return;
            if (now.Year < 1980 || now.Month < 1 || now.Month > 12
                || now.Day < 1 || now.Day > 31) return;

            date = (ushort)(((now.Year - 1980) << 9) | (now.Month << 5) | now.Day);
            time = (ushort)((now.Hour << 11) | (now.Minute << 5) | (now.Second / 2));
        }

        private static void FillEntry(byte* entry, byte* name83, uint firstCluster, uint size)
        {
            for (int i = 0; i < 32; i++) entry[i] = 0;
            for (int i = 0; i < 11; i++) entry[i] = name83[i];

            entry[11] = AttrArchive;

            // A zeroed date field means month 0 / day 0, which FAT does not
            // allow and a host fsck flags. Use the RTC when it is up, and
            // 1980-01-01 (the epoch of this format) when it is not.
            PackTimestamp(out ushort date, out ushort time);
            entry[14] = (byte)time; entry[15] = (byte)(time >> 8);    // creation
            entry[16] = (byte)date; entry[17] = (byte)(date >> 8);
            entry[18] = (byte)date; entry[19] = (byte)(date >> 8);    // last access
            entry[22] = (byte)time; entry[23] = (byte)(time >> 8);    // modified
            entry[24] = (byte)date; entry[25] = (byte)(date >> 8);
            entry[20] = (byte)(firstCluster >> 16);
            entry[21] = (byte)(firstCluster >> 24);
            entry[26] = (byte)firstCluster;
            entry[27] = (byte)(firstCluster >> 8);
            entry[28] = (byte)size;
            entry[29] = (byte)(size >> 8);
            entry[30] = (byte)(size >> 16);
            entry[31] = (byte)(size >> 24);
        }
    }
}
