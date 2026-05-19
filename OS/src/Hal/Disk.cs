// Driver implementations adapted from MOOS by nifanfa
// (https://github.com/nifanfa/MOOS), released under the Unlicense
// (public domain). Standing on shoulders of fellow public domain contributors.
//
// Ported from MOOS Kernel/FS/Disk.cs verbatim (only: namespace
// MOOS.FS -> OS.Hal, public -> internal). Block-device abstraction the
// AHCI driver implements and the FAT32 reader consumes.

namespace OS.Hal
{
    internal abstract unsafe class Disk
    {
        public static Disk Instance;

        public Disk()
        {
            Instance = this;
        }

        public abstract bool Read(ulong sector, uint count, byte* data);
        public abstract bool Write(ulong sector, uint count, byte* data);
    }
}
