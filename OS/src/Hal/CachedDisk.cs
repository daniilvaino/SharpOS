namespace OS.Hal
{
    /// <summary>
    /// Single sectors of a disk kept in memory (step208): what the file
    /// system reads a sector at a time — directories, the FAT, FSInfo — is
    /// read once. Direct-mapped, 1024 sectors (512 KiB); a sector that lands
    /// on a taken slot replaces it.
    /// </summary>
    /// <remarks>
    /// Write-through: a write goes to the disk at once and updates the copy
    /// it covers, so the disk always has the latest — reads of several sectors
    /// (file data, in runs) go past the cache and still see it, and a power
    /// cut loses nothing the old path would have kept. The memory comes from
    /// the native arena, not the heap: the volume mounts early.
    /// </remarks>
    internal sealed unsafe class CachedDisk : Disk
    {
        private const uint Slots = 1024;
        private const ulong Empty = ulong.MaxValue;

        private readonly Disk _inner;
        private readonly uint _sectorBytes;
        private readonly byte* _data;
        private readonly ulong* _lba;

        public ulong Hits, Misses;

        private CachedDisk(Disk inner, uint sectorBytes, byte* data, ulong* lba)
        {
            _inner = inner;
            _sectorBytes = sectorBytes;
            _data = data;
            _lba = lba;
            for (uint i = 0; i < Slots; i++) _lba[i] = Empty;
        }

        /// <summary>The disk with a cache in front; the disk itself when there is no memory for one.</summary>
        public static Disk Over(Disk inner, uint sectorBytes)
        {
            byte* data = (byte*)OS.Kernel.Memory.NativeArena.Allocate((ulong)Slots * sectorBytes);
            ulong* lba = (ulong*)OS.Kernel.Memory.NativeArena.Allocate((ulong)Slots * 8);
            if (data == null || lba == null) return inner;
            return new CachedDisk(inner, sectorBytes, data, lba);
        }

        private static uint Slot(ulong sector) => (uint)((sector ^ (sector >> 10)) % Slots);

        public override bool Read(ulong sector, uint count, byte* data)
        {
            if (count != 1) return _inner.Read(sector, count, data);
            uint slot = Slot(sector);
            byte* cached = _data + (ulong)slot * _sectorBytes;
            if (_lba[slot] == sector)
            {
                System.Runtime.CompilerServices.Unsafe.CopyBlock(data, cached, _sectorBytes);
                Hits++;
                return true;
            }
            if (!_inner.Read(sector, 1, data)) return false;
            System.Runtime.CompilerServices.Unsafe.CopyBlock(cached, data, _sectorBytes);
            _lba[slot] = sector;
            Misses++;
            return true;
        }

        public override bool Write(ulong sector, uint count, byte* data)
        {
            bool ok = _inner.Write(sector, count, data);
            for (uint i = 0; i < count; i++)
            {
                uint slot = Slot(sector + i);
                if (_lba[slot] != sector + i) continue;
                // Written: the copy follows. Failed: what the disk holds is
                // not known — the copy goes.
                if (ok) System.Runtime.CompilerServices.Unsafe.CopyBlock(_data + (ulong)slot * _sectorBytes, data + (ulong)i * _sectorBytes, _sectorBytes);
                else _lba[slot] = Empty;
            }
            return ok;
        }

        public override bool Flush() => _inner.Flush();
    }
}
