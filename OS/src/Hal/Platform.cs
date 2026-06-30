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
        // every raw bootInfo.* caller (app-service ABI, guest launcher,
        // ELF loader) reads our FAT, not dead UEFI. Called once by
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

        public static void WriteChar(char value)
        {
            if (s_ownConsole)
            {
                Serial.WriteChar(value);
                FbTty.Putc(value);
                return;
            }

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
            if (!s_initialized)
                return;

            for (int i = 0; i < text.Length; i++)
                WriteChar(text[i]);
        }

        public static void WriteLine(string text)
        {
            Write(text);
            WriteChar('\n');
        }

        public static void Shutdown()
        {
            if (!s_initialized)
                return;

            if (!HasCapability(PlatformCapabilities.Shutdown))
            {
                Halt();
                return;
            }

            if (s_bootInfo.Shutdown == null)
            {
                Halt();
                return;
            }

            s_bootInfo.Shutdown();
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
                if (!Ps2Keyboard.TryReadScancode(out byte psc))
                    return KeyboardReadStatus.NoKey;
                switch (Ps2Keyboard.Decode(psc, out char pch, out _))
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
                        return KeyboardReadStatus.NoKey;     // Control/None
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
            while (true) ;
        }
    }
}
