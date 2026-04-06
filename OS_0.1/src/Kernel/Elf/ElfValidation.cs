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
        private const ulong KernelLowSyncStart = 0x00100000UL;
        private const ulong KernelLowSyncEndExclusive = 0x20000000UL;

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
            DebugLog.Write(LogLevel.Info, "elf validation start");

            if (!FileSystem.Init())
            {
                DebugLog.Write(LogLevel.Warn, "fs init failed");
                DebugLog.Write(LogLevel.Info, "elf validation done");
                Platform.Shutdown();
                Platform.Halt();
                return;
            }

            DebugLog.Write(LogLevel.Info, "fs init ok");
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

            DebugLog.Write(LogLevel.Info, "app batch summary");
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("passed: ");
            UiText.WriteUInt(passed);
            DebugLog.EndLine();

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("failed: ");
            UiText.WriteUInt(failed);
            DebugLog.EndLine();

            DebugLog.Write(LogLevel.Info, "elf validation done");
            Platform.Shutdown();
            Platform.Halt();
        }

        private static void RunAppAndAccumulate(ref ExternalElfApp app, ref uint passed, ref uint failed)
        {
            if (app.OptionalIfMissing && !FileSystem.Exists(app.Path))
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write("optional app not found: ");
                UiText.Write(app.Path);
                DebugLog.EndLine();
                return;
            }

            AppRunResult result = RunApp(ref app);
            if (result == AppRunResult.Success)
            {
                passed++;
                return;
            }

            failed++;
            DebugLog.Begin(LogLevel.Warn);
            UiText.Write("app failed: ");
            UiText.Write(app.Path);
            UiText.Write(" reason=");
            UiText.Write(ResultName(result));
            DebugLog.EndLine();
        }

        private static AppRunResult RunApp(ref ExternalElfApp app)
        {
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("app run start: ");
            UiText.Write(app.Path);
            DebugLog.EndLine();

            if (!FileSystem.Exists(app.Path))
                return AppRunResult.FileNotFound;

            if (!FileSystem.ReadAll(app.Path, out FileBuffer fileBuffer))
                return AppRunResult.ReadFailed;

            MemoryBlock image = fileBuffer.AsMemoryBlock();
            DebugLog.Write(LogLevel.Info, "open elf file ok");
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("read elf bytes = ");
            UiText.WriteUInt(fileBuffer.Length);
            DebugLog.EndLine();

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

            DebugLog.Write(LogLevel.Info, "process build start");
            ulong markerVirtualAddress = app.ValidateMarker ? ElfAppContract.MarkerVirtualAddress : 0;
            if (!ProcessImageBuilder.TryBuild(
                ref loadedImage,
                markerVirtualAddress,
                app.ServiceAbi,
                app.AppAbiVersion,
                ProcessImageBuilder.DefaultStackMappedTop,
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

            if (!JumpStub.EnsureInitialized())
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            if (!TrySyncKernelLowMappings(ref processImage))
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            if (!TryValidateJumpContext(ref processImage))
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            if (!Pager.TryGetPagerCr3(out ulong pagerCr3))
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            pagerCr3 &= 0x000FFFFFFFFFF000UL;
            if (pagerCr3 == 0)
            {
                CleanupProcessMappings(ref processImage, ref loadedImage);
                return AppRunResult.JumpFailed;
            }

            DebugLog.Write(LogLevel.Info, "jump start");
            ProcessManager.SetCurrent(ref processImage, ref loadedImage);
            bool jumpOk = false;
            int returnExitCode = 0;
            try
            {
                jumpOk = JumpStub.Run(
                    processImage.EntryPoint,
                    processImage.StackTop,
                    processImage.StartupBlockVirtual,
                    pagerCr3,
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
                DebugLog.Write(LogLevel.Warn, "process returned without Exit");

            DebugLog.Write(LogLevel.Info, "process returned");
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("process exit code = ");
            UiText.WriteInt(processImage.ExitCode);
            DebugLog.EndLine();

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("exit source = ");
            UiText.Write(exitByService ? "service" : "return");
            DebugLog.EndLine();

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
                DebugLog.Write(LogLevel.Warn, "unsupported ELF type: only ET_EXEC is supported");
                return false;
            }

            uint loadSegments = 0;

            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                if (!ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header))
                    return false;

                if (header.Type == ElfProgramType.Interpreter)
                {
                    DebugLog.Write(LogLevel.Warn, "unsupported ELF program header: PT_INTERP");
                    return false;
                }

                if (header.Type == ElfProgramType.Dynamic)
                {
                    DebugLog.Write(LogLevel.Warn, "unsupported ELF program header: PT_DYNAMIC");
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
                processImage.StackTop == 0 ||
                processImage.StartupBlockVirtual == 0)
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

        private static bool TrySyncKernelLowMappings(ref ProcessImage processImage)
        {
            uint importedCount = 0;

            for (ulong current = KernelLowSyncStart; current < KernelLowSyncEndExclusive; current += PageSize)
            {
                if (IsInRange(current, processImage.ImageStart, processImage.ImageEnd))
                    continue;

                if (IsInRange(current, processImage.StackBase, processImage.StackMappedTop))
                    continue;

                if (!Pager.TryQueryKernel(current, out ulong kernelPhysical, out PageFlags kernelFlags))
                    continue;

                ulong kernelPagePhysical = kernelPhysical & ~(PageSize - 1);
                PageFlags normalizedKernelFlags = PageFlagOps.NormalizeForMap(kernelFlags);

                // Skip pages already mapped in pager — they were set up intentionally
                // (e.g. JumpStub maps its shellcode page executable; overwriting with kernel
                // CR3 flags would re-add NX on real hardware where firmware uses NX for data).
                if (Pager.TryQuery(current, out _, out _))
                    continue;

                if (!Pager.Map(current, kernelPagePhysical, kernelFlags))
                {
                    DebugLog.Write(LogLevel.Warn, "kernel mapping sync: map failed");
                    return false;
                }

                importedCount++;
            }

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("kernel low sync imported: ");
            UiText.WriteUInt(importedCount);
            DebugLog.EndLine();
            return true;
        }

        private static bool TryValidateJumpContext(ref ProcessImage processImage)
        {
            if (!TryLogMappedAddress("entry map", processImage.EntryPoint, false))
                return false;

            if (!TryLogMappedAddress("stack top map", processImage.StackTop - 1, false))
                return false;

            if (!TryLogMappedAddress("startup block map", processImage.StartupBlockVirtual, false))
                return false;

            if (!TryLogMappedAddress("service table map", processImage.ServiceTableVirtual, false))
                return false;

            if (!JumpStub.TryGetAddress(out ulong jumpStubAddress))
            {
                DebugLog.Write(LogLevel.Warn, "jump context: stub address unavailable");
                return false;
            }

            if (!TryLogMappedAddress("jump stub map", jumpStubAddress, true))
                return false;

            return true;
        }

        private static bool TryLogMappedAddress(string label, ulong virtualAddress, bool requireExecutable)
        {
            if (!Pager.TryQuery(virtualAddress, out ulong physicalAddress, out PageFlags flags))
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write(label);
                UiText.Write(": unmapped vaddr=0x");
                UiText.WriteHex(virtualAddress, 16);
                DebugLog.EndLine();
                return false;
            }

            if (requireExecutable && (flags & PageFlags.NoExecute) == PageFlags.NoExecute)
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write(label);
                UiText.Write(": NX vaddr=0x");
                UiText.WriteHex(virtualAddress, 16);
                UiText.Write(" paddr=0x");
                UiText.WriteHex(physicalAddress, 16);
                DebugLog.EndLine();
                return false;
            }

            DebugLog.Begin(LogLevel.Info);
            UiText.Write(label);
            UiText.Write(": vaddr=0x");
            UiText.WriteHex(virtualAddress, 16);
            UiText.Write(" paddr=0x");
            UiText.WriteHex(physicalAddress, 16);
            UiText.Write(" flags=0x");
            UiText.WriteHex((ulong)flags, 16);
            DebugLog.EndLine();
            return true;
        }

        private static bool IsInRange(ulong address, ulong startInclusive, ulong endExclusive)
        {
            return address >= startInclusive && address < endExclusive;
        }

        private static bool TryVerifyMarker()
        {
            if (!TryReadMappedUInt32(ElfAppContract.MarkerVirtualAddress, out uint markerValue))
                return false;

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("process wrote marker = 0x");
            UiText.WriteHex(markerValue, 8);
            DebugLog.EndLine();

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

            DebugLog.Write(LogLevel.Info, "app mappings released");
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

