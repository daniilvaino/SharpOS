using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // First write to a filesystem, ever, in this project. Deliberately the
    // narrowest useful operation: overwrite the head of an existing file
    // without touching its size, its cluster chain or its directory entry —
    // so a bug here can only corrupt that file's own contents.
    //
    // The staged file (\sharpos\fswrite.bin, filled with 'A' by
    // run_build.ps1) is written with a recognisable pattern, then read back
    // through the ordinary read path. Reading back proves the write reached
    // the disk rather than a cache, but not that it landed in the right
    // place: only the host can say that, by opening the same image with an
    // independent FAT implementation:
    //
    //     mtype -i OS\.qemu\esp.img ::/sharpos/fswrite.bin
    //
    // POST-EBS only, like FatProbe — AHCI must own the disk.
    internal static unsafe class FatWriteProbe
    {
        private const string Path = "sharpos/fswrite.bin";
        private const int PatternLength = 64;

        public static void Run()
        {
            if (Ahci.Device == null) Ahci.Initialize();
            if (Ahci.Device == null || Vfs.Mount(Ahci.Device) == null)
            {
                Console.WriteLine("[fatwrite] mount=N FAIL");
                return;
            }

            if (!Fat32.Stat(Path, out uint size, out bool isDir) || isDir)
            {
                Console.WriteLine("[fatwrite] staged file missing FAIL");
                return;
            }

            byte* pattern = stackalloc byte[PatternLength];
            for (int i = 0; i < PatternLength; i++)
                pattern[i] = (byte)('0' + (i % 10));

            int written = Fat32.WriteFileInPlace(Path, pattern, PatternLength);
            if (written != PatternLength)
            {
                Console.Write("[fatwrite] write=");
                Console.WriteInt(written);
                Console.WriteLine(" FAIL");
                return;
            }

            byte* back = stackalloc byte[PatternLength];
            int read = Fat32.ReadFile(Path, back, PatternLength, out uint _);
            if (read != PatternLength)
            {
                Console.Write("[fatwrite] readback=");
                Console.WriteInt(read);
                Console.WriteLine(" FAIL");
                return;
            }

            for (int i = 0; i < PatternLength; i++)
            {
                if (back[i] == pattern[i]) continue;
                Console.Write("[fatwrite] mismatch at ");
                Console.WriteInt(i);
                Console.WriteLine(" FAIL");
                return;
            }

            // Size must be untouched — this operation is not allowed to grow
            // the file, and a changed size would mean the directory entry was
            // written when it should not have been.
            Fat32.Stat(Path, out uint sizeAfter, out _);
            Console.Write("[fatwrite] wrote=");
            Console.WriteInt(written);
            Console.Write(" size=");
            Console.WriteUInt(sizeAfter);
            Console.WriteLine(sizeAfter == size ? " PASS" : " size-changed FAIL");

            if (Probes.FatCreate)
                RunCreate();
        }

        // Creating a file is a different class of write from the one above:
        // it allocates clusters and edits a directory. A bug here damages the
        // volume, not one file's contents — which is why it is separate, and
        // why the name is fixed and short (8.3 only, see Fat32Create).
        //
        // Host-side confirmation, the only thing that proves the entry is
        // legal rather than merely readable by us:
        //
        //     mdir -i OS\.qemu\esp.img ::/sharpos
        //     mtype -i OS\.qemu\esp.img ::/sharpos/created.txt
        private const string CreatePath = "sharpos/created.txt";

        private static void RunCreate()
        {
            // A previous run leaves the file behind (there is no delete yet),
            // so an existing file is a skip, not a failure.
            if (Fat32.Stat(CreatePath, out uint existing, out _))
            {
                Console.Write("[fatcreate] already exists size=");
                Console.WriteUInt(existing);
                Console.WriteLine(" SKIP");
                return;
            }

            const int Size = 512;
            if (!Fat32.TryCreateFile(CreatePath, Size))
            {
                Console.WriteLine("[fatcreate] create FAIL");
                return;
            }

            if (!Fat32.Stat(CreatePath, out uint size, out bool isDir) || isDir || size != Size)
            {
                Console.WriteLine("[fatcreate] entry not found after create FAIL");
                return;
            }

            byte* pattern = stackalloc byte[64];
            for (int i = 0; i < 64; i++) pattern[i] = (byte)('A' + (i % 26));

            if (Fat32.WriteFileInPlace(CreatePath, pattern, 64) != 64)
            {
                Console.WriteLine("[fatcreate] write into new file FAIL");
                return;
            }

            byte* back = stackalloc byte[64];
            if (Fat32.ReadFile(CreatePath, back, 64, out uint _) != 64)
            {
                Console.WriteLine("[fatcreate] readback FAIL");
                return;
            }

            for (int i = 0; i < 64; i++)
            {
                if (back[i] == pattern[i]) continue;
                Console.Write("[fatcreate] mismatch at ");
                Console.WriteInt(i);
                Console.WriteLine(" FAIL");
                return;
            }

            Console.Write("[fatcreate] created ");
            Console.Write(CreatePath);
            Console.Write(" size=");
            Console.WriteUInt(size);
            Console.WriteLine(" verify=PASS");
        }
    }
}
