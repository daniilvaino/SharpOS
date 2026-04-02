using OS.Boot;
using OS.Hal;
using OS.Kernel.Exec;
using OS.Kernel.File;
using OS.Kernel.Paging;
using OS.Kernel.Process;
using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static unsafe class ElfValidation
    {
        private const ulong PageSize = X64PageTable.PageSize;
        private const string BootDirectoryPath = "\\EFI\\BOOT";

        private struct ExternalElfApp
        {
            public string Name;
            public string Path;
            public uint AppAbiVersion;
            public int ExpectedExitCode;
            public bool ValidateMarker;
            public bool OptionalIfMissing;
            public AppServiceAbi ServiceAbi;
        }

        public static void Run(BootInfo bootInfo)
        {
            Log.Write(LogLevel.Info, "elf validation start");

            if (!FileSystem.Init())
            {
                Log.Write(LogLevel.Warn, "fs init failed");
                Log.Write(LogLevel.Info, "elf validation done");
                Platform.Shutdown();
                Platform.Halt();
                return;
            }

            Log.Write(LogLevel.Info, "fs init ok");
            FileDiagnostics.DumpDirectory(BootDirectoryPath);

            ExternalElfApp hello = default;
            hello.Name = ElfAppContract.HelloAppName;
            hello.Path = ElfAppContract.HelloAppPath;
            hello.AppAbiVersion = ProcessStartupBlock.AbiVersionV1;
            hello.ExpectedExitCode = ElfAppContract.HelloExitCodeExpected;
            hello.ValidateMarker = false;
            hello.ServiceAbi = AppServiceAbi.WindowsX64;

            ExternalElfApp abiInfo = default;
            abiInfo.Name = ElfAppContract.AbiInfoAppName;
            abiInfo.Path = ElfAppContract.AbiInfoAppPath;
            abiInfo.AppAbiVersion = ProcessStartupBlock.AbiVersionV1;
            abiInfo.ExpectedExitCode = ElfAppContract.AbiInfoExitCodeExpected;
            abiInfo.ValidateMarker = false;
            abiInfo.ServiceAbi = AppServiceAbi.WindowsX64;

            ExternalElfApp helloCs = default;
            helloCs.Name = ElfAppContract.HelloCsAppName;
            helloCs.Path = ElfAppContract.HelloCsAppPath;
            helloCs.AppAbiVersion = ProcessStartupBlock.AbiVersionV2;
            helloCs.ExpectedExitCode = ElfAppContract.HelloCsExitCodeExpected;
            helloCs.ValidateMarker = false;
            helloCs.OptionalIfMissing = true;
            helloCs.ServiceAbi = AppServiceAbi.SystemV;

            ExternalElfApp marker = default;
            marker.Name = ElfAppContract.MarkerAppName;
            marker.Path = ElfAppContract.MarkerAppPath;
            marker.AppAbiVersion = ProcessStartupBlock.AbiVersionV1;
            marker.ExpectedExitCode = ElfAppContract.MarkerExitCodeExpected;
            marker.ValidateMarker = true;
            marker.ServiceAbi = AppServiceAbi.WindowsX64;

            uint passed = 0;
            uint failed = 0;
            RunAppAndAccumulate(ref hello, ref passed, ref failed);
            RunAppAndAccumulate(ref abiInfo, ref passed, ref failed);
            RunAppAndAccumulate(ref helloCs, ref passed, ref failed);
            RunAppAndAccumulate(ref marker, ref passed, ref failed);

            Log.Write(LogLevel.Info, "app batch summary");
            Log.Begin(LogLevel.Info);
            Console.Write("passed: ");
            Console.WriteUInt(passed);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("failed: ");
            Console.WriteUInt(failed);
            Log.EndLine();

            Log.Write(LogLevel.Info, "elf validation done");
            Platform.Shutdown();
            Platform.Halt();
        }

        private static void RunAppAndAccumulate(ref ExternalElfApp app, ref uint passed, ref uint failed)
        {
            if (app.OptionalIfMissing && !FileSystem.Exists(app.Path))
            {
                Log.Begin(LogLevel.Warn);
                Console.Write("optional app not found: ");
                Console.Write(app.Path);
                Log.EndLine();
                return;
            }

            AppRunResult result = RunApp(ref app);
            if (result == AppRunResult.Success)
            {
                passed++;
                return;
            }

            failed++;
            Log.Begin(LogLevel.Warn);
            Console.Write("app failed: ");
            Console.Write(app.Path);
            Console.Write(" reason=");
            Console.Write(ResultName(result));
            Log.EndLine();
        }

        private static AppRunResult RunApp(ref ExternalElfApp app)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("app run start: ");
            Console.Write(app.Path);
            Log.EndLine();

            if (!FileSystem.Exists(app.Path))
                return AppRunResult.FileNotFound;

            if (!FileSystem.ReadAll(app.Path, out FileBuffer fileBuffer))
                return AppRunResult.ReadFailed;

            MemoryBlock image = fileBuffer.AsMemoryBlock();
            Log.Write(LogLevel.Info, "open elf file ok");
            Log.Begin(LogLevel.Info);
            Console.Write("read elf bytes = ");
            Console.WriteUInt(fileBuffer.Length);
            Log.EndLine();

            if (!ElfParser.TryParse(image, out ElfParseResult parseResult, out ElfParseError parseError))
            {
                ElfDiagnostics.WriteParseError(parseError);
                return AppRunResult.ElfParseFailed;
            }

            if (!TryValidateSegments(ref parseResult))
                return AppRunResult.ElfParseFailed;

            ElfDiagnostics.DumpSummary(ref parseResult);

            if (!ElfLoader.TryLoad(ref parseResult, out ElfLoadedImage loadedImage, out ElfLoadError loadError))
            {
                ElfDiagnostics.WriteLoadError(loadError);
                return AppRunResult.ElfLoadFailed;
            }

            ElfLoadValidation.Run(ref parseResult, ref loadedImage);
            ElfDiagnostics.DumpLoadedImage(loadedImage);

            Log.Write(LogLevel.Info, "process build start");
            ulong markerVirtualAddress = app.ValidateMarker ? ElfAppContract.MarkerVirtualAddress : 0;
            if (!ProcessImageBuilder.TryBuild(
                ref loadedImage,
                markerVirtualAddress,
                app.ServiceAbi,
                app.AppAbiVersion,
                out ProcessImage processImage))
            {
                CleanupLoadedImageMappings(ref loadedImage);
                return AppRunResult.ProcessBuildFailed;
            }

            if (!TryValidateProcess(ref processImage, app.AppAbiVersion))
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.ProcessValidationFailed;
            }

            ProcessDiagnostics.DumpSummary(ref processImage);

            Log.Write(LogLevel.Info, "jump start");
            ProcessManager.SetCurrent(ref processImage, ref loadedImage);
            bool jumpOk;
            int returnExitCode = 0;
            try
            {
                jumpOk = JumpStub.Run(
                    processImage.EntryPointPhysical,
                    processImage.StackTopPhysical,
                    processImage.StartupBlockPhysical,
                    out returnExitCode);
            }
            finally
            {
                ProcessManager.ClearCurrent();
            }

            if (!jumpOk)
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            bool exitByService = AppServiceBuilder.TryConsumeExit(out int serviceExitCode);
            processImage.ExitCode = exitByService ? serviceExitCode : returnExitCode;

            if (!exitByService)
                Log.Write(LogLevel.Warn, "process returned without Exit");

            Log.Write(LogLevel.Info, "process returned");
            Log.Begin(LogLevel.Info);
            Console.Write("process exit code = ");
            Console.WriteInt(processImage.ExitCode);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("exit source = ");
            Console.Write(exitByService ? "service" : "return");
            Log.EndLine();

            if (processImage.ExitCode != app.ExpectedExitCode)
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.ExitCodeMismatch;
            }

            if (app.ValidateMarker && !TryVerifyMarker())
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.MarkerMismatch;
            }

            if (!CleanupProcessMappings(ref processImage, ref loadedImage))
                return AppRunResult.MappingCleanupFailed;

            return AppRunResult.Success;
        }

        private static bool TryValidateSegments(ref ElfParseResult result)
        {
            if (result.Header.Type != ElfType.Executable)
            {
                Log.Write(LogLevel.Warn, "unsupported ELF type: only ET_EXEC is supported");
                return false;
            }

            uint loadSegments = 0;

            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                if (!ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header))
                    return false;

                if (header.Type == ElfProgramType.Interpreter)
                {
                    Log.Write(LogLevel.Warn, "unsupported ELF program header: PT_INTERP");
                    return false;
                }

                if (header.Type == ElfProgramType.Dynamic)
                {
                    Log.Write(LogLevel.Warn, "unsupported ELF program header: PT_DYNAMIC");
                    return false;
                }

                if (header.Type != ElfProgramType.Load)
                    continue;

                if (header.FileSize > header.MemorySize)
                    return false;

                if (header.Align != 0)
                {
                    ulong mask = header.Align - 1;
                    if ((header.Align & mask) != 0)
                        return false;
                }

                loadSegments++;
            }

            return loadSegments != 0;
        }

        private static bool TryValidateProcess(ref ProcessImage processImage, uint expectedAbiVersion)
        {
            if (processImage.AbiVersion != expectedAbiVersion)
                return false;

            if (processImage.EntryPoint == 0 ||
                processImage.EntryPointPhysical == 0 ||
                processImage.StackTopPhysical == 0 ||
                processImage.StartupBlockPhysical == 0)
            {
                return false;
            }

            if (!Pager.TryQuery(processImage.EntryPoint, out _, out PageFlags entryFlags))
                return false;

            if ((entryFlags & PageFlags.NoExecute) == PageFlags.NoExecute)
                return false;

            if (!Pager.TryQuery(processImage.StackTop - 1, out _, out PageFlags stackFlags))
                return false;

            if ((stackFlags & PageFlags.Writable) != PageFlags.Writable)
                return false;

            return true;
        }

        private static bool TryVerifyMarker()
        {
            if (!TryReadMappedUInt32(ElfAppContract.MarkerVirtualAddress, out uint markerValue))
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("process wrote marker = 0x");
            Console.WriteHex(markerValue, 8);
            Log.EndLine();

            return markerValue == ElfAppContract.MarkerExpectedValue;
        }

        private static bool TryReadMappedUInt32(ulong virtualAddress, out uint value)
        {
            value = 0;

            for (uint i = 0; i < 4; i++)
            {
                if (!Pager.TryQuery(virtualAddress + i, out ulong physicalAddress, out _))
                    return false;

                value |= ((uint)(*((byte*)physicalAddress)) << (int)(i * 8));
            }

            return true;
        }

        private static void CleanupLoadedImageMappings(ref ElfLoadedImage loadedImage)
        {
            UnmapMappedRange(loadedImage.LowestVirtualAddress, loadedImage.HighestVirtualAddressExclusive);
        }

        private static bool CleanupProcessMappings(ref ProcessImage processImage, ref ElfLoadedImage loadedImage)
        {
            bool imageCleanupOk = UnmapMappedRange(loadedImage.LowestVirtualAddress, loadedImage.HighestVirtualAddressExclusive);
            bool stackCleanupOk = UnmapMappedRange(processImage.StackBase, processImage.StackMappedTop);
            if (!imageCleanupOk || !stackCleanupOk)
                return false;

            Log.Write(LogLevel.Info, "app mappings released");
            return true;
        }

        private static bool UnmapMappedRange(ulong startInclusive, ulong endExclusive)
        {
            if (endExclusive <= startInclusive)
                return true;

            ulong current = AlignDown(startInclusive);
            ulong limit = AlignUp(endExclusive);
            while (current < limit)
            {
                if (Pager.TryQuery(current, out _, out _) && !Pager.Unmap(current))
                    return false;

                if (!TryAdvancePage(ref current))
                    return false;
            }

            return true;
        }

        private static ulong AlignDown(ulong value)
        {
            return value & ~(PageSize - 1);
        }

        private static ulong AlignUp(ulong value)
        {
            ulong mask = PageSize - 1;
            if ((value & mask) == 0)
                return value;

            return (value + mask) & ~mask;
        }

        private static bool TryAdvancePage(ref ulong address)
        {
            if (address > 0xFFFFFFFFFFFFFFFFUL - PageSize)
                return false;

            address += PageSize;
            return true;
        }

        private static string ResultName(AppRunResult result)
        {
            switch (result)
            {
                case AppRunResult.Success: return "Success";
                case AppRunResult.FileNotFound: return "FileNotFound";
                case AppRunResult.ReadFailed: return "ReadFailed";
                case AppRunResult.ElfParseFailed: return "ElfParseFailed";
                case AppRunResult.ElfLoadFailed: return "ElfLoadFailed";
                case AppRunResult.ProcessBuildFailed: return "ProcessBuildFailed";
                case AppRunResult.ProcessValidationFailed: return "ProcessValidationFailed";
                case AppRunResult.JumpFailed: return "JumpFailed";
                case AppRunResult.ExitCodeMismatch: return "ExitCodeMismatch";
                case AppRunResult.MarkerMismatch: return "MarkerMismatch";
                case AppRunResult.MappingCleanupFailed: return "MappingCleanupFailed";
                default: return "Unknown";
            }
        }
    }
}
