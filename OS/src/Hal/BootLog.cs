namespace OS.Hal
{
    // Mirrors console output into a file on the ESP.
    //
    // Why it exists: on real hardware there is no serial port, no scrollback
    // and — after ExitBootServices — no keyboard either, so a machine that
    // dies mid-boot takes its own explanation with it. The screen has been
    // the only channel, and it holds about forty lines.
    //
    // The file is pre-staged by run_build.ps1 at a fixed size; this never
    // grows it, allocates nothing and does not touch the directory entry, so
    // a bug here cannot damage anything but the log's own contents.
    //
    // Flushed once per line rather than at the end: the runs worth reading are
    // exactly the ones that never reach the end.
    internal static unsafe class BootLog
    {
        private const string Path = "sharpos/bootlog.txt";
        private const int SectorSize = 512;

        private static bool s_ready;
        private static bool s_inFlush;      // re-entrancy: flushing writes, writing logs
        private static ulong s_startLba;
        private static uint s_sectorCount;
        private static uint s_sector;       // next sector to write
        private static int s_used;          // bytes staged in s_line
        private static byte[] s_line;

        // Everything printed before the disk was ours.
        //
        // The log can only bind after the filesystem mounts, which is well
        // past ExitBootServices — yet the lines that explain a machine are
        // mostly earlier than that: hardware discovery, paging, the
        // framebuffer, the mode it chose. Losing exactly those was what kept
        // the diagnosis running on photographs of somebody else's screen.
        //
        // Bounded and non-wrapping: the point is the beginning of the boot,
        // so when it fills, the later part is the part to drop.
        // A fixed buffer inside a static struct, NOT a static byte[].
        //
        // An array field would need a class constructor to allocate it, and
        // those do not run here (limits §1). Small arrays survive because the
        // compiler lays them out ahead of time, but this one is far past the
        // size where it does that — so the first character ever printed took
        // the machine down before a single line appeared. A fixed buffer is
        // plain static storage: no allocation, no constructor.
        // 128 KiB: a real boot with the probes on overflowed 32 KiB and lost
        // the earliest lines — the ones the buffer exists for. Static storage,
        // so the cost is address space rather than allocation.
        private const int EarlySize = 128 * 1024;

        private struct EarlyBuffer
        {
            public fixed byte Data[EarlySize];
        }

        private static EarlyBuffer s_early;
        private static int s_earlyUsed;
        private static bool s_earlyOverflowed;
        private static bool s_earlyDrained;

        public static bool IsReady => s_ready;

        // 0 none, 1 could not create, 2 created but fragmented.
        private static uint s_failure;
        public static uint FailReason => s_failure;

        /// <summary>Binds to the staged file. Safe to call more than once;
        /// after a failure the log stays disabled and the kernel runs on.</summary>
        public static bool TryInit()
        {
            if (s_ready) return true;

            s_failure = 0;
            if (!Fat32.TryOpenLinear(Path, out ulong lba, out uint sectors))
            {
                // Absent on a medium nobody staged — which is every machine
                // the log matters most on, since they boot from a stick
                // written once and never touched by the build again. Make it.
                if (!Fat32.TryCreateFile(Path, CreateBytes)) { s_failure = 1; return false; }

                // A file that exists but cannot be opened flat is the
                // fragmented case: the writer refuses it rather than scribble
                // across clusters it does not own.
                if (!Fat32.TryOpenLinear(Path, out lba, out sectors)) { s_failure = 2; return false; }
            }

            s_line = new byte[SectorSize];
            s_startLba = lba;
            s_sectorCount = sectors;
            s_sector = 0;
            s_used = 0;
            s_ready = true;

            // Nothing deletes this file and nothing shortens it: it is
            // rewritten in place from the start, so a boot that says less than
            // the previous one leaves the older text right after its own —
            // reading as if it belonged to this run. Clear a window ahead of
            // the writer so that cannot happen, and mark where this boot began.
            uint blank = sectors < BlankSectors ? sectors : BlankSectors;
            Fat32.BlankSectors(lba, blank);

            DrainEarly();
            return true;
        }

        // 512 KiB of clearing: comfortably more than a boot produces, while
        // cheap enough over USB (multi-sector writes, a handful of commands).
        private const uint BlankSectors = 1024;

        // Size to create when the file is missing. 1 MiB is far more than a
        // boot writes, and small enough that zeroing its clusters over USB is
        // not felt — the staged file is 16 MiB, but that one costs nothing
        // because the build writes it into the image offline.
        private const uint CreateBytes = 1024 * 1024;

        // Replay what was captured before the disk existed, so the file opens
        // with the start of the boot rather than the middle of it.
        private static void DrainEarly()
        {
            if (s_earlyDrained) return;
            s_earlyDrained = true;

            const string banner = "===== SharpOS boot log start =====\n";
            for (int i = 0; i < banner.Length; i++) Putc(banner[i]);

            for (int i = 0; i < s_earlyUsed; i++)
            {
                char c;
                fixed (byte* p = s_early.Data) c = (char)p[i];
                Putc(c);
            }

            if (s_earlyOverflowed)
            {
                const string note = "[bootlog] early buffer overflowed - lines lost here\n";
                for (int i = 0; i < note.Length; i++) Putc(note[i]);
            }
        }

        /// <summary>Records one character. Called from Platform.WriteChar, so
        /// it must never call back into the console.</summary>
        public static void Putc(char ch)
        {
            if (ch == '\r') return;                  // the file wants bare LF

            if (!s_ready)
            {
                // No disk yet — hold it until there is one.
                if (s_earlyDrained) return;
                if (s_earlyUsed < EarlySize)
                {
                    fixed (byte* p = s_early.Data) p[s_earlyUsed++] = (byte)ch;
                }
                else s_earlyOverflowed = true;
                return;
            }

            if (s_inFlush) return;

            if (s_used < SectorSize)
                s_line[s_used++] = (byte)ch;

            if (ch == '\n' || s_used == SectorSize)
                Flush();
        }

        /// <summary>Pushes the current sector to disk. The sector is rewritten
        /// as it fills rather than being closed off per line — otherwise every
        /// line would cost 512 bytes and the file would read as one message per
        /// screenful of padding.</summary>
        public static void Flush()
        {
            if (!s_ready || s_inFlush || s_used == 0) return;
            if (s_sector >= s_sectorCount) return;   // full: stop, never wrap

            s_inFlush = true;
            // Pad only the unwritten tail of this sector; the next line will
            // overwrite the padding.
            for (int i = s_used; i < SectorSize; i++) s_line[i] = (byte)' ';

            bool ok;
            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            fixed (byte* p = s_line)
            {
                ok = Fat32.WriteSectorAt(s_startLba + s_sector, p);
            }
            OS.Kernel.Diagnostics.PerfCounters.CountDiskLog(started);

            if (!ok)
            {
                s_ready = false;          // disk gone: stop trying, keep booting
                s_inFlush = false;
                return;
            }

            if (s_used == SectorSize)     // sector closed — move on
            {
                s_sector++;
                s_used = 0;
            }

            s_inFlush = false;
        }
    }
}
