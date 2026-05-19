// Read-only FAT16/FAT32 reader, clean-room from the documented FAT
// on-disk format (Microsoft FAT spec; the on-disk layout is not
// copyrightable). Structure/algorithm cross-checked against
// DiscUtils.Fat (MIT, Copyright 2008-2011 Kenneth Bell) and ChaN
// FatFs ff.h (BSD-1-clause) for diligence — no code copied from
// either; this is an independent minimal implementation.
//
// Sits on OS.Hal.Disk. Disk.Read DMAs into the *physical* address of
// the destination pointer, so all reads go through an Ahci.AllocDma
// scratch sector (virt==phys), never a heap/VM-window pointer. One
// sector at a time — simple and slow, fine for boot file access.
// 8.3 names only (the paths we need — \EFI\BOOT\BOOTX64.EFI etc. —
// are 8.3; LFN deferred).

namespace OS.Hal
{
    internal static unsafe class Fat32
    {
        private static Disk s_disk;
        private static byte* s_sec;          // DMA scratch, 1 sector
        private const int BulkBytes = 8192;  // 16x512 — single-PRDT path
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

        public static bool Mounted => s_mounted;
        public static bool IsFat32 => s_isFat32;

        private static ushort RdU16(byte* p, int o) => (ushort)(p[o] | (p[o + 1] << 8));
        private static uint RdU32(byte* p, int o)
            => (uint)(p[o] | (p[o + 1] << 8) | (p[o + 2] << 16) | (p[o + 3] << 24));

        private static bool ReadAbs(ulong lba)
            => s_disk != null && s_disk.Read(lba, 1, s_sec);

        // Diagnostics for a failed mount (the on-disk layout, so a
        // FAIL is actionable instead of opaque).
        public static byte DiagJmp0, DiagJmp1;
        public static ushort DiagBpsLba0;
        public static byte DiagPart0Type;
        public static uint DiagPart0Lba;
        public static int DiagPath;                  // 1=superfloppy 2=mbr 0=none

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
            s_bulk = (byte*)Ahci.AllocDma(BulkBytes / 4096);   // 2 pages, contiguous
            if (s_bulk == null) return false;

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
            return false;
        }

        private static ulong ClusterLba(uint cluster)
            => s_dataLba + (ulong)(cluster - 2) * s_spc;

        // Next cluster in the chain, or 0 at end / bad.
        private static uint FatNext(uint cluster)
        {
            if (s_isFat32)
            {
                ulong byteOff = (ulong)cluster * 4UL;
                ulong lba = s_fatLba + byteOff / s_bps;
                uint o = (uint)(byteOff % s_bps);
                if (!ReadAbs(lba)) return 0;
                uint v = RdU32(s_sec, (int)o) & 0x0FFFFFFFu;
                return v >= 0x0FFFFFF8u ? 0 : v;
            }
            else
            {
                ulong byteOff = (ulong)cluster * 2UL;
                ulong lba = s_fatLba + byteOff / s_bps;
                uint o = (uint)(byteOff % s_bps);
                if (!ReadAbs(lba)) return 0;
                uint v = RdU16(s_sec, (int)o);
                return v >= 0xFFF8u ? 0 : v;
            }
        }

        // Uppercase 8.3 compare: entry 11-byte field vs "NAME    EXT".
        private static bool Name83Eq(byte* ent, byte* want11)
        {
            for (int i = 0; i < 11; i++)
                if (ent[i] != want11[i]) return false;
            return true;
        }

        // Build the padded 11-byte 8.3 field from path[cs, cs+cl)
        // (no Substring — that BCL surface isn't guaranteed here).
        private static void Make83(string path, int cs, int cl, byte* outp)
        {
            for (int i = 0; i < 11; i++) outp[i] = (byte)' ';
            int dot = -1;
            for (int i = 0; i < cl; i++) if (path[cs + i] == '.') { dot = i; break; }
            int nameLen = dot < 0 ? cl : dot;
            for (int i = 0; i < nameLen && i < 8; i++)
                outp[i] = Up((byte)path[cs + i]);
            if (dot >= 0)
                for (int i = 0; i < cl - dot - 1 && i < 3; i++)
                    outp[8 + i] = Up((byte)path[cs + dot + 1 + i]);
        }

        private static byte Up(byte c) => (c >= (byte)'a' && c <= (byte)'z') ? (byte)(c - 32) : c;

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
            byte* want = stackalloc byte[11];
            Make83(path, cs, cl, want);
            char* lfn = stackalloc char[260];
            int lfnLen = 0;

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
                        bool hit = (lfnLen > 0 && LongNameEq(path, cs, cl, lfn, lfnLen))
                                   || Name83Eq(ent, want);
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

        public static int ReadFile(string path, byte* dst, int cap, out uint fileSize)
        {
            fileSize = 0;
            if (!Resolve(path, out uint clus, out uint size, out bool isDir) || isDir)
                return -1;
            fileSize = size;

            // Bulk: read up to BulkBytes worth of sectors per AHCI
            // command (chunk <= 16 sectors @512 = single-PRDT proven
            // path), then memcpy out — ~16x fewer commands than the
            // sector-at-a-time path (matters for 20+ MB assemblies).
            uint maxChunk = (uint)(BulkBytes / s_bps);
            if (maxChunk == 0) maxChunk = 1;

            int copied = 0;
            uint cluster = clus;
            uint remain = size;
            while (cluster != 0 && copied < cap && remain > 0)
            {
                uint si = 0;
                while (si < s_spc && copied < cap && remain > 0)
                {
                    uint chunk = s_spc - si;
                    if (chunk > maxChunk) chunk = maxChunk;
                    if (!s_disk.Read(ClusterLba(cluster) + si, chunk, s_bulk))
                        return copied;
                    ulong bytes = (ulong)chunk * s_bps;
                    for (ulong i = 0; i < bytes && copied < cap && remain > 0; i++)
                    {
                        dst[copied++] = s_bulk[i];
                        remain--;
                    }
                    si += chunk;
                }
                cluster = FatNext(cluster);
            }
            return copied;
        }
    }
}
