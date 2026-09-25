using OS.Boot;
using OS.Hal;
using OS.Kernel.Exec;
using OS.Kernel.File;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    /// <summary>
    /// The end of boot: mount the file system and start the launcher, a
    /// freestanding PE the kernel loads, maps and jumps into.
    /// </summary>
    /// <remarks>
    /// Was ElfValidation, from when this phase ran a batch of ELF test apps
    /// and checked their exit codes and a marker each wrote. ELF went in
    /// step137 and the batch shrank to the launcher; the name, the marker
    /// check and an ELF segment validator stayed until step171.
    /// </remarks>
    internal static unsafe class LauncherBoot
    {
        private const ulong PageSize = X64PageTable.PageSize;
        // Where applications live. \EFI\BOOT holds the firmware entry point and
        // nothing else worth listing.
        private const string AppDirectoryPath = "\\apps";
        private const ulong KernelLowSyncStart = 0x00100000UL;
        private const ulong KernelLowSyncEndExclusive = 0x20000000UL;

        // The launcher the kernel starts after boot: a Terminal.Gui
        // application (step163). Being started BY the kernel rather than by
        // another launcher is the point — the process model keeps one
        // suspended context, so anything it starts would otherwise be refused.
        private const string LauncherPath = "\\apps\\LAUNCHER.EXE";

        // A script on the volume means the machine has work to do with nobody
        // at it: the shell runs the lines and exits, and the batch ends the way
        // it always does — with Shutdown below. This is how the test rig gets a
        // full run out of a boot without a keypress.
        private const string ShellPath = "\\apps\\SHELL.EXE";
        private const string AutorunScriptPath = "\\apps\\AUTORUN.SH";

        // It exits cleanly when the user leaves it.
        private const int LauncherExitCodeExpected = 0;

        private struct BootApp
        {
            public string Path;
            public uint AppAbiVersion;
            public int ExpectedExitCode;
            public bool OptionalIfMissing;
            public AppServiceAbi ServiceAbi;
        }

        public static void Run(BootInfo bootInfo)
        {
            DebugLog.Write(LogLevel.Info, "pe launcher start");

            // Panic rather than shut down. On a real machine a quiet
            // Shutdown() here is indistinguishable from a clean finish: the
            // box powers off having run nothing, and the reason is gone with
            // it. A panic keeps the screen up long enough to read.
            if (!FileSystem.Init())
                OS.Kernel.Panic.Fail("no boot disk: " + OS.Hal.BootDisk.MissingReason
                                     + " — the launcher and every app load from it; nothing to run");

            DebugLog.Write(LogLevel.Info, "fs init ok");
            FileDiagnostics.DumpDirectory(AppDirectoryPath);

            // PeLoader flattens, maps and jumps it; WindowsX64 service ABI.
            BootApp launcher = default;

            bool unattended = FileSystem.Exists(AutorunScriptPath)
                              && FileSystem.Exists(ShellPath);
            if (unattended)
                DebugLog.Write(LogLevel.Info, "autorun script found: starting the shell, not the launcher");

            launcher.Path = unattended ? ShellPath : LauncherPath;
            // Follows CurrentAbiVersion rather than naming a number: pinned to
            // V2 it kept working after V3 landed, which hid the version
            // mismatch that broke every app launched from the launcher.
            launcher.AppAbiVersion = ProcessStartupBlock.CurrentAbiVersion;
            launcher.ExpectedExitCode = LauncherExitCodeExpected;
            launcher.OptionalIfMissing = true;
            launcher.ServiceAbi = AppServiceAbi.WindowsX64;

            uint passed = 0;
            uint failed = 0;
            RunAppAndAccumulate(ref launcher, ref passed, ref failed);

            DebugLog.Write(LogLevel.Info, "app batch summary");
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("passed: ");
            UiText.WriteUInt(passed);
            DebugLog.EndLine();

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("failed: ");
            UiText.WriteUInt(failed);
            DebugLog.EndLine();

            // "passed: 0 / failed: 0" reads as a green batch while meaning the
            // opposite — every app was optional and none was on the disk.
            if (passed == 0 && failed == 0)
                OS.Kernel.Panic.Fail("app batch ran nothing — no app image on disk");

            DebugLog.Write(LogLevel.Info, "pe launcher done");
            Platform.Shutdown();
            Platform.Halt();
        }

        private static void RunAppAndAccumulate(ref BootApp app, ref uint passed, ref uint failed)
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
                // Success said nothing, so "app run start" was the last word on
                // this app and a reader could not tell a finished run from one
                // that died mid-way. Failure always announced itself; success
                // has to as well, or the log only proves that apps break.
                DebugLog.Begin(LogLevel.Info);
                UiText.Write("app run ok: ");
                UiText.Write(app.Path);
                DebugLog.EndLine();
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

        private static AppRunResult RunApp(ref BootApp app)
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
            DebugLog.Write(LogLevel.Info, "open app file ok");
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("read app bytes = ");
            UiText.WriteUInt(fileBuffer.Length);
            DebugLog.EndLine();

            // Apps are freestanding win-x64 PEs (see build.ps1).
            // PeLoader flattens + maps the image at its ImageBase and yields
            // the LoadedImage the ProcessImageBuilder pipeline below consumes.
            if (!global::OS.Kernel.Pe.PeLoader.TryLoad(image, out LoadedImage loadedImage, out int peStage))
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write("pe load failed at stage = ");
                UiText.WriteInt(peStage);
                DebugLog.EndLine();
                return AppRunResult.ImageLoadFailed;
            }

            DebugLog.Write(LogLevel.Info, "process build start");
            if (!ProcessImageBuilder.TryBuild(
                ref loadedImage,
                0,
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
            uint previousGeneration = OS.Kernel.Threading.Scheduler.EnterApp(out uint appGeneration);
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
                AppServiceBuilder.EndAppRun(appGeneration, previousGeneration);
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

            if (!CleanupProcessMappings(ref processImage, ref loadedImage))
                return AppRunResult.MappingCleanupFailed;

            return AppRunResult.Success;
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

        private static void CleanupLoadedImageMappings(ref LoadedImage loadedImage)
        {
            UnmapMappedRange(loadedImage.LowestVirtualAddress, loadedImage.HighestVirtualAddressExclusive);
        }

        private static bool CleanupProcessMappings(ref ProcessImage processImage, ref LoadedImage loadedImage)
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

            // Drop any managed-EH .pdata registration for this base (step140).
            // No-op unless startInclusive is a registered app image base (i.e.
            // not for stack ranges).
            global::OS.Boot.EH.CoffRuntimeFunctionTable.UnregisterImage((byte*)startInclusive);

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
                case AppRunResult.ImageLoadFailed: return "ImageLoadFailed";
                case AppRunResult.ProcessBuildFailed: return "ProcessBuildFailed";
                case AppRunResult.ProcessValidationFailed: return "ProcessValidationFailed";
                case AppRunResult.JumpFailed: return "JumpFailed";
                case AppRunResult.ExitCodeMismatch: return "ExitCodeMismatch";
                case AppRunResult.MappingCleanupFailed: return "MappingCleanupFailed";
                default: return "Unknown";
            }
        }
    }
}
