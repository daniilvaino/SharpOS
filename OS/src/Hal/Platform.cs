using OS.Boot;
using OS.Kernel.Memory;

namespace OS.Hal
{
    internal static unsafe class Platform
    {
        private const uint TimedReadThresholdBytes = 1024 * 1024;

        private static BootInfo s_bootInfo;
        private static bool s_initialized;

        public static PlatformCapabilities Capabilities => s_bootInfo.Capabilities;

        public static void Init(BootInfo bootInfo)
        {
            s_bootInfo = bootInfo;
            s_initialized = true;
        }

        public static BootInfo GetBootInfo() => s_bootInfo;

        public static bool HasCapability(PlatformCapabilities capability)
        {
            return (Capabilities & capability) == capability;
        }

        private static ulong NowTicks()
            => Timer.Hpet.IsInitialized ? Timer.Hpet.ReadCounter() : 0;

        private static ulong ElapsedTicks(ulong start, ulong end)
        {
            if (start == 0 || end < start) return 0;
            return end - start;
        }

        private static ulong TicksToMs(ulong ticks)
        {
            ulong hz = Timer.Hpet.FrequencyHz;
            if (hz == 0) return 0;
            return ticks * 1000UL / hz;
        }

        // Post-ExitBootServices: UEFI ConOut is dead, so the kernel
        // Console must run on the own substrate (16550 UART hardware —
        // pure port I/O, valid post-EBS — plus FbTty into the mapped
        // GOP MMIO). Set once, just before calling ExitBootServices.
        private static bool s_ownConsole;

        public static void UseOwnConsole() => s_ownConsole = true;

        // Repoint the BootInfo file/dir delegates at the FAT bridge so
        // every raw bootInfo.* caller (app-service ABI, launcher, PE
        // loader) reads our FAT, not dead UEFI. Called once by
        // Vfs.Mount after a volume comes up.
        public static void UseFatBootDelegates()
        {
            s_bootInfo.FileExists         = &FatBootBridge.FileExists;
            s_bootInfo.FileReadAll        = &FatBootBridge.FileReadAll;
            s_bootInfo.FileReadIntoBuffer = &FatBootBridge.FileReadIntoBuffer;
            s_bootInfo.DirectoryReadEntry = &FatBootBridge.DirectoryReadEntry;
        }

        // True once the console has been rerouted off UEFI — the
        // ExitBootServices boundary. Any UEFI Boot Services call after
        // this point hits dead firmware, so the post-EBS-dangerous
        // helpers (UefiFile IO, the UEFI exec-mem allocator) check this
        // and refuse rather than fault. Defense-in-depth: the boot flow
        // already orders all Boot Services use before the reroute.
        public static bool BootServicesGone => s_ownConsole;

        // Console output is not reentrant, and preemption made that visible:
        // the terminal engine carries a cursor, a grid and an escape-sequence
        // parser, the boot log carries a write index, and a thread switch in
        // the middle of any of them leaves state no reader expects. PowerShell
        // died inside WriteConsole the first time preemption was allowed near
        // it.
        //
        // Guarded per call rather than per character: an escape sequence is
        // several characters long and means nothing if another thread's output
        // lands inside it. Nesting is fine — suppression is a counter.
        /// <summary>Writes one character of the kernel's own log.</summary>
        public static void WriteChar(char value) => WriteChar(value, OutputChannel.Kernel);

        /// <summary>
        /// Writes one character on behalf of <paramref name="channel"/>; where it
        /// lands is <see cref="OutputRouting"/>'s decision.
        /// </summary>
        public static void WriteChar(char value, OutputChannel channel)
        {
            // Entered again before the outer character is done: a fault or an
            // interrupt inside the terminal, the disk log or the port itself.
            // Neither the terminal nor the disk log survives that; the raw port
            // does. A fault in the disk log's SATA read printed straight into
            // the terminal it had interrupted and broke it (#PF in XtermSharp),
            // and the first fault's line was lost.
            //
            // Inside a collection the same: every live object carries the mark
            // bit in its MethodTable pointer, and the disk log's virtual call
            // on SATADevice went through a table read one byte off — straight
            // to a non-canonical address (the collector's own "[gc] exception
            // chain entry implausible" line, printed while marking).
            if (s_writing != 0 || SharpOS.Std.NoRuntime.GcMark.InProgress)
            {
                if (s_ownConsole) Serial.WriteChar(value);
                return;
            }

            s_writing++;
            OS.Kernel.Threading.Preemption.Suppress();
            try { WriteCharCore(value, channel); }
            finally
            {
                // The count first. Allow takes a tick deferred by the section
                // and switches threads right there (step196): with the count
                // still raised, every writer in the meantime went to the raw
                // port and off the screen, and a thread leaving the machine
                // reset the count under this one, which then took it to -1 —
                // all output, the launcher's frames too, on COM1 for good
                // (step214, a long pipeline ending in `take`).
                s_writing--;
                OS.Kernel.Threading.Preemption.Allow();
            }
        }

        private static int s_writing;

        /// <summary>
        /// An app thread leaves from wherever it is (JumpStub.LeaveApp),
        /// possibly mid-character inside a service: the finally above never
        /// runs. Preemption is off while a character is written, so a raised
        /// count can only be this thread's.
        /// </summary>
        public static void AbandonWrite() => s_writing = 0;

        private static void WriteCharCore(char value, OutputChannel channel)
        {
            OutputSink sinks = OutputRouting.SinksFor(channel);

            // Mirror to the on-disk log before anything else: whatever kills
            // the machine next, this line is already on its way to the platter.
            if ((sinks & OutputSink.DiskLog) != 0)
            {
                ulong started = OS.Kernel.Diagnostics.PerfCounters.SinkClock();
                BootLog.Putc(value);
                OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.SinkDiskLogTsc, started);
            }

            if (s_ownConsole)
            {
                // Serial is raw: if the terminal engine breaks, the UART log has
                // to survive to say so.
                const OutputSink AnyPort = OutputSink.Com1 | OutputSink.Com3 | OutputSink.Com4;
                if ((sinks & AnyPort) != 0)
                {
                    ulong started = OS.Kernel.Diagnostics.PerfCounters.SinkClock();

                    if ((sinks & OutputSink.Com1) != 0)
                        Serial.WriteChar(value);

                    if ((sinks & OutputSink.Com3) != 0)
                        Serial.WriteCharCom3(value);

                    if ((sinks & OutputSink.Com4) != 0)
                        Serial.WriteCharCom4(value);

                    OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.SinkSerialTsc, started);
                }

                if ((sinks & OutputSink.Screen) != 0)
                {
                    ulong started = OS.Kernel.Diagnostics.PerfCounters.SinkClock();

                    if (TerminalConsole.IsReady)
                        TerminalConsole.Putc(value);
                    else
                        FbTty.Putc(value);

                    OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.SinkTerminalTsc, started);
                }
                return;
            }

            // Before ExitBootServices the firmware owns the screen, and ConOut
            // is the only way onto it. The firmware also mirrors ConOut to
            // COM1 on its own, so here everything that reaches the screen
            // reaches COM1 as well, whatever the channel; COM3 and COM4 are
            // only brought up at the switch to our own console.
            if ((sinks & OutputSink.Screen) == 0)
                return;

            if (!s_initialized)
                return;

            if (!HasCapability(PlatformCapabilities.TextOutput))
                return;

            if (s_bootInfo.WriteChar == null)
                return;

            s_bootInfo.WriteChar(value);
        }

        public static void Write(string text)
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try { WriteCore(text); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        private static void WriteCore(string text)
        {
            if (!s_initialized)
                return;

            for (int i = 0; i < text.Length; i++)
                WriteChar(text[i]);

            // Painting is batched: the engine marks dirty rows while the string is fed,
            // and one flush per write draws them.
            TerminalConsole.Flush();
        }

        /// <summary>
        /// Paints whatever has been fed to the terminal engine but not drawn.
        /// </summary>
        /// <remarks>
        /// Putc paints on a line break, and Write paints once per string, which
        /// covers everything that logs. It does not cover a full-screen
        /// application: a text UI positions the cursor and fills cells, and
        /// never writes a newline at all — so its output reached the engine and
        /// stopped there, updating the grid while the screen showed the
        /// previous frame.
        /// </remarks>
        public static void FlushConsole() => TerminalConsole.Flush();

        public static void WriteLine(string text)
        {
            Write(text);
            WriteChar('\n');
            TerminalConsole.Flush();
        }

        // Off by the machine's own means (step210): ACPI S5, then a reset
        // through 0xCF9 and the keyboard controller, and the firmware's
        // ResetSystem only when neither took. The plan is said before the
        // volume is flushed: a line after it would go to a disk that is about
        // to lose power.
        public static void Shutdown()
        {
            if (!s_initialized)
                return;

            Console.Write("[power] off: ");
            if (OS.Hal.Acpi.Power.CanPowerOff)
            {
                Console.Write("ACPI S5, PM1a=0x");
                Console.WriteHex(OS.Hal.Acpi.Power.Pm1a);
                Console.Write(" SLP_TYP=");
                Console.WriteUInt(OS.Hal.Acpi.Power.SlpTypA);
            }
            else
            {
                Console.Write("no S5 (");
                Console.Write(OS.Hal.Acpi.Power.Missing ?? "?");
                Console.Write(")");
            }
            Console.WriteLine(", then reset 0xCF9/0x64, then firmware");

            // What the volume holds, to the medium before the power goes
            // (step208): a stick acknowledges writes it still has in its cache.
            Fat32.FlushForShutdown();

            OS.Hal.Acpi.Power.PowerOff();
            OS.Hal.Acpi.Power.Reset();

            if (HasCapability(PlatformCapabilities.Shutdown) && s_bootInfo.Shutdown != null)
                s_bootInfo.Shutdown();
            Halt();
        }

        // Raw-event variant for the app key service (step143): post-EBS it
        // reports EVERY keyboard byte — presses AND releases — with the set-1
        // make code, so an app (DOOM) can track held keys. `raw` packs:
        //   bits 0..7  set-1 make code
        //   bit  8     extended (0xE0-prefixed)
        //   bit  9     1 = key down (make), 0 = key up (break)
        //   bit  31    raw info present
        // unicodeChar/scanCode carry the legacy launcher mapping for presses
        // (zeros for releases/modifiers — the launcher ignores unmatched
        // keys). Pre-EBS falls back to the legacy UEFI path with raw = 0.
        public static KeyboardReadStatus TryReadKeyRaw(out ushort unicodeChar, out ushort scanCode, out uint raw)
        {
            unicodeChar = 0;
            scanCode = 0;
            raw = 0;

            if (BootServicesGone)
            {
                if (!ScancodeSource.TryReadScancode(out byte psc))
                    return KeyboardReadStatus.NoKey;

                Ps2Keyboard.KeyKind kind = Ps2Keyboard.DecodeEx(
                    psc, out char pch, out byte make, out bool extended, out bool isBreak);

                if (psc == 0xE0)
                    return KeyboardReadStatus.NoKey; // prefix byte — event follows

                raw = (uint)make
                    | (extended ? 0x100u : 0u)
                    | (isBreak ? 0u : 0x200u)
                    | 0x80000000u;

                if (!isBreak)
                {
                    switch (kind)
                    {
                        case Ps2Keyboard.KeyKind.Char: unicodeChar = pch; break;
                        case Ps2Keyboard.KeyKind.Enter: unicodeChar = 0x0D; break;
                        case Ps2Keyboard.KeyKind.Backspace: unicodeChar = 0x08; break;
                        case Ps2Keyboard.KeyKind.Escape: scanCode = 0x17; break;
                        case Ps2Keyboard.KeyKind.Up: scanCode = 0x01; break;
                        case Ps2Keyboard.KeyKind.Down: scanCode = 0x02; break;
                        case Ps2Keyboard.KeyKind.Right: scanCode = 0x03; break;
                        case Ps2Keyboard.KeyKind.Left: scanCode = 0x04; break;
                        default:
                            scanCode = NavigationScanCode(kind);
                            if (scanCode == 0 && !extended) scanCode = FunctionScanCode(make);
                            break;
                    }
                }

                return KeyboardReadStatus.KeyAvailable;
            }

            return TryReadKey(out unicodeChar, out scanCode);
        }

        // The rest of the navigation cluster in UEFI scan codes, as the
        // arrows above (EFI_SIMPLE_TEXT_INPUT: Home 5, End 6, Insert 7,
        // Delete 8, PageUp 9, PageDown 0x0A); 0 for anything else.
        private static ushort NavigationScanCode(Ps2Keyboard.KeyKind kind)
        {
            switch (kind)
            {
                case Ps2Keyboard.KeyKind.Home: return 0x05;
                case Ps2Keyboard.KeyKind.End: return 0x06;
                case Ps2Keyboard.KeyKind.Insert: return 0x07;
                case Ps2Keyboard.KeyKind.Delete: return 0x08;
                case Ps2Keyboard.KeyKind.PageUp: return 0x09;
                case Ps2Keyboard.KeyKind.PageDown: return 0x0A;
                default: return 0;
            }
        }

        // F1–F12 (step217): set-1 make codes 0x3B–0x44, 0x57, 0x58, as UEFI
        // scan codes 0x0B–0x16; 0 for anything else.
        private static ushort FunctionScanCode(byte make)
        {
            if (make >= 0x3B && make <= 0x44) return (ushort)(0x0B + make - 0x3B);
            if (make == 0x57) return 0x15;
            if (make == 0x58) return 0x16;
            return 0;
        }

        public static KeyboardReadStatus TryReadKey(out ushort unicodeChar, out ushort scanCode)
        {
            unicodeChar = 0;
            scanCode = 0;

            // Post-EBS UEFI SimpleTextInput is dead — read the own PS/2
            // instead, mapping to the (unicodeChar, scanCode) the
            // launcher/shell expect (Enter=CR, Esc=SCAN 0x17). Same
            // bridge idea as the file seam.
            if (BootServicesGone)
            {
                if (!ScancodeSource.TryReadScancode(out byte psc))
                    return KeyboardReadStatus.NoKey;
                Ps2Keyboard.KeyKind kind = Ps2Keyboard.Decode(psc, out char pch, out _);
                switch (kind)
                {
                    case Ps2Keyboard.KeyKind.Char:
                        unicodeChar = pch; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Enter:
                        unicodeChar = 0x0D; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Backspace:
                        unicodeChar = 0x08; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Escape:
                        scanCode = 0x17; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Up:
                        scanCode = 0x01; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Down:
                        scanCode = 0x02; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Right:
                        scanCode = 0x03; return KeyboardReadStatus.KeyAvailable;
                    case Ps2Keyboard.KeyKind.Left:
                        scanCode = 0x04; return KeyboardReadStatus.KeyAvailable;
                    default:
                        scanCode = NavigationScanCode(kind);
                        return scanCode != 0 ? KeyboardReadStatus.KeyAvailable
                                             : KeyboardReadStatus.NoKey;     // Control/None
                }
            }

            if (!s_initialized)
                return KeyboardReadStatus.Unsupported;

            if (!HasCapability(PlatformCapabilities.KeyboardInput))
                return KeyboardReadStatus.Unsupported;

            if (s_bootInfo.KeyboardTryReadKey == null)
                return KeyboardReadStatus.Unsupported;

            ushort readUnicodeChar = 0;
            ushort readScanCode = 0;
            uint status = s_bootInfo.KeyboardTryReadKey(&readUnicodeChar, &readScanCode);
            if (status == (uint)BootKeyReadStatus.Ok)
            {
                unicodeChar = readUnicodeChar;
                scanCode = readScanCode;
                return KeyboardReadStatus.KeyAvailable;
            }

            if (status == (uint)BootKeyReadStatus.NotReady)
                return KeyboardReadStatus.NoKey;

            return KeyboardReadStatus.DeviceError;
        }

        public static bool FileExists(string path)
        {
            // Own FS (our FAT over AHCI) takes over once a volume is
            // mounted (post-EBS); until then Fs.Current is null and we
            // fall back to UEFI SimpleFS — so this is inert in the
            // current default boot, the seam for replacing UEFI FS.
            if (Fs.Current != null)
                return Fs.Current.Exists(path);

            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.FileExists == null)
                return false;

            fixed (char* pathPointer = path)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                return s_bootInfo.FileExists(pathPointer) == (uint)BootFileStatus.Ok;
            }
        }

        // Cheap existence + metadata probe. Reads NO file content —
        // PS / BCL GetFileAttributes hits this hundreds of times per
        // assembly load. The old "TryReadFile then discard the buffer"
        // shortcut slurped entire DLLs into NativeArena, drove the
        // AHCI queue, and ran us into physical OOM. Always go through
        // TryStat for existence checks; reserve TryReadFile for code
        // that actually needs the bytes.
        public static bool TryStat(string path, out uint size, out bool isDir)
        {
            size = 0; isDir = false;
            if (Fs.Current != null)
                return Fs.Current.Stat(path, out size, out isDir);

            // Pre-mount fallback: UEFI bridge only knows "exists / not".
            // Treat as zero-size normal file; PS rarely probes this
            // window (we mount FAT very early in boot).
            if (!s_initialized) return false;
            if (!HasCapability(PlatformCapabilities.ExternalElf)) return false;
            if (s_bootInfo.FileExists == null) return false;
            fixed (char* p = path)
            {
                if (p == null || p[0] == '\0') return false;
                if (s_bootInfo.FileExists(p) != (uint)BootFileStatus.Ok) return false;
            }
            return true;   // size=0, isDir=false
        }

        public static bool TryReadFile(string path, out void* buffer, out uint size)
        {
            buffer = null;
            size = 0;

            // Own FS bridge (see FileExists): when a volume is mounted,
            // serve from our FAT instead of UEFI. Two passes — cap=0
            // probes the size (dst unused), then allocate + fill. The
            // host RWX-patches the returned buffer just like the UEFI
            // pool one.
            if (Fs.Current != null)
            {
                ulong probeStart = NowTicks();
                int probe = Fs.Current.ReadFile(path, null, 0, out uint fsz);
                ulong probeEnd = NowTicks();
                if (probe < 0 || fsz == 0)
                    return false;

                bool trace = fsz >= TimedReadThresholdBytes;
                ulong cmd0 = Ahci.ReadCommands;
                ulong sec0 = Ahci.ReadSectors;
                ulong ioTicks0 = Ahci.ReadTicks;
                if (trace)
                {
                    Console.Write("[fsread] start path=\"");
                    Console.Write(path);
                    Console.Write("\" sz=");
                    Console.WriteUInt(fsz);
                    Console.Write(" probe_ms=");
                    Console.WriteULong(TicksToMs(ElapsedTicks(probeStart, probeEnd)));
                    Console.WriteLine("");
                }

                ulong allocStart = NowTicks();
                void* buf = NativeArena.Allocate(fsz);
                ulong allocEnd = NowTicks();
                if (buf == null)
                    return false;

                ulong readStart = NowTicks();
                int read = Fs.Current.ReadFile(path, (byte*)buf, (int)fsz, out fsz);
                ulong readEnd = NowTicks();
                if (read < 0)
                    return false;

                if (trace)
                {
                    Console.Write("[fsread] done sz=");
                    Console.WriteUInt(fsz);
                    Console.Write(" alloc_ms=");
                    Console.WriteULong(TicksToMs(ElapsedTicks(allocStart, allocEnd)));
                    Console.Write(" read_ms=");
                    Console.WriteULong(TicksToMs(ElapsedTicks(readStart, readEnd)));
                    Console.Write(" total_ms=");
                    Console.WriteULong(TicksToMs(ElapsedTicks(probeStart, readEnd)));
                    Console.Write(" ahci_cmds=");
                    Console.WriteULong(Ahci.ReadCommands - cmd0);
                    Console.Write(" sectors=");
                    Console.WriteULong(Ahci.ReadSectors - sec0);
                    Console.Write(" ahci_ms=");
                    Console.WriteULong(TicksToMs(Ahci.ReadTicks - ioTicks0));
                    Console.WriteLine("");
                }

                buffer = buf;
                size = fsz;
                return true;
            }

            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.FileReadAll == null)
                return false;

            fixed (char* pathPointer = path)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                void* readBuffer = null;
                uint readSize = 0;
                uint status = s_bootInfo.FileReadAll(pathPointer, &readBuffer, &readSize);
                if (status != (uint)BootFileStatus.Ok)
                    return false;

                buffer = readBuffer;
                size = readSize;
                return true;
            }
        }

        public static bool TryReadDirectoryEntry(
            string directoryPath,
            uint index,
            char* nameBuffer,
            uint nameBufferChars,
            out uint nameLength,
            out ulong attributes)
        {
            nameLength = 0;
            attributes = 0;

            // Own FS bridge (see TryReadFile): post-mount the launcher
            // enumerates \EFI\BOOT via our FAT, no UEFI.
            if (Fs.Current != null)
            {
                if (nameBuffer == null || nameBufferChars == 0) return false;
                return Fs.Current.EnumDir(directoryPath, index,
                    nameBuffer, nameBufferChars, out nameLength, out attributes);
            }

            if (!s_initialized)
                return false;

            if (!HasCapability(PlatformCapabilities.ExternalElf))
                return false;

            if (s_bootInfo.DirectoryReadEntry == null || nameBuffer == null || nameBufferChars == 0)
                return false;

            fixed (char* pathPointer = directoryPath)
            {
                if (pathPointer == null || pathPointer[0] == '\0')
                    return false;

                uint readLength = 0;
                ulong readAttributes = 0;
                uint status = s_bootInfo.DirectoryReadEntry(
                    pathPointer,
                    index,
                    nameBuffer,
                    nameBufferChars,
                    &readLength,
                    &readAttributes);

                if (status != (uint)BootFileStatus.Ok)
                    return false;

                nameLength = readLength;
                attributes = readAttributes;
                return true;
            }
        }

        public static void Halt()
        {
            // Stop the machine, not just this thread. A bare `while (true)`
            // leaves the CPU running with interrupts on, so the timer keeps
            // firing, the scheduler keeps this thread runnable, and every
            // other thread limps along waiting on a thread that has already
            // died — and the collector waits forever for a safe point it will
            // never reach. Under cooperative scheduling the spin froze
            // everything by accident and looked like a halt; preemption turned
            // the same code into a livelock that reads as a mystery hang.
            X64Asm.Cli();
            while (true) X64Asm.Hlt();
        }
    }
}
