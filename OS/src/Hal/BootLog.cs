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

        public static bool IsReady => s_ready;

        /// <summary>Binds to the staged file. Safe to call more than once;
        /// after a failure the log stays disabled and the kernel runs on.</summary>
        public static bool TryInit()
        {
            if (s_ready) return true;
            if (!Fat32.TryOpenLinear(Path, out ulong lba, out uint sectors)) return false;

            s_line = new byte[SectorSize];
            s_startLba = lba;
            s_sectorCount = sectors;
            s_sector = 0;
            s_used = 0;
            s_ready = true;
            return true;
        }

        /// <summary>Records one character. Called from Platform.WriteChar, so
        /// it must never call back into the console.</summary>
        public static void Putc(char ch)
        {
            if (!s_ready || s_inFlush) return;
            if (ch == '\r') return;                  // the file wants bare LF

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
            fixed (byte* p = s_line)
            {
                ok = Fat32.WriteSectorAt(s_startLba + s_sector, p);
            }

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
