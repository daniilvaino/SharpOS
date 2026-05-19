namespace OS.Hal
{
    // Volume mount + filesystem detection. Mount(disk) runs a probe
    // chain: each FS tries to recognise the volume; the first that
    // does becomes Fs.Current. Today only FAT16/32 (Fat32.Mount
    // validates the BPB and returns false on a non-FAT volume) — adding
    // ext2/tar/etc later is one more probe here, no caller change.
    internal static unsafe class Vfs
    {
        public static Fs Mount(Disk disk)
        {
            if (disk == null) return null;

            // FAT16/32 — Fat32.Mount is itself the detector (rejects a
            // volume whose BPB doesn't validate).
            if (Fat32.Mount(disk))
            {
                Fs.Current = new Fat32Fs();
                return Fs.Current;
            }

            // future: if (Ext2.Probe(disk)) { ... }  /  TarFs, etc.
            return null;
        }
    }

    // Thin adapter: the static Fat32 reader as an Fs instance.
    internal sealed unsafe class Fat32Fs : Fs
    {
        public override int ReadFile(string path, byte* dst, int cap, out uint fileSize)
            => Fat32.ReadFile(path, dst, cap, out fileSize);

        public override bool Exists(string path) => Fat32.Exists(path);
    }
}
