namespace SharpOS.AppSdk
{
    internal static unsafe class AppHost
    {
        private const int MaxTempTextChars = 512;
        private const int MaxTempPathChars = 260;

        public static void WriteString(byte* text)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || text == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteStringAddress;
            if (write == null)
                return;

            write((ulong)text);
        }

        public static void WriteString(string text)
        {
            if (text == null || text.Length == 0)
                return;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            // One service call per chunk, encoded as UTF-8.
            //
            // This used to hand over one character at a time, which was correct
            // and expensive: a full-screen repaint became thousands of calls,
            // and — worse — the kernel paints the screen at the END of a write,
            // so a stream of single characters never finished a frame. Text
            // appeared over serial and the display kept the previous picture.
            //
            // The ASCII fallback that used to sit here could not carry the box
            // drawing a UI is made of, which is why the character path existed
            // at all.
            if (services->WriteStringAddress != 0)
            {
                WriteUtf8Chunks(text, (delegate* unmanaged<ulong, void>)services->WriteStringAddress);
                return;
            }

            delegate* unmanaged<uint, void> writeChar = (delegate* unmanaged<uint, void>)services->WriteCharAddress;
            if (writeChar == null) return;

            fixed (char* source = text)
            {
                for (int i = 0; i < text.Length; i++)
                    writeChar((uint)source[i]);
            }
        }

        /// <summary>
        /// Writes to the application's error stream.
        /// </summary>
        /// <remarks>
        /// What the process would send to stderr: the kernel keeps it apart
        /// from ordinary output (its own port and log file where the machine
        /// has one), so a failure can be found without reading everything
        /// printed around it. The screen shows both, in order. On a kernel that
        /// does not publish the service it goes out as ordinary output rather
        /// than nowhere.
        /// </remarks>
        public static void WriteError(string text)
        {
            if (text == null || text.Length == 0)
                return;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            if (services->WriteErrorAddress == 0)
            {
                WriteString(text);
                return;
            }

            WriteUtf8Chunks(text, (delegate* unmanaged<ulong, void>)services->WriteErrorAddress);
        }

        /// <summary>
        /// Diagnostics for the log, never for the screen.
        /// </summary>
        /// <remarks>
        /// Ordinary output and error output both paint. An application drawing
        /// a full-screen interface therefore cannot report through either —
        /// the report lands in the middle of what it describes, which is what
        /// a heap census from the launcher did. This goes where measurements
        /// go: the serial port and the log.
        ///
        /// Silent on a kernel that does not publish it. Deliberately NOT
        /// falling back to ordinary output: the whole point is not painting,
        /// and a fallback that paints would defeat it exactly where it matters.
        /// </remarks>
        public static void WriteDiagnostic(string text)
        {
            if (text == null || text.Length == 0)
                return;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->WriteDiagnosticAddress == 0)
                return;

            WriteUtf8Chunks(text, (delegate* unmanaged<ulong, void>)services->WriteDiagnosticAddress);
        }

        /// <summary>
        /// The same, from a buffer the caller already has: no string, no
        /// allocation.
        /// </summary>
        /// <remarks>
        /// For reports that must come out at the moment memory ran out — the
        /// collector runs from inside the allocator, and composing a string
        /// there would ask the allocator that just failed, and recurse.
        /// </remarks>
        public static void WriteDiagnostic(byte* utf8)
        {
            if (utf8 == null) return;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->WriteDiagnosticAddress == 0)
                return;

            var write = (delegate* unmanaged<ulong, void>)services->WriteDiagnosticAddress;
            write((ulong)utf8);
        }

        /// <summary>Whether the kernel takes diagnostics that do not paint.</summary>
        public static bool HasDiagnosticStream
        {
            get
            {
                AppServiceTable* services = AppRuntime.Services;
                return services != null && services->WriteDiagnosticAddress != 0;
            }
        }

        /// <summary>Whether the kernel keeps an error stream apart from output.</summary>
        public static bool HasErrorStream
        {
            get
            {
                AppServiceTable* services = AppRuntime.Services;
                return services != null && services->WriteErrorAddress != 0;
            }
        }

        // 4 KiB at a time. The kernel's own limit is larger, but this buffer is
        // on the stack and a chunk boundary costs only an extra call.
        private const int WriteChunkBytes = 4096;

        private static void WriteUtf8Chunks(string text, delegate* unmanaged<ulong, void> write)
        {
            byte* buffer = stackalloc byte[WriteChunkBytes + 1];
            int used = 0;

            fixed (char* source = text)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    // Never split a character across two calls: the far side
                    // decodes each chunk on its own, and half a rune is a
                    // different rune.
                    if (used + 4 > WriteChunkBytes)
                    {
                        buffer[used] = 0;
                        write((ulong)buffer);
                        used = 0;
                    }

                    used += EncodeUtf8(source, text.Length, ref i, buffer + used);
                }
            }

            if (used > 0)
            {
                buffer[used] = 0;
                write((ulong)buffer);
            }
        }

        /// <summary>
        /// Encodes one codepoint, consuming a surrogate pair when it finds one.
        /// Returns the number of bytes written.
        /// </summary>
        private static int EncodeUtf8(char* source, int length, ref int index, byte* destination)
        {
            uint value = source[index];

            if (value >= 0xD800 && value <= 0xDBFF && index + 1 < length)
            {
                uint low = source[index + 1];
                if (low >= 0xDC00 && low <= 0xDFFF)
                {
                    value = ((value - 0xD800u) << 10) + (low - 0xDC00u) + 0x10000u;
                    index++;
                }
            }

            if (value < 0x80)
            {
                destination[0] = (byte)value;
                return 1;
            }

            if (value < 0x800)
            {
                destination[0] = (byte)(0xC0 | (value >> 6));
                destination[1] = (byte)(0x80 | (value & 0x3F));
                return 2;
            }

            if (value < 0x10000)
            {
                destination[0] = (byte)(0xE0 | (value >> 12));
                destination[1] = (byte)(0x80 | ((value >> 6) & 0x3F));
                destination[2] = (byte)(0x80 | (value & 0x3F));
                return 3;
            }

            destination[0] = (byte)(0xF0 | (value >> 18));
            destination[1] = (byte)(0x80 | ((value >> 12) & 0x3F));
            destination[2] = (byte)(0x80 | ((value >> 6) & 0x3F));
            destination[3] = (byte)(0x80 | (value & 0x3F));
            return 4;
        }

        public static void WriteBuildId()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<void> writeBuildId = (delegate* unmanaged<void>)services->WriteBuildIdAddress;
            if (writeBuildId == null)
                return;

            writeBuildId();
        }

        public static void WriteChar(char c)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> writeChar = (delegate* unmanaged<uint, void>)services->WriteCharAddress;
            if (writeChar == null)
                return;

            writeChar((uint)c);
        }

        public static void WriteUInt(uint value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> write = (delegate* unmanaged<uint, void>)services->WriteUIntAddress;
            if (write == null)
                return;

            write(value);
        }

        public static void WriteHex(ulong value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteHexAddress;
            if (write == null)
                return;

            write(value);
        }

        public static uint GetAbiVersion()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return 0;

            delegate* unmanaged<uint> getVersion = (delegate* unmanaged<uint>)services->GetAbiVersionAddress;
            if (getVersion == null)
                return 0;

            return getVersion();
        }

        public static void Exit(int exitCode)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<int, void> exit = (delegate* unmanaged<int, void>)services->ExitAddress;
            if (exit == null)
                return;

            exit(exitCode);
        }

        public static bool FileExists(byte* path)
        {
            return FileExistsEx(path) == AppServiceStatus.Ok;
        }

        public static bool FileExists(string path)
        {
            return FileExistsEx(path) == AppServiceStatus.Ok;
        }

        public static AppServiceStatus FileExistsEx(byte* path)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (path == null)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> fileExists = (delegate* unmanaged<ulong, uint>)services->FileExistsAddress;
            if (fileExists == null)
                return AppServiceStatus.Unsupported;

            AppFileExistsRequest request = default;
            request.PathAddress = (ulong)path;
            return (AppServiceStatus)fileExists((ulong)(&request));
        }

        public static AppServiceStatus FileExistsEx(string path)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
                return AppServiceStatus.InvalidParameter;

            return FileExistsEx(pathBuffer);
        }

        public static AppServiceStatus TryReadFile(byte* path, byte* buffer, uint bufferCapacity, out uint bytesRead)
        {
            bytesRead = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (path == null || buffer == null || bufferCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> readFile = (delegate* unmanaged<ulong, uint>)services->ReadFileAddress;
            if (readFile == null)
                return AppServiceStatus.Unsupported;

            AppReadFileRequest request = default;
            request.PathAddress = (ulong)path;
            request.BufferAddress = (ulong)buffer;
            request.BufferCapacity = bufferCapacity;

            AppServiceStatus status = (AppServiceStatus)readFile((ulong)(&request));
            bytesRead = request.BytesRead;
            return status;
        }

        public static AppServiceStatus TryReadFile(string path, byte* buffer, uint bufferCapacity, out uint bytesRead)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
            {
                bytesRead = 0;
                return AppServiceStatus.InvalidParameter;
            }

            return TryReadFile(pathBuffer, buffer, bufferCapacity, out bytesRead);
        }

        /// <summary>
        /// The whole USB tree, flattened into the caller's array: one node per
        /// controller, device, interface and endpoint, linked by parent index.
        /// </summary>
        /// <remarks>
        /// Checked by address, not by ABI version. These services were added at
        /// the tail of the table without moving anything, so an app that runs
        /// on an older kernel gets a zero here and can say so, instead of
        /// refusing to start over a feature it may not even use.
        /// </remarks>
        public static AppServiceStatus TryUsbEnumerate(AppUsbNode* nodes, uint capacity,
                                                       out uint count)
        {
            count = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null) return AppServiceStatus.Unsupported;
            if (nodes == null || capacity == 0) return AppServiceStatus.InvalidParameter;

            delegate* unmanaged<ulong, uint> enumerate =
                (delegate* unmanaged<ulong, uint>)services->UsbEnumerateAddress;
            if (enumerate == null) return AppServiceStatus.Unsupported;

            AppUsbEnumerateRequest request = default;
            request.BufferAddress = (ulong)nodes;
            request.Capacity = capacity;

            uint status = enumerate((ulong)(&request));
            count = request.Count;
            return (AppServiceStatus)status;
        }

        /// <summary>
        /// One CTAPHID exchange. Channel 0 asks the key to introduce itself and
        /// hands back the channel it allocated, along with its protocol version
        /// and capabilities packed into <paramref name="request"/>.Command.
        /// </summary>
        public static AppServiceStatus TryUsbCtap(ref AppUsbCtapRequest request)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null) return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> ctap =
                (delegate* unmanaged<ulong, uint>)services->UsbCtapAddress;
            if (ctap == null) return AppServiceStatus.Unsupported;

            fixed (AppUsbCtapRequest* p = &request)
                return (AppServiceStatus)ctap((ulong)p);
        }

        /// <summary>Does this kernel hand over the USB bus at all?</summary>
        public static bool HasUsbServices
        {
            get
            {
                AppServiceTable* services = AppRuntime.Services;
                return services != null && services->UsbEnumerateAddress != 0;
            }
        }

        public static AppServiceStatus TryReadDirEntry(
            byte* directoryPath,
            uint entryIndex,
            byte* nameBuffer,
            uint nameBufferCapacity,
            out FileEntry entry)
        {
            entry = default;

            AppServiceTable* services = AppRuntime.Services;
            if (directoryPath == null || nameBuffer == null || nameBufferCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> readDirEntry = (delegate* unmanaged<ulong, uint>)services->ReadDirEntryAddress;
            if (readDirEntry == null)
                return AppServiceStatus.Unsupported;

            AppReadDirectoryEntryRequest request = default;
            request.DirectoryPathAddress = (ulong)directoryPath;
            request.EntryIndex = entryIndex;
            request.NameBufferAddress = (ulong)nameBuffer;
            request.NameBufferCapacity = nameBufferCapacity;

            AppServiceStatus status = (AppServiceStatus)readDirEntry((ulong)(&request));
            entry.NameLength = request.NameLength;
            entry.IsDirectory = request.IsDirectory;
            return status;
        }

        public static AppServiceStatus TryReadDirEntry(
            string directoryPath,
            uint entryIndex,
            byte* nameBuffer,
            uint nameBufferCapacity,
            out FileEntry entry)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(directoryPath, pathBuffer, MaxTempPathChars + 1, out _))
            {
                entry = default;
                return AppServiceStatus.InvalidParameter;
            }

            return TryReadDirEntry(pathBuffer, entryIndex, nameBuffer, nameBufferCapacity, out entry);
        }

        public static AppServiceStatus TryReadKey(out KeyInfo key)
        {
            key = default;

            AppServiceTable* services = AppRuntime.Services;
            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> tryReadKey = (delegate* unmanaged<ulong, uint>)services->TryReadKeyAddress;
            if (tryReadKey == null)
                return AppServiceStatus.Unsupported;

            AppReadKeyRequest request = default;
            AppServiceStatus status = (AppServiceStatus)tryReadKey((ulong)(&request));
            key.UnicodeChar = request.UnicodeChar;
            key.ScanCode = request.ScanCode;
            key.Raw = request.Reserved; // set-1 make/break event (step143)
            return status;
        }

        // HPET time source from the service table (step143). False when the
        // kernel has no HPET. The counter MMIO is identity-mapped in the
        // shared address space — read it directly (via a NoInlining reader:
        // ILC hoists non-volatile MMIO reads out of spin loops, see limits).
        public static bool TryGetHpet(out ulong counterAddress, out ulong frequencyHz)
        {
            counterAddress = 0;
            frequencyHz = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->HpetCounterAddress == 0 || services->HpetFrequencyHz == 0)
                return false;

            counterAddress = services->HpetCounterAddress;
            frequencyHz = services->HpetFrequencyHz;
            return true;
        }

        public static AppServiceStatus TryRunApp(
            byte* path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            out int exitCode)
            => TryRunApp(path, appAbiVersion, serviceAbi, null, 0, out exitCode);

        // arguments: UTF-8 strings, each ended by a NUL, argumentsLength bytes.
        private static AppServiceStatus TryRunApp(
            byte* path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            byte* arguments,
            uint argumentsLength,
            out int exitCode)
        {
            exitCode = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (path == null)
                return AppServiceStatus.InvalidParameter;

            if (!HasAbiV2Services(services))
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> runApp = (delegate* unmanaged<ulong, uint>)services->RunAppAddress;
            if (runApp == null)
                return AppServiceStatus.Unsupported;

            AppRunAppRequest request = default;
            request.PathAddress = (ulong)path;
            request.AppAbiVersion = appAbiVersion;
            request.ServiceAbi = (uint)serviceAbi;
            request.ExitCode = 0;
            if (argumentsLength != 0)
            {
                request.Reserved = AppRunAppRequest.FlagHasArguments;
                request.ArgumentsAddress = (ulong)arguments;
                request.ArgumentsLength = argumentsLength;
            }

            AppServiceStatus status = (AppServiceStatus)runApp((ulong)(&request));
            exitCode = request.ExitCode;
            return status;
        }

        public static AppServiceStatus TryRunApp(byte* path, out int exitCode)
        {
            return TryRunApp(path, AppServiceTable.AutoSelectAbiVersion, AppServiceAbi.Auto, out exitCode);
        }

        public static AppServiceStatus TryRunApp(
            string path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            out int exitCode)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
            {
                exitCode = 0;
                return AppServiceStatus.InvalidParameter;
            }

            return TryRunApp(pathBuffer, appAbiVersion, serviceAbi, out exitCode);
        }

        public static AppServiceStatus TryRunApp(string path, out int exitCode)
        {
            return TryRunApp(path, AppServiceTable.AutoSelectAbiVersion, AppServiceAbi.Auto, out exitCode);
        }

        /// <summary>
        /// Runs a program with arguments; it reads them from
        /// <see cref="Arguments"/>. Unsupported on a kernel without startup
        /// data, rather than starting the program without them.
        /// </summary>
        public static AppServiceStatus TryRunApp(string path, string[] arguments, out int exitCode)
        {
            if (arguments == null || arguments.Length == 0)
                return TryRunApp(path, out exitCode);

            exitCode = 0;
            if (!KernelTakesArguments)
                return AppServiceStatus.Unsupported;

            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
                return AppServiceStatus.InvalidParameter;

            int total = 0;
            byte[][] encoded = new byte[arguments.Length][];
            for (int i = 0; i < arguments.Length; i++)
            {
                encoded[i] = System.Text.Encoding.UTF8.GetBytes(arguments[i] ?? "");
                total += encoded[i].Length + 1;
            }
            byte[] list = new byte[total];
            int at = 0;
            for (int i = 0; i < encoded.Length; i++)
            {
                for (int k = 0; k < encoded[i].Length; k++)
                    list[at++] = encoded[i][k];
                list[at++] = 0;
            }

            fixed (byte* listPointer = list)
                return TryRunApp(pathBuffer, AppServiceTable.AutoSelectAbiVersion, AppServiceAbi.Auto,
                                 listPointer, (uint)total, out exitCode);
        }

        /// <summary>
        /// A zeroed block of the exchange heap, owned by this run: memory no
        /// collector owns, for what goes to another program. Null when the
        /// kernel does not offer it or has no memory.
        /// </summary>
        public static void* ExchangeAllocate(ulong size)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->ExchangeAllocateAddress == 0)
                return null;
            return ((delegate* unmanaged<ulong, void*>)services->ExchangeAllocateAddress)(size);
        }

        /// <summary>Gives a block back; false when it is not a live block of this run.</summary>
        public static bool ExchangeFree(void* block)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->ExchangeFreeAddress == 0)
                return false;
            return ((delegate* unmanaged<void*, uint>)services->ExchangeFreeAddress)(block) != 0;
        }

        /// <summary>Whether the kernel takes regions (an experiment, see RegionToKernelAddress).</summary>
        public static bool HasRegionToKernel
            => AppRuntime.Services != null && AppRuntime.Services->RegionToKernelAddress != 0;

        /// <summary>
        /// Hands an exchange block holding a region to the kernel, with the
        /// schema that describes it. The block is the kernel's afterwards.
        /// Answers the kernel's failure count; negative when it refused.
        /// </summary>
        public static int RegionToKernel(void* region, ulong length, byte* schema, ulong schemaLength)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->RegionToKernelAddress == 0)
                return -100;
            return ((delegate* unmanaged<void*, ulong, void*, ulong, int>)services->RegionToKernelAddress)(
                region, length, schema, schemaLength);
        }

        /// <summary>
        /// Takes a region the kernel writes for this run: two exchange blocks
        /// this run owns, the region and its schema. Answers 0, negative when
        /// the kernel could not or does not offer it.
        /// </summary>
        public static int RegionFromKernel(out byte* region, out ulong length, out byte* schema, out ulong schemaLength)
        {
            region = null;
            schema = null;
            length = 0;
            schemaLength = 0;
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->RegionFromKernelAddress == 0)
                return -100;
            ulong* answer = stackalloc ulong[4];
            int result = ((delegate* unmanaged<ulong*, int>)services->RegionFromKernelAddress)(answer);
            if (result == 0)
            {
                region = (byte*)answer[0];
                length = answer[1];
                schema = (byte*)answer[2];
                schemaLength = answer[3];
            }
            return result;
        }

        private static string[] s_arguments;

        /// <summary>What this program was started with; empty, never null.</summary>
        public static string[] Arguments => s_arguments ?? new string[0];

        // A kernel that reads FlagHasArguments publishes PreemptionDepthAddress
        // too (both arrived together); one that does not would start the child
        // with the arguments silently dropped.
        private static bool KernelTakesArguments
            => AppRuntime.Services != null && AppRuntime.Services->PreemptionDepthAddress != 0;

        // The arguments records of the startup data block (kernel StartupData).
        internal static void LoadArguments(AppServiceTable* services)
        {
            if (services == null || services->StartupDataAddress == 0 || services->StartupDataLength < 16)
                return;

            byte* block = (byte*)services->StartupDataAddress;
            uint length = services->StartupDataLength;
            if (*(uint*)block != 0x54445353) return;      // "SSDT"
            uint count = *(uint*)(block + 4);

            int arguments = 0;
            for (uint at = 16, n = 0; n < count && at + 8 <= length; n++)
            {
                uint len = *(uint*)(block + at + 4);
                if (*(uint*)(block + at) == 1) arguments++;
                at += 8 + ((len + 7) & ~7u);
            }

            string[] result = new string[arguments];
            int index = 0;
            for (uint at = 16, n = 0; n < count && at + 8 <= length; n++)
            {
                uint kind = *(uint*)(block + at);
                uint len = *(uint*)(block + at + 4);
                if (at + 8 + len > length) break;
                if (kind == 1)
                    result[index++] = System.Text.Encoding.UTF8.GetString(
                        new System.ReadOnlySpan<byte>(block + at + 8, (int)len));
                at += 8 + ((len + 7) & ~7u);
            }
            s_arguments = result;
        }

        /// <summary>
        /// Runs a managed assembly on the kernel's hosted CoreCLR and waits.
        /// </summary>
        /// <remarks>
        /// Unsupported when the kernel published no such service — a build
        /// without CoreCLR, or an older kernel. Detected by the address being
        /// zero, which is how every service appended to the table is detected;
        /// the ABI version does not move for a new pointer.
        /// </remarks>
        public static AppServiceStatus TryRunManagedApp(byte* path, out int exitCode)
        {
            exitCode = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (path == null)
                return AppServiceStatus.InvalidParameter;

            if (services == null)
                return AppServiceStatus.Unsupported;

            delegate* unmanaged<ulong, uint> runManaged =
                (delegate* unmanaged<ulong, uint>)services->RunManagedAppAddress;
            if (runManaged == null)
                return AppServiceStatus.Unsupported;

            AppRunManagedRequest request = default;
            request.PathAddress = (ulong)path;
            request.ExitCode = 0;

            AppServiceStatus status = (AppServiceStatus)runManaged((ulong)(&request));
            exitCode = request.ExitCode;
            return status;
        }

        public static AppServiceStatus TryRunManagedApp(string path, out int exitCode)
        {
            byte* pathBuffer = stackalloc byte[MaxTempPathChars + 1];
            if (!TryEncodeAscii(path, pathBuffer, MaxTempPathChars + 1, out _))
            {
                exitCode = 0;
                return AppServiceStatus.InvalidParameter;
            }

            return TryRunManagedApp(pathBuffer, out exitCode);
        }

        /// <summary>True when this kernel can host managed assemblies.</summary>
        public static bool CanRunManagedApps
        {
            get
            {
                AppServiceTable* services = AppRuntime.Services;
                return services != null && services->RunManagedAppAddress != 0;
            }
        }

        // GOP framebuffer geometry from the service table (step143). False on
        // headless boots (Base==0) or when services are absent. The FB memory
        // itself is identity-mapped in the shared address space — the caller
        // writes pixels directly at Base.
        public static bool TryGetFramebuffer(
            out ulong baseAddress, out uint width, out uint height, out uint stridePixels, out uint pixelFormat)
        {
            baseAddress = 0;
            width = 0;
            height = 0;
            stridePixels = 0;
            pixelFormat = 0;

            AppServiceTable* services = AppRuntime.Services;
            if (services == null || services->FramebufferBase == 0)
                return false;

            baseAddress = services->FramebufferBase;
            width = services->FramebufferWidth;
            height = services->FramebufferHeight;
            stridePixels = services->FramebufferStride;
            pixelFormat = services->FramebufferPixelFormat;
            return true;
        }

        public static bool TryEncodeAscii(string text, byte* destination, int destinationCapacity, out uint bytesWritten)
        {
            bytesWritten = 0;
            if (destination == null || destinationCapacity <= 0 || text == null)
                return false;

            int textLength = text.Length;
            if (textLength + 1 > destinationCapacity)
                return false;

            fixed (char* source = text)
            {
                for (int i = 0; i < textLength; i++)
                {
                    char value = source[i];
                    destination[i] = value <= 0x7F ? (byte)value : (byte)'?';
                }
            }

            destination[textLength] = 0;
            bytesWritten = (uint)textLength;
            return true;
        }

        private static bool HasAbiV2Services(AppServiceTable* services)
        {
            return services != null && services->AbiVersion >= AppServiceTable.AbiVersionV2;
        }
    }
}
