namespace OS.Hal
{
    // HAL-tier filesystem abstraction (the FS analogue of Disk): a
    // mounted volume exposes path -> bytes / exists. Callers talk to
    // Fs.Current, not a concrete FS, so swapping FAT for ext2/tar/etc
    // later is a Vfs detection-chain entry, not a caller change.
    // (Named Fs, not FileSystem, to stay distinct from the existing
    // UEFI-backed OS.Kernel.File.FileSystem facade — the bridge will
    // later route that one's Platform.TryReadFile through Fs.Current.)
    internal abstract unsafe class Fs
    {
        public static Fs Current;

        // Copy up to `cap` bytes of `path` into `dst`; out the true
        // file size. Returns bytes copied, or -1 if not found / dir.
        public abstract int ReadFile(string path, byte* dst, int cap, out uint fileSize);

        public abstract bool Exists(string path);

        // Cheap metadata probe: true iff the entry exists; `size` and
        // `isDir` reflect its on-disk metadata. NO file content is
        // read. PS GetFileAttributes-style probes (hundreds per
        // assembly load) MUST go through Stat, not ReadFile.
        public abstract bool Stat(string path, out uint size, out bool isDir);

        // The `index`-th entry of directory `path` ("" / "/" = root).
        // name -> nameOut (nameLen chars), attrs bit 0x10 = directory.
        public abstract bool EnumDir(string path, uint index,
            char* nameOut, uint nameCap, out uint nameLen, out ulong attrs);
    }
}
