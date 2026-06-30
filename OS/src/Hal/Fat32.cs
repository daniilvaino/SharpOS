// Read-only FAT16/FAT32 reader, clean-room from the documented FAT
// on-disk format (Microsoft FAT spec; the on-disk layout is not
// copyrightable). Structure/algorithm cross-checked against
// DiscUtils.Fat (MIT, Copyright 2008-2011 Kenneth Bell) and ChaN
// FatFs ff.h (BSD-1-clause) for diligence — no code copied from
// either; this is an independent minimal implementation.
//
// Mount understands three on-disk container shapes:
//   1. superfloppy        — LBA0 is the BPB itself (QEMU VVFAT default).
//   2. legacy MBR         — 4 entries at LBA0+0x1BE, scan by starting LBA.
//   3. GPT                — "EFI PART" header at LBA1, walk entry array.
// GPT field offsets cross-checked against UEFI 2.10 §5.3 and
// DiscUtils.Core/Partitions/GptHeader.cs (MIT) — no code copied.
//
// Sits on OS.Hal.Disk. Disk.Read DMAs into the *physical* address of
// the destination pointer, so reads go through Ahci.AllocDma scratch
// buffers (virt==phys), never heap/VM-window pointers. Directory and
// FAT metadata use a sector scratch buffer; file data uses bounded
// cluster-run bulk reads.

using System.Runtime.CompilerServices;

namespace OS.Hal
{
    internal static unsafe class Fat32
    {
        private static Disk s_disk;
        private static byte* s_sec;          // DMA scratch, 1 sector
        // 128 sectors × 512 = 64 KiB per AHCI command: eight 8 KiB PRDT
        // entries. This keeps DMA tables small while cutting tiny-cluster
        // FAT32 images from ~23k commands for CoreLib to a few hundred
        // when the file is laid out contiguously.
        private const int BulkBytes = 64 * 1024;
        private static byte* s_bulk;         // DMA scratch, bulk data reads
        private static uint s_bps;           // bytes per sector
        private static uint s_spc;           // sectors per cluster
        private static ulong s_partLba;      // partition start LBA
        private static ulong s_fatLba;       // first FAT sector (abs)
        private static ulong s_rootLba;      // FAT16 root region (abs)
        private static uint s_rootEntCnt;    // FAT16 root entries
        private static ulong s_dataLba;      // first data sector (abs)
        private static uint s_rootClus;      // FAT32 root cluster
        private static bool s_isFat32;
        private static bool s_mounted;

        // FAT-sector cache: a single FAT sector covers 128 (FAT32) or 256
        // (FAT16) cluster entries. Reading a 23 MB file walks ~5750
        // sequential clusters but touches only ~45 distinct FAT sectors,
        // so caching the last one collapses the 5705 redundant AHCI
        // commands. s_fatCachedLba == ulong.MaxValue means "no cache".
        private static byte* s_fatCache;
        private static ulong s_fatCachedLba = ulong.MaxValue;

        public static bool Mounted => s_mounted;
        public static bool IsFat32 => s_isFat32;
        public static uint BytesPerSector => s_bps;
        public static uint SectorsPerCluster => s_spc;
        public static uint BulkReadBytes => BulkBytes;

        private static ushort RdU16(byte* p, int o) => (ushort)(p[o] | (p[o + 1] << 8));
        private static uint RdU32(byte* p, int o)
            => (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));
        private static ulong RdU64(byte* p, int o)
            => (ulong)RdU32(p, o) | ((ulong)RdU32(p, o + 4) << 32);

        private static bool ReadAbs(ulong lba)
            => s_disk != null && s_disk.Read(lba, 1, s_sec);

        // Diagnostics for a failed mount (the on-disk layout, so a
        // FAIL is actionable instead of opaque).
        public static byte DiagJmp0, DiagJmp1;
        public static ushort DiagBpsLba0;
        public static byte DiagPart0Type;
        public static uint DiagPart0Lba;
        public static int DiagPath;                  // 1=superfloppy 2=mbr 3=gpt 0=none
        public static uint DiagGptEntries;           // NumberOfPartitionEntries from GPT header
        public static ulong DiagGptFirstLba;         // FirstLba of first non-empty GPT entry

        // A sector is a plausible FAT BPB if bytes/sector is one of
        // 512/1024/2048/4096, sec/clus is a power of two 1..128, FAT
        // count is 1 or 2, and reserved sectors >= 1.
        private static bool LooksLikeBpb(byte* b)
        {
            ushort bps = RdU16(b, 11);
            byte spc = b[13];
            byte nf = b[16];
            ushort rsvd = RdU16(b, 14);
            if (bps != 512 && bps != 1024 && bps != 2048 && bps != 4096) return false;
            if (spc == 0 || (spc & (spc - 1)) != 0 || spc > 128) return false;
            if (nf != 1 && nf != 2) return false;
            if (rsvd < 1) return false;
            return true;
        }

        private static bool ParseBpbAt(ulong partLba)
        {
            if (!ReadAbs(partLba)) return false;
            byte* b = s_sec;
            if (!LooksLikeBpb(b)) return false;

            s_partLba = partLba;
            s_bps = RdU16(b, 11);
            s_spc = b[13];
            uint rsvd = RdU16(b, 14);
            uint numFats = b[16];
            s_rootEntCnt = RdU16(b, 17);
            uint totSec16 = RdU16(b, 19);
            uint fatSz16 = RdU16(b, 22);
            uint totSec32 = RdU32(b, 32);
            uint fatSz32 = RdU32(b, 36);

            uint fatSz = fatSz16 != 0 ? fatSz16 : fatSz32;
            uint totSec = totSec16 != 0 ? totSec16 : totSec32;
            uint rootDirSec = ((s_rootEntCnt * 32u) + (s_bps - 1)) / s_bps;
            uint firstData = rsvd + numFats * fatSz + rootDirSec;
            if (totSec <= firstData) return false;
            uint dataSec = totSec - firstData;
            uint clusters = dataSec / s_spc;

            s_isFat32 = clusters >= 65525;
            s_fatLba = partLba + rsvd;
            s_rootLba = partLba + rsvd + numFats * fatSz;
            s_dataLba = partLba + firstData;
            s_rootClus = s_isFat32 ? RdU32(b, 44) : 0;
            return true;
        }

        // Mount the boot FAT volume. Handles both layouts QEMU VVFAT
        // can present: a partitionless "superfloppy" (LBA0 == BPB) and
        // an MBR-partitioned disk. The MBR type byte is unreliable
        // across VVFAT versions, so partitions are accepted by a valid
        // BPB at their start LBA, not by type.
        public static bool Mount(Disk disk)
        {
            if (s_mounted) return true;
            s_disk = disk;
            if (disk == null) return false;
            s_sec = (byte*)Ahci.AllocDma(1);
            if (s_sec == null) return false;
            s_bulk = (byte*)Ahci.AllocDma((BulkBytes + 4095) / 4096);
            if (s_bulk == null) return false;
            s_fatCache = (byte*)Ahci.AllocDma(1);
            if (s_fatCache == null) return false;
            s_fatCachedLba = ulong.MaxValue;

            if (!ReadAbs(0)) return false;
            DiagJmp0 = s_sec[0]; DiagJmp1 = s_sec[1];
            DiagBpsLba0 = RdU16(s_sec, 11);
            DiagPath = 0;

            // 1. Superfloppy: LBA0 itself is a valid BPB.
            if (LooksLikeBpb(s_sec) && ParseBpbAt(0))
            { DiagPath = 1; s_mounted = true; return true; }

            // 2. MBR: re-read LBA0 (ParseBpbAt clobbered s_sec), take
            //    the first partition whose start LBA holds a valid BPB.
            if (!ReadAbs(0)) return false;
            if (s_sec[510] != 0x55 || s_sec[511] != 0xAA) return false;
            byte* e0 = s_sec + 0x1BE;
            DiagPart0Type = e0[4];
            DiagPart0Lba = RdU32(e0, 8);
            uint* starts = stackalloc uint[4];
            for (int i = 0; i < 4; i++)
                starts[i] = RdU32(s_sec + 0x1BE + i * 16, 8);
            for (int i = 0; i < 4; i++)
            {
                if (starts[i] == 0) continue;
                if (ParseBpbAt(starts[i]))
                { DiagPath = 2; s_mounted = true; return true; }
            }

            // 3. GPT: protective MBR carries a single 0xEE entry. The real
            //    table lives at LBA1 ("EFI PART" magic), entries start at
            //    PartitionEntryLba. Walk entries, skip empty ones (type
            //    GUID = all-zero), try ParseBpbAt(FirstLba) on each — the
            //    one with a valid BPB is our ESP. We deliberately do NOT
            //    filter by partition-type GUID; ParseBpbAt itself is the
            //    test, same as the MBR branch above.
            if (!ReadAbs(1)) return false;
            // "EFI PART" magic = 45 46 49 20 50 41 52 54
            if (s_sec[0] != 0x45 || s_sec[1] != 0x46 || s_sec[2] != 0x49 || s_sec[3] != 0x20 ||
                s_sec[4] != 0x50 || s_sec[5] != 0x41 || s_sec[6] != 0x52 || s_sec[7] != 0x54)
                return false;
            ulong entriesLba = RdU64(s_sec, 72);
            uint entryCount = RdU32(s_sec, 80);
            uint entrySize  = RdU32(s_sec, 84);
            DiagGptEntries = entryCount;
            // Sanity: typical layout is 128 entries × 128 bytes. Cap to
            // protect against a corrupt header turning into a billion-LBA
            // scan that would hang the boot.
            if (entrySize < 128 || entrySize > 512 || entryCount == 0 || entryCount > 256)
                return false;
            // GPT is defined on 512-byte LBAs for our targets (UEFI
            // 4Kn would change this, not in scope). s_bps belongs to
            // the FAT volume, not the partition table, so use 512.
            uint perSec = 512u / entrySize;
            if (perSec == 0) perSec = 1;
            for (uint i = 0; i < entryCount; i++)
            {
                ulong lba = entriesLba + (i / perSec);
                if (!ReadAbs(lba)) return false;
                byte* e = s_sec + (int)((i % perSec) * entrySize);
                // Skip empty: PartitionTypeGuid all-zero across 16 bytes.
                bool empty = true;
                for (int k = 0; k < 16; k++) if (e[k] != 0) { empty = false; break; }
                if (empty) continue;
                ulong firstLba = RdU64(e, 32);
                if (firstLba == 0) continue;
                if (DiagGptFirstLba == 0) DiagGptFirstLba = firstLba;
                if (ParseBpbAt(firstLba))
                { DiagPath = 3; s_mounted = true; return true; }
            }
            return false;
        }

        private static ulong ClusterLba(uint cluster)
            => s_dataLba + (ulong)(cluster - 2) * s_spc;

        // Read one FAT sector, satisfied from a 1-sector cache so a
        // sequential cluster walk doesn't re-issue 128 redundant AHCI
        // commands per FAT sector. Uses a dedicated DMA buffer so it
        // never collides with s_sec (which the caller may still be
        // looking at — e.g. directory walks).
        private static bool ReadFatSector(ulong lba)
        {
            if (lba == s_fatCachedLba) return true;
            if (!s_disk.Read(lba, 1, s_fatCache)) return false;
            s_fatCachedLba = lba;
            return true;
        }

        // Next cluster in the chain, or 0 at end / bad.
        private static uint FatNext(uint cluster)
        {
            if (s_isFat32)
            {
                ulong byteOff = (ulong)cluster * 4UL;
                ulong lba = s_fatLba + byteOff / s_bps;
                uint o = (uint)(byteOff % s_bps);
                if (!ReadFatSector(lba)) return 0;
                uint v = RdU32(s_fatCache, (int)o) & 0x0FFFFFFFu;
                return v >= 0x0FFFFFF8u ? 0 : v;
            }
            else
            {
                ulong byteOff = (ulong)cluster * 2UL;
                ulong lba = s_fatLba + byteOff / s_bps;
                uint o = (uint)(byteOff % s_bps);
                if (!ReadFatSector(lba)) return 0;
                uint v = RdU16(s_fatCache, (int)o);
                return v >= 0xFFF8u ? 0 : v;
            }
        }

        // NOTE: there is deliberately no Make83/Is83 here. A reader must
        // never fabricate an 8.3 key from a requested name — the writer's
        // ~N alias protocol is its private business and unknowable from a
        // long name, so a guessed key can alias the wrong file. FindIn
        // matches only against names actually stored on disk (the
        // reconstructed LFN and the verbatim 8.3 short field).

        // Find a directory entry named `comp` within the directory that
        // starts at `dirCluster` (0 => FAT16 fixed root). Fills first
        // cluster + size + isDir; returns false if not found.
        // ASCII case-insensitive equality of path[cs,cs+cl) vs the
        // reconstructed long name lfn[0,lfnLen).
        private static bool LongNameEq(string path, int cs, int cl, char* lfn, int lfnLen)
        {
            if (cl != lfnLen) return false;
            for (int i = 0; i < cl; i++)
            {
                char a = path[cs + i], b = lfn[i];
                if (a >= 'a' && a <= 'z') a = (char)(a - 32);
                if (b >= 'a' && b <= 'z') b = (char)(b - 32);
                if (a != b) return false;
            }
            return true;
        }

        // Pull the 13 UCS-2 chars of one LFN entry into lfn at
        // (ordinal-1)*13; track the high-water length (the run ends at
        // the first 0x0000 terminator).
        private static void LfnFrag(byte* ent, char* lfn, ref int lfnLen)
        {
            int ord = (ent[0] & 0x1F) - 1;
            if (ord < 0 || ord > 19) return;             // 20*13=260 cap
            int baseIdx = ord * 13;
            int* offs = stackalloc int[13]
            { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
            for (int k = 0; k < 13; k++)
            {
                ushort u = (ushort)(ent[offs[k]] | (ent[offs[k] + 1] << 8));
                if (u == 0x0000 || u == 0xFFFF) continue;   // pad/terminator
                int idx = baseIdx + k;
                if (idx >= 260) continue;
                lfn[idx] = (char)u;
                if (idx + 1 > lfnLen) lfnLen = idx + 1;
            }
        }

        private static bool FindIn(uint dirCluster, string path, int cs, int cl,
            out uint firstClus, out uint size, out bool isDir)
        {
            firstClus = 0; size = 0; isDir = false;
            char* lfn = stackalloc char[260];
            int lfnLen = 0;
            char* n83 = stackalloc char[13];   // decoded stored 8.3 ("NAME.EXT")

            bool fat16Root = !s_isFat32 && dirCluster == 0;
            uint cluster = fat16Root ? 0 : (dirCluster == 0 ? s_rootClus : dirCluster);
            uint rootSecs = fat16Root
                ? ((s_rootEntCnt * 32u) + (s_bps - 1)) / s_bps : 0;
            uint secInRoot = 0;

            while (true)
            {
                uint secsThis = fat16Root ? rootSecs : s_spc;
                for (uint si = 0; si < secsThis; si++)
                {
                    ulong lba = fat16Root
                        ? s_rootLba + secInRoot + si
                        : ClusterLba(cluster) + si;
                    if (!ReadAbs(lba)) return false;
                    for (uint off = 0; off < s_bps; off += 32)
                    {
                        byte* ent = s_sec + off;
                        if (ent[0] == 0x00) return false;       // end of dir
                        if (ent[0] == 0xE5) { lfnLen = 0; continue; }
                        if ((ent[11] & 0x0F) == 0x0F)           // LFN fragment
                        {
                            LfnFrag(ent, lfn, ref lfnLen);
                            continue;
                        }
                        if ((ent[11] & 0x08) != 0) { lfnLen = 0; continue; } // vol label
                        // Collision-impossible match: compare the request
                        // ONLY against names this entry actually stores on
                        // disk — its reconstructed LFN (if any) and its
                        // verbatim 8.3 short field. No fabricated key, no
                        // lossy prefilter, so a wrong file can never alias
                        // a right one regardless of what a writer chose for
                        // its ~N alias. Both compares are case-insensitive.
                        bool hit = lfnLen > 0 && LongNameEq(path, cs, cl, lfn, lfnLen);
                        if (!hit)
                        {
                            int n83Len = Name83Out(ent, n83, 13);
                            hit = LongNameEq(path, cs, cl, n83, n83Len);
                        }
                        lfnLen = 0;
                        if (hit)
                        {
                            firstClus = ((uint)RdU16(ent, 20) << 16) | RdU16(ent, 26);
                            size = RdU32(ent, 28);
                            isDir = (ent[11] & 0x10) != 0;
                            return true;
                        }
                    }
                }
                if (fat16Root)
                {
                    secInRoot += rootSecs;
                    if (secInRoot >= rootSecs) return false;    // single pass
                }
                else
                {
                    cluster = FatNext(cluster);
                    if (cluster == 0) return false;
                }
            }
        }

        // Resolve a '/'- or '\'-separated path from the root, copy up to
        // `cap` bytes of the file into `dst` (DMA scratch reused per
        // sector → dst must tolerate partial). Returns bytes copied,
        // -1 on not-found.
        // Walk a '/'- or '\\'-separated path from the root to its final
        // component. Returns false if any component is missing (or a
        // non-final component isn't a directory).
        private static bool Resolve(string path, out uint clus, out uint size, out bool isDir)
        {
            clus = 0; size = 0; isDir = false;
            if (!s_mounted) return false;
            uint dirClus = 0;                  // root
            int start = 0, n = path.Length;
            bool any = false;
            while (start < n)
            {
                while (start < n && (path[start] == '/' || path[start] == '\\')) start++;
                int end = start;
                while (end < n && path[end] != '/' && path[end] != '\\') end++;
                if (end == start) break;
                if (!FindIn(dirClus, path, start, end - start,
                        out clus, out size, out isDir)) return false;
                any = true;
                start = end;
                if (start < n) { if (!isDir) return false; dirClus = clus; }
            }
            return any;
        }

        public static bool Exists(string path)
            => Resolve(path, out _, out _, out _);

        // Emit a trimmed 8.3 name ("NAME.EXT") into out[].
        private static int Name83Out(byte* ent, char* outp, uint cap)
        {
            int nl = 8; while (nl > 0 && ent[nl - 1] == (byte)' ') nl--;
            int el = 3; while (el > 0 && ent[8 + el - 1] == (byte)' ') el--;
            int o = 0;
            for (int i = 0; i < nl && o < cap; i++) outp[o++] = (char)ent[i];
            if (el > 0 && o < cap)
            {
                outp[o++] = '.';
                for (int i = 0; i < el && o < cap; i++) outp[o++] = (char)ent[8 + i];
            }
            return o;
        }

        // Directory enumeration: the `index`-th real entry of the dir
        // at `path` ("" / "/" = root). Writes the name (LFN if present,
        // else 8.3) into nameOut, sets attrs (0x10 = directory).
        // Returns false past the end / bad path.
        public static bool EnumDir(string path, uint index,
            char* nameOut, uint nameCap, out uint nameLen, out ulong attrs)
        {
            nameLen = 0; attrs = 0;
            if (!s_mounted) return false;

            uint dirCluster;
            bool fat16Root;
            // Trim leading separators; empty => root.
            int s0 = 0; while (s0 < path.Length &&
                (path[s0] == '/' || path[s0] == '\\')) s0++;
            if (s0 >= path.Length)
            {
                fat16Root = !s_isFat32;
                dirCluster = s_isFat32 ? s_rootClus : 0;
            }
            else
            {
                if (!Resolve(path, out uint dc, out _, out bool isD) || !isD)
                    return false;
                fat16Root = false;
                dirCluster = dc;
            }

            char* lfn = stackalloc char[260];
            int lfnLen = 0;
            uint seen = 0;
            uint cluster = dirCluster;
            uint rootSecs = fat16Root
                ? ((s_rootEntCnt * 32u) + (s_bps - 1)) / s_bps : 0;
            uint secInRoot = 0;

            while (true)
            {
                uint secsThis = fat16Root ? rootSecs : s_spc;
                for (uint si = 0; si < secsThis; si++)
                {
                    ulong lba = fat16Root
                        ? s_rootLba + secInRoot + si
                        : ClusterLba(cluster) + si;
                    if (!ReadAbs(lba)) return false;
                    for (uint off = 0; off < s_bps; off += 32)
                    {
                        byte* ent = s_sec + off;
                        if (ent[0] == 0x00) return false;        // end of dir
                        if (ent[0] == 0xE5) { lfnLen = 0; continue; }
                        if ((ent[11] & 0x0F) == 0x0F)
                        { LfnFrag(ent, lfn, ref lfnLen); continue; }
                        if ((ent[11] & 0x08) != 0) { lfnLen = 0; continue; }
                        if (seen == index)
                        {
                            int o;
                            if (lfnLen > 0)
                            {
                                o = lfnLen > (int)nameCap ? (int)nameCap : lfnLen;
                                for (int i = 0; i < o; i++) nameOut[i] = lfn[i];
                            }
                            else o = Name83Out(ent, nameOut, nameCap);
                            nameLen = (uint)o;
                            // FAT directory entry: size in bytes at offset 28 (LE).
                            // Pack into upper 32 bits of attrs so callers that
                            // care (NtQueryDirectoryFile / GetFileInformationByHandleEx)
                            // get size without an interface change; callers that
                            // only check the dir bit (& 0x10) are unaffected.
                            uint size = (uint)ent[28]
                                      | ((uint)ent[29] << 8)
                                      | ((uint)ent[30] << 16)
                                      | ((uint)ent[31] << 24);
                            attrs = (ulong)(ent[11] & 0x10) | ((ulong)size << 32);
                            return true;
                        }
                        seen++;
                        lfnLen = 0;
                    }
                }
                if (fat16Root)
                {
                    secInRoot += rootSecs;
                    return false;                                // single pass
                }
                cluster = FatNext(cluster);
                if (cluster == 0) return false;
            }
        }

        public static int ReadFile(string path, byte* dst, int cap, out uint fileSize)
        {
            fileSize = 0;
            if (!Resolve(path, out uint clus, out uint size, out bool isDir) || isDir)
                return -1;
            fileSize = size;

            // Bulk: coalesce a physically-contiguous FAT cluster run, then
            // read up to BulkBytes per AHCI command. This avoids issuing
            // one command per tiny FAT32 cluster on the default ESP image.
            uint maxChunk = (uint)(BulkBytes / s_bps);
            if (maxChunk == 0) maxChunk = 1;
            uint maxRunClusters = maxChunk / s_spc;
            if (maxRunClusters == 0) maxRunClusters = 1;

            int copied = 0;
            uint cluster = clus;
            uint remain = size;
            while (cluster != 0 && copied < cap && remain > 0)
            {
                uint runClusters = 1;
                uint lastCluster = cluster;
                uint nextCluster = FatNext(lastCluster);
                while (runClusters < maxRunClusters &&
                       nextCluster != 0 &&
                       nextCluster == lastCluster + 1)
                {
                    lastCluster = nextCluster;
                    runClusters++;
                    nextCluster = FatNext(lastCluster);
                }

                uint runSectors = runClusters * s_spc;
                uint byteBudget = remain;
                uint capLeft = (uint)(cap - copied);
                if (byteBudget > capLeft) byteBudget = capLeft;
                uint sectorsNeeded = (byteBudget + s_bps - 1) / s_bps;
                if (runSectors > sectorsNeeded) runSectors = sectorsNeeded;

                uint si = 0;
                while (si < runSectors && copied < cap && remain > 0)
                {
                    uint chunk = runSectors - si;
                    if (chunk > maxChunk) chunk = maxChunk;
                    if (!s_disk.Read(ClusterLba(cluster) + si, chunk, s_bulk))
                        return copied;
                    ulong bytes = (ulong)chunk * s_bps;
                    ulong take = bytes;
                    if (take > (ulong)(cap - copied)) take = (ulong)(cap - copied);
                    if (take > remain) take = remain;
                    Unsafe.CopyBlock(dst + copied, s_bulk, (uint)take);
                    copied += (int)take;
                    remain -= (uint)take;
                    si += chunk;
                }
                cluster = nextCluster;
            }
            return copied;
        }
    }
}
