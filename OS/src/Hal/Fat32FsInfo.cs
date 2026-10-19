namespace OS.Hal
{
    // The free-cluster count (step208): FSInfo kept, not given up.
    //
    // Before, the first change of the FAT marked FSInfo's count unknown for
    // good, and every "how much room" counted the whole FAT again. Now the
    // count lives here: read from FSInfo at mount when it is plausible (as
    // Linux does — the specification calls it a hint), counted once in the
    // FAT when it is not, and moved by every FAT entry that goes from free to
    // used or back — the two places that change entries see both values.
    //
    // On the disk the order is the old one's, for a power cut: the first
    // change after the count was written marks it unknown there; the points
    // that flush the device (a file closed, a directory changed, power-off)
    // write the true count and the next-free hint back. A cut in between
    // leaves "unknown", which every tool recounts — never a wrong number.
    internal static unsafe partial class Fat32
    {
        private static uint s_freeClusters;
        private static bool s_freeKnown;
        // Whether FSInfo on the disk says "unknown" now.
        private static bool s_fsInfoUnknownOnDisk;

        private const uint FsInfoLead = 0x41615252u, FsInfoStruct = 0x61417272u;

        // At mount: FSInfo's count and hint, when they are plausible.
        private static void ReadFsInfo()
        {
            s_freeKnown = false;
            s_fsInfoUnknownOnDisk = false;
            if (s_fsInfoLba == 0 || !ReadAbs(s_fsInfoLba)) return;
            if (RdU32(s_sec, 0) != FsInfoLead || RdU32(s_sec, 484) != FsInfoStruct) return;
            uint free = RdU32(s_sec, 488), hint = RdU32(s_sec, 492);
            if (free != 0xFFFFFFFFu && free <= s_clusterCount - 2)
            {
                s_freeClusters = free;
                s_freeKnown = true;
            }
            else s_fsInfoUnknownOnDisk = true;
            if (hint >= 2 && hint < s_clusterCount) s_allocationHint = hint;
        }

        // A FAT entry changed from `before` to `after` (values without the
        // reserved top bits).
        private static void FatEntryChanged(uint before, uint after)
        {
            bool wasFree = before == 0, isFree = after == 0;
            if (wasFree == isFree) return;
            if (s_freeKnown)
            {
                if (isFree) s_freeClusters++;
                else if (s_freeClusters > 0) s_freeClusters--;
            }
            FreeCountUnknown();
        }

        // The disk's FSInfo marked unknown, once per stretch of changes.
        private static void FreeCountUnknown()
        {
            if (s_fsInfoUnknownOnDisk || s_fsInfoLba == 0) return;
            s_fsInfoUnknownOnDisk = true;
            if (!ReadAbs(s_fsInfoLba)) return;
            if (RdU32(s_sec, 0) != FsInfoLead || RdU32(s_sec, 484) != FsInfoStruct) return;
            for (int i = 488; i < 496; i++) s_sec[i] = 0xFF;
            s_disk.Write(s_fsInfoLba, 1, s_sec);
        }

        // At a flush point: the true count and the hint back on the disk.
        private static void WriteFsInfo()
        {
            if (!s_fsInfoUnknownOnDisk || s_fsInfoLba == 0) return;
            if (!s_freeKnown && !CountFree()) return;
            if (!FlushFatCache()) return;
            if (!ReadAbs(s_fsInfoLba)) return;
            if (RdU32(s_sec, 0) != FsInfoLead || RdU32(s_sec, 484) != FsInfoStruct) return;
            uint hint = s_allocationHint >= 2 && s_allocationHint < s_clusterCount ? s_allocationHint : 0xFFFFFFFFu;
            for (int i = 0; i < 4; i++) s_sec[488 + i] = (byte)(s_freeClusters >> (8 * i));
            for (int i = 0; i < 4; i++) s_sec[492 + i] = (byte)(hint >> (8 * i));
            if (s_disk.Write(s_fsInfoLba, 1, s_sec)) s_fsInfoUnknownOnDisk = false;
        }

        // The free clusters counted in the FAT, every sector of it.
        private static bool CountFree()
        {
            uint perSector = s_bps / 4;
            uint free = 0;
            for (uint cluster = 2; cluster < s_clusterCount; )
            {
                ulong lba = s_fatLba + (ulong)cluster * 4 / s_bps;
                if (!ReadFatSector(lba)) return false;
                uint first = (uint)((lba - s_fatLba) * perSector);
                uint end = first + perSector;
                if (end > s_clusterCount) end = s_clusterCount;
                for (uint c = cluster; c < end; c++)
                    if ((RdU32(s_fatCache, (int)((c - first) * 4)) & 0x0FFFFFFFu) == 0) free++;
                cluster = end;
            }
            s_freeClusters = free;
            s_freeKnown = true;
            return true;
        }
    }
}
