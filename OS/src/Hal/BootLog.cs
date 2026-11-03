namespace OS.Hal
{
    // Mirrors console output into a file on the ESP.
    //
    // Why it exists: on real hardware there is no serial port, no scrollback
    // and — after ExitBootServices — no keyboard either, so a machine that
    // dies mid-boot takes its own explanation with it. The screen has been
    // the only channel, and it holds about forty lines.
    //
    // An ordinary file since step238, made anew at each boot and grown as the
    // text comes, its size in the directory always what was written. Before,
    // it was staged at a fixed size (16 MiB of spaces by the build, 1 MiB made
    // by the kernel) and written by sectors in place: `cat` printed megabytes
    // of padding, and — the worse half — nothing after the mount reached the
    // disk. A character is printed with switching suppressed, and a disk
    // write there is refused (step210: it may sleep in the driver), so every
    // flush was put off and the one-sector buffer filled and dropped the rest.
    //
    // Now printing only queues (Putc); a kernel thread of its own writes what
    // is queued every WriteIntervalMs and syncs it — data, FAT, size — so the
    // file reads whole up to its last write, and a crash loses at most that
    // interval.
    internal static unsafe class BootLog
    {
        private const string Directory = "sharpos";
        private const string Path = "sharpos/bootlog.txt";

        private static bool s_ready;
        private static int s_handle;        // the open file
        private static uint s_written;      // bytes in the file

        // At most this much: past it the writer stops with a line saying so.
        private const uint MaxBytes = 16 * 1024 * 1024;
        private static bool s_full;

        // What was printed since the last write. A fixed buffer for the same
        // reason as the early one: filled with switching suppressed, from any
        // thread, without allocating. Lines that do not fit are counted and
        // the file says how much went missing.
        private const int PendingSize = 64 * 1024;

        private struct PendingBuffer
        {
            public fixed byte Data[PendingSize];
        }

        #pragma warning disable CS0169 // used through fixed, which Roslyn does not count as a use
        private static PendingBuffer s_pending;
        #pragma warning restore CS0169
        private static int s_pendingUsed;
        private static uint s_lost;
        private static byte[] s_batch;      // the writer's copy of what it takes
        private static bool s_writing;      // one writer at a time: the thread, or Shutdown's last flush

        private const uint WriteIntervalMs = 50;

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

        #pragma warning disable CS0169 // used through fixed, which Roslyn does not count as a use
        private static EarlyBuffer s_early;
        #pragma warning restore CS0169
        private static int s_earlyUsed;
        private static bool s_earlyOverflowed;
        private static bool s_earlyDrained;

        // Second sink: the same text out a USB serial port.
        //
        // The file on the stick only becomes readable once the machine is
        // switched off and the medium handed back. On the test rig the other
        // end of this port is the phone, which can read the run as it happens
        // — and, when the machine hangs, still has everything up to the hang.
        // Filled by printing, sent by the writer thread every interval
        // (step239): printing runs with switching suppressed, where a USB
        // transfer is not allowed, and before this the buffer was only sent
        // from a line end — never, so all but the first 512 bytes were lost.
        private const int SerialSize = 16 * 1024;

        private struct SerialBuffer
        {
            public fixed byte Data[SerialSize];
        }

        #pragma warning disable CS0169 // used through fixed, which Roslyn does not count as a use
        private static SerialBuffer s_serial;
        #pragma warning restore CS0169
        private static int s_serialUsed;
        private static bool s_serialReady;
        private static bool s_inSerial;

        public static bool IsReady => s_ready;

        // 0 none, 1 could not open or create, 2 the first write failed.
        private static uint s_failure;
        public static uint FailReason => s_failure;

        /// <summary>
        /// Makes the file anew — a boot's log, not the end of the last one's —
        /// writes what was printed before the disk was ours, and starts the
        /// writer. Safe to call more than once; after a failure the log stays
        /// off and the kernel runs on.
        /// </summary>
        public static bool TryInit()
        {
            if (s_ready) return true;

            s_failure = 0;
            int handle = Fat32.Open(Path, Fat32.OpenWrite);
            if (handle == -1 && Fat32.CreateDirectory(Directory) >= 0)
                handle = Fat32.Open(Path, Fat32.OpenWrite);
            if (handle <= 0) { s_failure = 1; return false; }
            s_handle = handle;
            s_batch = new byte[PendingSize];

            // What was captured before the disk existed, so the file opens with
            // the start of the boot rather than the middle of it — straight
            // from the early buffer, which is larger than the queue.
            s_earlyDrained = true;
            const string banner = "===== SharpOS boot log start =====\n";
            byte* line = stackalloc byte[banner.Length];
            for (int i = 0; i < banner.Length; i++) line[i] = (byte)banner[i];
            bool ok = Append(line, banner.Length);
            fixed (byte* p = s_early.Data) ok &= Append(p, s_earlyUsed);
            if (s_earlyOverflowed)
            {
                const string note = "[bootlog] early buffer overflowed - lines lost here\n";
                byte* text = stackalloc byte[note.Length];
                for (int i = 0; i < note.Length; i++) text[i] = (byte)note[i];
                ok &= Append(text, note.Length);
            }
            if (!ok || !Fat32.Sync(s_handle)) { s_failure = 2; return false; }

            s_ready = true;
            StartWriter();
            return true;
        }

        private static bool s_writerStarted;

        // One writer for both sinks, started by whichever comes first.
        private static void StartWriter()
        {
            if (s_writerStarted) return;
            s_writerStarted = true;
            OS.Kernel.Threading.Scheduler.Spawn(&WriterEntry, 32 * 1024);
        }

        private static bool Append(byte* bytes, int length)
        {
            if (length <= 0) return true;
            if (!Fat32.Write(s_handle, bytes, length)) return false;
            s_written += (uint)length;
            return true;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WriterEntry()
        {
            while (s_ready || s_serialReady)
            {
                OS.Kernel.Threading.Scheduler.Sleep(WriteIntervalMs);
                Flush();
                SerialFlushQueued();
            }
        }

        /// <summary>
        /// Start mirroring to a USB serial port and replay the boot so far.
        /// Called once the CDC-ACM device is configured; before that there is
        /// nowhere for the text to go.
        /// </summary>
        public static void AttachSerial()
        {
            if (s_serialReady || !Usb.UsbCdcAcm.IsPresent) return;
            s_serialReady = true;

            const string banner = "===== SharpOS boot log start =====\n";
            for (int i = 0; i < banner.Length; i++) SerialPutc(banner[i]);

            for (int i = 0; i < s_earlyUsed; i++)
            {
                char c;
                fixed (byte* p = s_early.Data) c = (char)p[i];
                SerialPutc(c);
            }
            SerialFlush();
            StartWriter();
        }

        private static void SerialPutc(char ch)
        {
            if (!s_serialReady || s_inSerial) return;

            if (s_serialUsed < SerialSize)
            {
                fixed (byte* p = s_serial.Data) p[s_serialUsed++] = (byte)ch;
            }

            if (ch == '\n' || s_serialUsed >= SerialSize) SerialFlush();
        }

        // The writer thread's turn for the port: what printing could not send.
        private static void SerialFlushQueued()
        {
            if (!s_serialReady || s_serialUsed == 0) return;
            OS.Kernel.Threading.Preemption.Suppress();
            if (s_inSerial || s_serialUsed == 0)
            {
                OS.Kernel.Threading.Preemption.Allow();
                return;
            }
            int n = s_serialUsed;
            byte[] copy = s_serialBatch ??= new byte[SerialSize];
            fixed (byte* p = s_serial.Data)
                for (int i = 0; i < n; i++) copy[i] = p[i];
            s_serialUsed = 0;
            s_inSerial = true;
            OS.Kernel.Threading.Preemption.Allow();

            bool ok;
            fixed (byte* p = copy) ok = Usb.UsbCdcAcm.Write(p, n);
            if (!ok) s_serialReady = false;
            s_inSerial = false;
        }

        private static byte[] s_serialBatch;

        private static bool InInterrupt()
            => (OS.Kernel.Threading.Scheduler.Current?.InterruptDepth ?? 0) != 0;

        private static void SerialFlush()
        {
            if (!s_serialReady || s_inSerial || s_serialUsed == 0) return;
            if (InInterrupt() || OS.Kernel.Threading.Preemption.Depth != 0)
                return;                             // a USB transfer: as Flush

            s_inSerial = true;
            bool ok;
            fixed (byte* p = s_serial.Data) ok = Usb.UsbCdcAcm.Write(p, s_serialUsed);
            s_serialUsed = 0;
            // The other end went away: stop trying rather than pay a timeout
            // per line for the rest of the boot.
            if (!ok) s_serialReady = false;
            s_inSerial = false;
        }

        /// <summary>Records one character. Called from Platform.WriteChar, so
        /// it must never call back into the console.</summary>
        public static void Putc(char ch)
        {
            if (ch == '\r') return;                  // the file wants bare LF

            // Independent of the disk sink: the port is usually up first, and
            // stays up when the filesystem does not.
            SerialPutc(ch);

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

            // Queued only: printing happens with switching suppressed, where a
            // disk write is not allowed (Flush says why). A line at a time,
            // per thread (step239): the kernel says a line in several writes —
            // Write, WriteUInt, WriteLine — and another thread's text landed
            // in between, in the middle of the line.
            OS.Kernel.Threading.Preemption.Suppress();
            int slot = LineSlotOf(OS.Kernel.Threading.Scheduler.Current?.Id ?? 0);
            if (slot < 0) Queue(ch);
            else
            {
                fixed (byte* lines = s_lines.Data)
                fixed (int* used = s_lineState.Used)
                fixed (int* owner = s_lineState.Owner)
                {
                    lines[slot * LineBytes + used[slot]++] = (byte)ch;
                    if (ch == '\n' || used[slot] == LineBytes)
                    {
                        for (int i = 0; i < used[slot]; i++) Queue((char)lines[slot * LineBytes + i]);
                        used[slot] = 0;
                        owner[slot] = 0;
                    }
                }
            }
            OS.Kernel.Threading.Preemption.Allow();
        }

        private static void Queue(char ch)
        {
            if (s_pendingUsed < PendingSize)
            {
                fixed (byte* p = s_pending.Data) p[s_pendingUsed++] = (byte)ch;
            }
            else s_lost++;
        }

        // A line being said, per thread: up to LineSlots threads at once; a
        // thread past them, or none (early), goes straight to the queue.
        private const int LineSlots = 8, LineBytes = 512;

        private struct LineData
        {
            public fixed byte Data[LineSlots * LineBytes];
        }

        private struct LineState
        {
            public fixed int Owner[LineSlots];   // thread id; 0 free
            public fixed int Used[LineSlots];
        }

        #pragma warning disable CS0169 // used through fixed, which Roslyn does not count as a use
        private static LineData s_lines;
        private static LineState s_lineState;
        #pragma warning restore CS0169

        // The slot of the thread's unfinished line, a free one for a new line, -1 for neither.
        private static int LineSlotOf(int thread)
        {
            if (thread == 0) return -1;
            int free = -1;
            fixed (int* owner = s_lineState.Owner)
            {
                for (int i = 0; i < LineSlots; i++)
                {
                    if (owner[i] == thread) return i;
                    if (owner[i] == 0 && free < 0) free = i;
                }
                if (free >= 0) owner[free] = thread;
            }
            return free;
        }

        // Unfinished lines go as they are: before the power goes.
        private static void QueueOpenLines()
        {
            OS.Kernel.Threading.Preemption.Suppress();
            fixed (byte* lines = s_lines.Data)
            fixed (int* used = s_lineState.Used)
            fixed (int* owner = s_lineState.Owner)
                for (int slot = 0; slot < LineSlots; slot++)
                {
                    for (int i = 0; i < used[slot]; i++) Queue((char)lines[slot * LineBytes + i]);
                    used[slot] = 0;
                    owner[slot] = 0;
                }
            OS.Kernel.Threading.Preemption.Allow();
        }

        /// <summary>Before the power goes: unfinished lines too, then all of it to the disk.</summary>
        public static void FlushForShutdown()
        {
            QueueOpenLines();
            Flush();
        }

        /// <summary>
        /// Writes what is queued and syncs the file. The writer thread calls it
        /// every interval; Shutdown once more before the power goes.
        /// </summary>
        public static void Flush()
        {
            if (!s_ready || s_full) return;

            // Not from an interrupt handler (step210): a line printed there (the
            // sampler's report on a tick) would write the disk with the tick
            // still unacknowledged. With the drivers asleep on interrupts that
            // wait never ends — no tick comes to wake anything.
            //
            // Nor inside a critical section: a disk command may wait for its
            // device and give up the CPU, and a section that suppresses
            // preemption is relying on nobody else running — an app's collection
            // is one (stop-the-world on one core). A line the walker printed in
            // the middle of a collection flushed to the boot log on a USB stick,
            // the write slept, another thread of the app allocated a buffer the
            // mark had already passed, the sweep freed it under a file read
            // (step210). The queue keeps such lines for the writer.
            if (InInterrupt() || OS.Kernel.Threading.Preemption.Depth != 0) return;

            // Take what is queued, in one go, and let printing go on into an
            // empty queue while it is written.
            int taken;
            uint lost;
            OS.Kernel.Threading.Preemption.Suppress();
            if (s_writing)
            {
                OS.Kernel.Threading.Preemption.Allow();
                return;
            }
            s_writing = true;
            taken = s_pendingUsed;
            lost = s_lost;
            fixed (byte* p = s_pending.Data)
                for (int i = 0; i < taken; i++) s_batch[i] = p[i];
            s_pendingUsed = 0;
            s_lost = 0;
            OS.Kernel.Threading.Preemption.Allow();

            bool ok = true;
            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            if (taken > 0 || lost > 0)
            {
                if (s_written + (uint)taken > MaxBytes)
                {
                    s_full = true;
                    taken = 0;
                    lost = 0;
                    ok &= AppendText("[bootlog] the log reached " + (MaxBytes >> 20).ToString() + " MiB: nothing more is written\n");
                }
                fixed (byte* b = s_batch) ok &= Append(b, taken);
                if (lost > 0)
                    ok &= AppendText("[bootlog] " + lost.ToString() + " bytes lost here: printed faster than written\n");
                ok &= Fat32.Sync(s_handle);
            }
            OS.Kernel.Diagnostics.PerfCounters.CountDiskLog(started);

            // The disk gone: stop trying, keep booting.
            if (!ok) s_ready = false;
            s_writing = false;
        }

        private static bool AppendText(string text)
        {
            byte* bytes = stackalloc byte[text.Length];
            for (int i = 0; i < text.Length; i++) bytes[i] = (byte)text[i];
            return Append(bytes, text.Length);
        }
    }
}
