// Driver implementations adapted from MOOS by nifanfa
// (https://github.com/nifanfa/MOOS), released under the Unlicense
// (public domain). Standing on shoulders of fellow public domain contributors.
//
// Ported from MOOS Kernel/FS/Disk.cs (namespace MOOS.FS -> OS.Hal,
// public -> internal). Block-device abstraction the AHCI and USB drivers
// implement and the FAT32 reader consumes.
//
// Cut: the static Instance every constructor set to itself. It made "the
// disk" whichever one was created last — a probe's, a driver's — rather
// than the one chosen; BootDisk is where the boot disk is decided.

namespace OS.Hal
{
    internal abstract unsafe class Disk
    {
        public abstract bool Read(ulong sector, uint count, byte* data);
        public abstract bool Write(ulong sector, uint count, byte* data);
    }
}
