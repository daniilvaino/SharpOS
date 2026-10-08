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
            {
                DebugLog.Write(LogLevel.Info, "autorun script found: starting the shell, not the launcher");
                // A battery's log says which build of each program ran; a prompt does not need it.
                AppServiceBuilder.Settings |= AppServiceTable.SettingAnnounceBuild;
            }

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

            // Must be zero: an allocation from an interrupt handler can land in
            // the middle of the interrupted thread's own allocation.
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("kernel allocations inside interrupts: ");
            UiText.WriteULong(SharpOS.Std.NoRuntime.GcHeap.AllocationsWhereForbidden);
            UiText.Write(" last size=");
            UiText.WriteUInt(SharpOS.Std.NoRuntime.GcHeap.LastForbiddenSize);
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

            // The boot app is a process like any other (step194): started at
            // its slot's range, on a main thread of its own, and waited for.
            OS.Kernel.Diagnostics.Sampler.ReportScreenState();
            AppServiceBuilder.EnterStart();
            AppServiceStatus started;
            AppProcess proc;
            try
            {
                StartupData.Clear();
                started = AppServiceBuilder.StartProcess(image, FileNameOf(app.Path),
                    app.AppAbiVersion, app.ServiceAbi, abiFromRequest: true, launcherId: 0, out proc);
            }
            finally
            {
                AppServiceBuilder.LeaveStart();
            }
            if (started != AppServiceStatus.Ok)
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write("app start failed: status=");
                UiText.WriteUInt((uint)started);
                DebugLog.EndLine();
                return AppRunResult.JumpFailed;
            }

            AppServiceBuilder.WaitForExit(proc);
            int exitCode = proc.ExitCode;
            AppServiceBuilder.ReleaseProcess(proc);

            DebugLog.Begin(LogLevel.Info);
            UiText.Write("process exit code = ");
            UiText.WriteInt(exitCode);
            DebugLog.EndLine();

            return exitCode == app.ExpectedExitCode ? AppRunResult.Success : AppRunResult.ExitCodeMismatch;
        }

        private static string FileNameOf(string path)
        {
            int cut = path.LastIndexOf('/');
            int other = path.LastIndexOf((char)92);
            if (other > cut) cut = other;
            return cut >= 0 ? path.Substring(cut + 1) : path;
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
