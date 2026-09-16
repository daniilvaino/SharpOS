using OS.Boot;
using OS.Hal;
using OS.Kernel.Exec;
using OS.Kernel.Input;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    // ABI-boundary code: all char*/byte* buffers passed into
    // BootInfo.File* callbacks go straight to UEFI firmware, which
    // expects stable unmanaged pointers during the call. `stackalloc`
    // is the correct tool here — managed arrays would require pinning
    // + marshalling with no readability gain. Path composition that
    // stays on our side (TryBuildAbiManifestPath) uses managed
    // string.Concat; the copy-back loop at the ABI edge is unavoidable.
    internal static unsafe partial class AppServiceBuilder
    {
        // Raised from 512 for full-screen output: a text UI repaints its
        // whole screen in one go, and 512 bytes forced callers back onto the
        // per-character service — thousands of service calls per frame, and no
        // paint at the end of any of them.
        private const int MaxWriteStringBytes = 16384;
        private const uint MaxPathChars = 260;
        private const uint MaxNameChars = 260;
        private const ulong EfiFileAttributeDirectory = 0x0000000000000010UL;
        private const ulong PageSize = X64PageTable.PageSize;

        private const uint ServiceThunkPageSize = 4096;
        private const uint ServiceThunkSlotSize = 64;
        private const ulong ServiceThunkVirtualBase = 0x0000700000010000UL;
        private const uint ServiceThunkSearchPages = 1024;
        private const ulong KernelLowSyncStart = 0x00100000UL;
        private const ulong KernelLowSyncEndExclusive = 0x20000000UL;

        private enum AbiResolveSource : uint
        {
            Request = 0,
            ImageManifest = 1,
            Fallback = 2,
        }

        private static int s_exitRequested;
        private static int s_exitCode;

        // Nested app-launch depth. RunExternalApp is the single choke
        // point for service-driven app launches (a running guest
        // calling RunApp). Max 1: the launcher may launch an app, but
        // that app may not launch another (recursive HELLOCS) — capped
        // before load so it returns `unsupported`, never the faulting
        // nested build.
        private static int s_runExternalDepth;

        // Levels of app-starts-app allowed below the one the kernel started.
        // Each costs a stack region (ProcessImageBuilder.StackRegionStride)
        // and a live frame per level on the kernel stack.
        private const int MaxNestedLaunchDepth = 4;

        private static uint s_publishedAbiVersion = AppServiceTable.AbiVersionV1;

        private static bool s_serviceThunksInitialized;
        private static ulong s_serviceThunkPageVirtual;
        private static ulong s_serviceThunkPagePhysical;
        private static ulong s_win64WriteStringThunk;
        private static ulong s_win64WriteUIntThunk;
        private static ulong s_win64WriteHexThunk;
        private static ulong s_win64GetAbiVersionThunk;
        private static ulong s_win64ExitThunk;
        private static ulong s_win64FileExistsThunk;
        private static ulong s_win64ReadFileThunk;
        private static ulong s_win64ReadDirEntryThunk;
        private static ulong s_win64TryReadKeyThunk;
        private static ulong s_win64RunAppThunk;
        private static ulong s_win64RunManagedAppThunk;
        private static ulong s_systemVRunManagedAppThunk;
        private static ulong s_systemVWriteStringThunk;
        private static ulong s_systemVWriteUIntThunk;
        private static ulong s_systemVWriteHexThunk;
        private static ulong s_systemVGetAbiVersionThunk;
        private static ulong s_systemVExitThunk;
        private static ulong s_systemVFileExistsThunk;
        private static ulong s_systemVReadFileThunk;
        private static ulong s_systemVReadDirEntryThunk;
        private static ulong s_systemVTryReadKeyThunk;
        private static ulong s_systemVRunAppThunk;
        private static ulong s_win64WriteCharThunk;
        private static ulong s_systemVWriteCharThunk;
        private static ulong s_win64WriteBuildIdThunk;
        private static ulong s_systemVWriteBuildIdThunk;
        private static ulong s_win64SpawnThreadThunk;
        private static ulong s_systemVSpawnThreadThunk;
        private static ulong s_win64ConsoleSizeThunk;
        private static ulong s_systemVConsoleSizeThunk;
        private static ulong s_win64CurrentThreadIdThunk;
        private static ulong s_systemVCurrentThreadIdThunk;
        private static ulong s_win64SleepThunk;
        private static ulong s_systemVSleepThunk;
        private static ulong s_win64WriteErrorThunk;
        private static ulong s_systemVWriteErrorThunk;
        private static ulong s_win64WriteDiagnosticThunk;
        private static ulong s_systemVWriteDiagnosticThunk;

        public static bool TryBuild(
            ulong serviceVirtual,
            AppServiceAbi serviceAbi,
            uint requestedAbiVersion,
            out ulong servicePhysical)
        {
            servicePhysical = 0;
            if (!Pager.TryQuery(serviceVirtual, out servicePhysical, out _))
                return false;

            uint publishedAbiVersion = NormalizeAbiVersion(requestedAbiVersion);

            delegate* managed<ulong, void> writeStringAddress = &WriteString;
            delegate* managed<uint, void> writeUIntAddress = &WriteUInt;
            delegate* managed<ulong, void> writeHexAddress = &WriteHex;
            delegate* managed<uint> getAbiVersionAddress = &GetAbiVersion;
            delegate* managed<int, void> exitAddress = &Exit;
            delegate* managed<ulong, uint> fileExistsAddress = &FileExists;
            delegate* managed<ulong, uint> readFileAddress = &ReadFile;
            delegate* managed<ulong, uint> readDirEntryAddress = &ReadDirEntry;
            delegate* managed<ulong, uint> tryReadKeyAddress = &TryReadKey;
            delegate* managed<ulong, uint> runAppAddress = &RunApp;
            delegate* managed<ulong, uint> runManagedAppAddress = &RunManagedApp;
            delegate* managed<uint, void> writeCharAddress = &WriteChar;
            delegate* managed<void> writeBuildIdAddress = &WriteBuildId;
            delegate* managed<ulong, uint> spawnThreadAddress = &SpawnThread;
            delegate* managed<uint, void> sleepAddress = &SleepMilliseconds;
            delegate* managed<uint> currentThreadIdAddress = &CurrentThreadId;
            delegate* managed<uint> consoleSizeAddress = &ConsoleSize;
            delegate* managed<ulong, void> writeErrorAddress = &WriteError;
            delegate* managed<ulong, void> writeDiagnosticAddress = &WriteDiagnostic;

            ulong tableWriteStringAddress = (ulong)writeStringAddress;
            ulong tableWriteUIntAddress = (ulong)writeUIntAddress;
            ulong tableWriteHexAddress = (ulong)writeHexAddress;
            ulong tableGetAbiVersionAddress = (ulong)getAbiVersionAddress;
            ulong tableExitAddress = (ulong)exitAddress;
            ulong tableFileExistsAddress = 0;
            ulong tableReadFileAddress = 0;
            ulong tableReadDirEntryAddress = 0;
            ulong tableTryReadKeyAddress = 0;
            ulong tableRunAppAddress = 0;
            ulong tableRunManagedAppAddress = 0;
            ulong tableWriteCharAddress = 0;
            ulong tableWriteBuildIdAddress = 0;
            ulong tableSpawnThreadAddress = 0;
            ulong tableSleepAddress = 0;
            ulong tableCurrentThreadIdAddress = 0;
            ulong tableConsoleSizeAddress = 0;
            ulong tableWriteErrorAddress = 0;
            ulong tableWriteDiagnosticAddress = 0;

            if (!EnsureServiceThunks(
                (ulong)writeStringAddress,
                (ulong)writeUIntAddress,
                (ulong)writeHexAddress,
                (ulong)getAbiVersionAddress,
                (ulong)exitAddress,
                (ulong)fileExistsAddress,
                (ulong)readFileAddress,
                (ulong)readDirEntryAddress,
                (ulong)tryReadKeyAddress,
                (ulong)runAppAddress,
                (ulong)runManagedAppAddress,
                (ulong)writeCharAddress,
                (ulong)writeBuildIdAddress,
                (ulong)spawnThreadAddress,
                (ulong)sleepAddress,
                (ulong)currentThreadIdAddress,
                (ulong)consoleSizeAddress,
                (ulong)writeErrorAddress,
                (ulong)writeDiagnosticAddress))
            {
                return false;
            }

            if (serviceAbi == AppServiceAbi.SystemV)
            {
                tableWriteStringAddress = s_systemVWriteStringThunk;
                tableWriteUIntAddress = s_systemVWriteUIntThunk;
                tableWriteHexAddress = s_systemVWriteHexThunk;
                tableGetAbiVersionAddress = s_systemVGetAbiVersionThunk;
                tableExitAddress = s_systemVExitThunk;
                tableWriteCharAddress = s_systemVWriteCharThunk;
                tableWriteBuildIdAddress = s_systemVWriteBuildIdThunk;
                tableWriteErrorAddress = s_systemVWriteErrorThunk;
                tableWriteDiagnosticAddress = s_systemVWriteDiagnosticThunk;
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
                {
                    tableFileExistsAddress = s_systemVFileExistsThunk;
                    tableReadFileAddress = s_systemVReadFileThunk;
                    tableReadDirEntryAddress = s_systemVReadDirEntryThunk;
                    tableTryReadKeyAddress = s_systemVTryReadKeyThunk;
                    tableRunAppAddress = s_systemVRunAppThunk;
                    tableRunManagedAppAddress = s_systemVRunManagedAppThunk;
                }
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV3)
                {
                    tableSpawnThreadAddress = s_systemVSpawnThreadThunk;
                    tableSleepAddress = s_systemVSleepThunk;
                    tableCurrentThreadIdAddress = s_systemVCurrentThreadIdThunk;
                    tableConsoleSizeAddress = s_systemVConsoleSizeThunk;
                }
            }
            else
            {
                tableWriteStringAddress = s_win64WriteStringThunk;
                tableWriteUIntAddress = s_win64WriteUIntThunk;
                tableWriteHexAddress = s_win64WriteHexThunk;
                tableGetAbiVersionAddress = s_win64GetAbiVersionThunk;
                tableExitAddress = s_win64ExitThunk;
                tableWriteCharAddress = s_win64WriteCharThunk;
                tableWriteBuildIdAddress = s_win64WriteBuildIdThunk;
                tableWriteErrorAddress = s_win64WriteErrorThunk;
                tableWriteDiagnosticAddress = s_win64WriteDiagnosticThunk;
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV2)
                {
                    tableFileExistsAddress = s_win64FileExistsThunk;
                    tableReadFileAddress = s_win64ReadFileThunk;
                    tableReadDirEntryAddress = s_win64ReadDirEntryThunk;
                    tableTryReadKeyAddress = s_win64TryReadKeyThunk;
                    tableRunAppAddress = s_win64RunAppThunk;
                    tableRunManagedAppAddress = s_win64RunManagedAppThunk;
                }
                if (publishedAbiVersion >= AppServiceTable.AbiVersionV3)
                {
                    tableSpawnThreadAddress = s_win64SpawnThreadThunk;
                    tableSleepAddress = s_win64SleepThunk;
                    tableCurrentThreadIdAddress = s_win64CurrentThreadIdThunk;
                    tableConsoleSizeAddress = s_win64ConsoleSizeThunk;
                }
            }

            AppServiceTable table = default;
            table.AbiVersion = publishedAbiVersion;
            table.Reserved = 0;
            table.WriteStringAddress = tableWriteStringAddress;
            table.WriteUIntAddress = tableWriteUIntAddress;
            table.WriteHexAddress = tableWriteHexAddress;
            table.GetAbiVersionAddress = tableGetAbiVersionAddress;
            table.ExitAddress = tableExitAddress;
            table.FileExistsAddress = tableFileExistsAddress;
            table.ReadFileAddress = tableReadFileAddress;
            table.ReadDirEntryAddress = tableReadDirEntryAddress;
            table.TryReadKeyAddress = tableTryReadKeyAddress;
            table.RunAppAddress = tableRunAppAddress;
            table.RunManagedAppAddress = tableRunManagedAppAddress;
            table.WriteCharAddress = tableWriteCharAddress;
            table.WriteBuildIdAddress = tableWriteBuildIdAddress;
            table.SpawnThreadAddress = tableSpawnThreadAddress;
            table.SleepAddress = tableSleepAddress;
            table.CurrentThreadIdAddress = tableCurrentThreadIdAddress;
            table.ConsoleSizeAddress = tableConsoleSizeAddress;
            table.WriteErrorAddress = tableWriteErrorAddress;
            table.WriteDiagnosticAddress = tableWriteDiagnosticAddress;

            // Hand the app the kernel's interface-dispatch bridge entry so it
            // can trampoline its RhpInitialDynamicInterfaceDispatch into our
            // shared (major-9-pure) resolver. Raw shellcode address, ABI-agnostic
            // (ILC call-site ABI: rcx=this, r10=cell). Null if the bridge failed
            // to install at boot — the app's stub then stays in its fallback body.
            table.InterfaceDispatchBridgeAddress =
                (ulong)OS.Kernel.Memory.InterfaceDispatchBridge.ShellcodeStart;

            // Kernel RhpThrowEx entry (patched-in-place throw shellcode) so the
            // app's throw/catch shares the kernel EH engine (step140).
            table.RhpThrowExAddress =
                (ulong)OS.Boot.EH.ThrowExStub.GetMethodAddress();

            // And its partner: `throw;` inside a catch lowers to RhpRethrow,
            // which resumes the dispatch already in flight.
            table.RhpRethrowAddress =
                (ulong)OS.Boot.EH.RethrowStub.GetMethodAddress();

            // GOP framebuffer geometry (step143): identity-mapped in the shared
            // pager, so the app blits directly. Base stays 0 on headless boots.
            if (OS.Hal.Framebuffer.IsAvailable)
            {
                table.FramebufferBase = OS.Hal.Framebuffer.BaseAddress;
                table.FramebufferWidth = OS.Hal.Framebuffer.Width;
                table.FramebufferHeight = OS.Hal.Framebuffer.Height;
                table.FramebufferStride = OS.Hal.Framebuffer.Stride;
                table.FramebufferPixelFormat = OS.Hal.Framebuffer.PixelFormat;
            }

            // HPET time source (step143): identity-mapped MMIO counter + its
            // calibrated frequency, for app-side Stopwatch / frame pacing.
            if (OS.Hal.Timer.Hpet.IsInitialized)
            {
                table.HpetCounterAddress = OS.Hal.Timer.Hpet.CounterAddress;
                table.HpetFrequencyHz = OS.Hal.Timer.Hpet.FrequencyHz;
            }

            // Precise stack-root walk, lent to the app for its own collector.
            // Only offered when the machinery is actually up: without the
            // register spill or the function tables, a walk would silently
            // report no roots — and an app that believes that will free live
            // objects. Zero here means "no walker", and AppGC refuses to
            // collect rather than collect wrongly.
            if (OS.Kernel.Memory.KernelGcPreciseWalk.IsAvailable)
                table.GcWalkRootsAddress = (ulong)(nint)(delegate* unmanaged<nuint, void>)
                    &OS.Kernel.Memory.AppGcService.WalkRoots;

            // Blocking waits for app threads. Win64 callers only: the wait
            // takes four arguments in rcx/rdx/r8/r9, where a SysV caller would
            // not put them, and there is no thunk to move them.
            if (serviceAbi != AppServiceAbi.SystemV && publishedAbiVersion >= AppServiceTable.AbiVersionV3)
            {
                table.WaitOnAddressAddress = (ulong)(nint)(delegate* unmanaged<void*, void*, uint, uint, uint>)&AppWaitOnAddress;
                table.WakeByAddressAllAddress = (ulong)(nint)(delegate* unmanaged<void*, void>)&AppWakeByAddressAll;
            }

            AppServiceTable* serviceTablePointer = Pager.IsPagerRootActive()
                ? (AppServiceTable*)serviceVirtual
                : (AppServiceTable*)servicePhysical;

            *serviceTablePointer = table;
            s_exitRequested = 0;
            s_exitCode = 0;
            s_publishedAbiVersion = publishedAbiVersion;
            return true;
        }

        private static uint NormalizeAbiVersion(uint requestedAbiVersion)
            => AppServiceTable.Normalize(requestedAbiVersion);

        private static bool EnsureServiceThunks(
            ulong writeStringTarget,
            ulong writeUIntTarget,
            ulong writeHexTarget,
            ulong getAbiVersionTarget,
            ulong exitTarget,
            ulong fileExistsTarget,
            ulong readFileTarget,
            ulong readDirEntryTarget,
            ulong tryReadKeyTarget,
            ulong runAppTarget,
            ulong runManagedAppTarget,
            ulong writeCharTarget,
            ulong writeBuildIdTarget,
            ulong spawnThreadTarget,
            ulong sleepTarget,
            ulong currentThreadIdTarget,
            ulong consoleSizeTarget,
            ulong writeErrorTarget,
            ulong writeDiagnosticTarget)
        {
            if (s_serviceThunksInitialized)
                return true;

            // Phase E1 note: stale guard removed — pre-E1 it never fired
            // (IsPagerRootActive was always false); post-E1 kernel CR3 ==
            // pager root, and Pager.Map writes are now CPU-visible directly,
            // so thunk init works identically. See same edit in JumpStub.
            bool initialized = false;
            try
            {
                ulong thunkPagePhysical = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (thunkPagePhysical == 0)
                    return false;

                if (!TryMapServiceThunkPage(thunkPagePhysical, out ulong thunkPageVirtual))
                    return false;

                global::OS.Kernel.Util.Memory.Zero((void*)thunkPagePhysical, ServiceThunkPageSize);

                byte* page = (byte*)thunkPagePhysical;
                uint cursor = 0;

                s_win64WriteStringThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeStringTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteUIntThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeUIntTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteHexThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeHexTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64GetAbiVersionThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, getAbiVersionTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ExitThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, exitTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64FileExistsThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, fileExistsTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ReadFileThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, readFileTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ReadDirEntryThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, readDirEntryTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64TryReadKeyThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, tryReadKeyTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64RunAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, runAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64RunManagedAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, runManagedAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteStringThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeStringTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteUIntThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeUIntTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteHexThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeHexTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVGetAbiVersionThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, getAbiVersionTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVExitThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, exitTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVFileExistsThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, fileExistsTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVReadFileThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, readFileTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVReadDirEntryThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, readDirEntryTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVTryReadKeyThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, tryReadKeyTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVRunAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, runAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVRunManagedAppThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, runManagedAppTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteCharThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeCharTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteCharThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeCharTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64WriteBuildIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, writeBuildIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteBuildIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, writeBuildIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                // V3 — threads. One argument each (entry pointer, milliseconds),
                // so they fit the same one-arg thunk both ABIs already use.
                s_win64SpawnThreadThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, spawnThreadTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVSpawnThreadThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, spawnThreadTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64SleepThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, sleepTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVSleepThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, sleepTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64CurrentThreadIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, currentThreadIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVCurrentThreadIdThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, currentThreadIdTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_win64ConsoleSizeThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64NoArgThunk(page + cursor, consoleSizeTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVConsoleSizeThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVNoArgThunk(page + cursor, consoleSizeTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                // The error stream: same shape as WriteString, one address.
                s_win64WriteErrorThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeErrorTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteErrorThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeErrorTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                // Diagnostics: same shape again, different channel.
                s_win64WriteDiagnosticThunk = thunkPageVirtual + cursor;
                if (!TryWriteWin64OneArgThunk(page + cursor, writeDiagnosticTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_systemVWriteDiagnosticThunk = thunkPageVirtual + cursor;
                if (!TryWriteSystemVOneArgThunk(page + cursor, writeDiagnosticTarget))
                    return false;
                cursor += ServiceThunkSlotSize;

                s_serviceThunkPagePhysical = thunkPagePhysical;
                s_serviceThunkPageVirtual = thunkPageVirtual;
                s_serviceThunksInitialized = true;
                initialized = true;
            }
            finally
            {
                if (!initialized)
                    s_serviceThunksInitialized = false;
            }

            return initialized;
        }

        private static bool TryMapServiceThunkPage(ulong thunkPagePhysical, out ulong thunkPageVirtual)
        {
            thunkPageVirtual = 0;
            if ((thunkPagePhysical & (PageSize - 1)) != 0)
                return false;

            for (uint i = 0; i < ServiceThunkSearchPages; i++)
            {
                ulong candidate = ServiceThunkVirtualBase + ((ulong)i * PageSize);
                if (Pager.TryQuery(candidate, out _, out _))
                    continue;

                if (Pager.Map(candidate, thunkPagePhysical, PageFlags.Writable))
                {
                    thunkPageVirtual = candidate;
                    return true;
                }
            }

            return false;
        }

        // step 115 follow-up #4: Iced-driven thunk emitters with a
        // byte-compare gate against the legacy byte-streams. Iced writes
        // to the LIVE destination (this is the actual migration), legacy
        // writes to a stack scratch — bytes must match exactly or
        // CompareOrPanic halts with offset + iced/legacy values. Each
        // thunk slot is 64 bytes (ServiceThunkSlotSize); thunk bodies are
        // 21 / 24 bytes, so the scratch has plenty of headroom. First
        // successful emit per shape prints one [thunk] line; subsequent
        // emits are silent but still gated (24+ calls per boot).
        private static bool TryWriteWin64OneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            byte* scratch = stackalloc byte[64];
            int icedLen = EmitWin64OneArgThunkIced(destination, 64, target);
            int legacyLen = EmitWin64OneArgThunkLegacy(scratch, target);
            CompareOrPanic("Win64OneArgThunk", destination, scratch, icedLen, legacyLen);

            if (!s_win64GateLogged)
            {
                s_win64GateLogged = true;
                Console.Write("[thunk] win64-onearg iced=legacy OK len=0x");
                Console.WriteHex((ulong)icedLen);
                Console.WriteLine("");
            }
            return true;
        }

        private static bool TryWriteWin64NoArgThunk(byte* destination, ulong target)
        {
            return TryWriteWin64OneArgThunk(destination, target);
        }

        private static bool TryWriteSystemVOneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            byte* scratch = stackalloc byte[64];
            int icedLen = EmitSystemVOneArgThunkIced(destination, 64, target);
            int legacyLen = EmitSystemVOneArgThunkLegacy(scratch, target);
            CompareOrPanic("SystemVOneArgThunk", destination, scratch, icedLen, legacyLen);

            if (!s_sysVGateLogged)
            {
                s_sysVGateLogged = true;
                Console.Write("[thunk] sysv-onearg iced=legacy OK len=0x");
                Console.WriteHex((ulong)icedLen);
                Console.WriteLine("");
            }
            return true;
        }

        private static bool s_win64GateLogged;
        private static bool s_sysVGateLogged;

        /// <summary>
        /// Steps a stack walk over a service thunk: true, with the caller's
        /// <paramref name="rip"/> and <paramref name="rsp"/>, when
        /// <paramref name="rip"/> is inside one.
        /// </summary>
        /// <remarks>
        /// Every service an app calls goes through a thunk on the thunk page,
        /// and the thunks have no unwind data, so a walk up from inside a
        /// service stopped there. For the app's collector that meant every
        /// thread parked in a service — a thread asleep is parked in Sleep —
        /// kept its app frames out of the walk, and what only they held was
        /// freed (step169). The shape is ours and fixed, so no table is
        /// needed: both ABIs are "sub rsp,28h / call rax / add rsp,28h / ret",
        /// the SysV one behind a 3-byte "mov rcx,rdi". A thunk holds no roots.
        /// </remarks>
        internal static bool TryUnwindServiceThunk(ref ulong rip, ref ulong rsp)
        {
            ulong pageVirtual = s_serviceThunkPageVirtual;
            if (pageVirtual == 0 || rip < pageVirtual || rip >= pageVirtual + ServiceThunkPageSize)
                return false;

            ulong offsetInPage = rip - pageVirtual;
            ulong slotOffset = offsetInPage - offsetInPage % ServiceThunkSlotSize;
            uint offset = (uint)(offsetInPage - slotOffset);

            // Read through the physical mapping the thunks were written through.
            byte* slot = (byte*)(s_serviceThunkPagePhysical + slotOffset);
            bool systemV = slot[0] == 0x48 && slot[1] == 0x89 && slot[2] == 0xF9;   // mov rcx, rdi

            // "sub rsp,28h" starts here; the frame exists from its end up to
            // the start of "add rsp,28h" — which is also where "call rax"
            // returns to.
            uint sub = systemV ? 13u : 10u;
            if (offset >= sub + 4 && offset <= sub + 6)
                rsp += 0x28;

            rip = *(ulong*)rsp;
            rsp += 8;
            return true;
        }

        // ---- Legacy byte-stream emitters (return length for compare). ----

        private static int EmitWin64OneArgThunkLegacy(byte* destination, ulong target)
        {
            // mov rax, target
            destination[0] = 0x48;
            destination[1] = 0xB8;
            WriteU64(destination + 2, target);
            // sub rsp, 0x28
            destination[10] = 0x48;
            destination[11] = 0x83;
            destination[12] = 0xEC;
            destination[13] = 0x28;
            // call rax
            destination[14] = 0xFF;
            destination[15] = 0xD0;
            // add rsp, 0x28
            destination[16] = 0x48;
            destination[17] = 0x83;
            destination[18] = 0xC4;
            destination[19] = 0x28;
            // ret
            destination[20] = 0xC3;
            return 21;
        }

        private static int EmitSystemVOneArgThunkLegacy(byte* destination, ulong target)
        {
            // mov rcx, rdi
            destination[0] = 0x48;
            destination[1] = 0x89;
            destination[2] = 0xF9;
            // mov rax, target
            destination[3] = 0x48;
            destination[4] = 0xB8;
            WriteU64(destination + 5, target);
            // sub rsp, 0x28
            destination[13] = 0x48;
            destination[14] = 0x83;
            destination[15] = 0xEC;
            destination[16] = 0x28;
            // call rax
            destination[17] = 0xFF;
            destination[18] = 0xD0;
            // add rsp, 0x28
            destination[19] = 0x48;
            destination[20] = 0x83;
            destination[21] = 0xC4;
            destination[22] = 0x28;
            // ret
            destination[23] = 0xC3;
            return 24;
        }

        private static bool TryWriteSystemVNoArgThunk(byte* destination, ulong target)
        {
            return TryWriteWin64OneArgThunk(destination, target);
        }

        private static void WriteU64(byte* destination, ulong value)
        {
            destination[0] = (byte)(value & 0xFF);
            destination[1] = (byte)((value >> 8) & 0xFF);
            destination[2] = (byte)((value >> 16) & 0xFF);
            destination[3] = (byte)((value >> 24) & 0xFF);
            destination[4] = (byte)((value >> 32) & 0xFF);
            destination[5] = (byte)((value >> 40) & 0xFF);
            destination[6] = (byte)((value >> 48) & 0xFF);
            destination[7] = (byte)((value >> 56) & 0xFF);
        }

        public static bool TryConsumeExit(out int exitCode)
        {
            exitCode = 0;
            if (s_exitRequested == 0)
                return false;

            exitCode = s_exitCode;
            s_exitRequested = 0;
            return true;
        }

        private static void WriteString(ulong textAddress)
        {
            WriteUtf8(textAddress, AppOutputChannel());
            NoteAlternateScreenOwner();
        }

        // The application's error stream. Never the Ui channel, even from inside
        // a full-screen interface: an error is a message, not part of a frame,
        // and hiding it from the logs because the screen was busy would lose
        // exactly the line worth keeping.
        /// <summary>
        /// Diagnostics from an application, routed where measurements go: the
        /// serial port and the log, never the screen.
        /// </summary>
        /// <remarks>
        /// A full-screen application cannot report through ordinary or error
        /// output — both paint, so the report lands inside the interface it is
        /// describing. That is exactly what a heap census from the launcher
        /// did.
        /// </remarks>
        private static void WriteDiagnostic(ulong textAddress)
            => WriteUtf8(textAddress, OS.Hal.OutputChannel.Perf);

        private static void WriteError(ulong textAddress)
            => WriteUtf8(textAddress, OS.Hal.OutputChannel.AppErr);

        private static void WriteUtf8(ulong textAddress, OS.Hal.OutputChannel channel)
        {
            if (textAddress == 0)
                return;

            // This path used to reach the screen through UiText -> Console, and
            // Console drops everything while Quiet is set. Kept, so moving the
            // output onto its own channel changes nothing about when it shows.
            if (OS.Hal.Console.Quiet)
                return;

            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            int characters = 0;
            uint last = 0;

            // The bytes are UTF-8, and they have to be decoded here.
            //
            // This used to cast each byte to a char, which is Latin-1 by
            // another name: a box-drawing rune arrived as its three UTF-8
            // bytes and left as three separate characters, each re-encoded to
            // UTF-8 on the way to the terminal engine. The screen showed the
            // mojibake that double encoding always produces.
            byte* pointer = (byte*)textAddress;
            for (int i = 0; i < MaxWriteStringBytes; )
            {
                byte lead = pointer[i];
                if (lead == 0)
                    break;

                uint codepoint;
                int length;

                if (lead < 0x80) { codepoint = lead; length = 1; }
                else if ((lead & 0xE0) == 0xC0) { codepoint = (uint)(lead & 0x1F); length = 2; }
                else if ((lead & 0xF0) == 0xE0) { codepoint = (uint)(lead & 0x0F); length = 3; }
                else if ((lead & 0xF8) == 0xF0) { codepoint = (uint)(lead & 0x07); length = 4; }
                else
                {
                    // A stray continuation byte. Skip it rather than guess: one
                    // bad byte should cost one character, not resynchronise the
                    // rest of the string onto the wrong boundary.
                    i++;
                    continue;
                }

                if (i + length > MaxWriteStringBytes) break;

                bool truncated = false;
                for (int k = 1; k < length; k++)
                {
                    byte continuation = pointer[i + k];
                    if ((continuation & 0xC0) != 0x80) { truncated = true; break; }
                    codepoint = (codepoint << 6) | (uint)(continuation & 0x3F);
                }

                if (truncated) { i++; continue; }
                i += length;
                characters++;
                last = codepoint;

                if (codepoint <= 0xFFFF)
                {
                    OS.Hal.Platform.WriteChar((char)codepoint, channel);
                }
                else
                {
                    // Past the basic plane the engine wants the surrogate pair,
                    // since it consumes chars rather than codepoints.
                    codepoint -= 0x10000;
                    OS.Hal.Platform.WriteChar((char)(0xD800 + (codepoint >> 10)), channel);
                    OS.Hal.Platform.WriteChar((char)(0xDC00 + (codepoint & 0x3FF)), channel);
                }
            }

            // Paint once, at the end of the write — the same batching
            // Platform.Write uses for kernel output.
            //
            // Without this a full-screen app draws nothing: the terminal engine
            // paints on a line break, and a text UI never writes one. Its
            // escape sequences were reaching the engine and changing the grid,
            // while the framebuffer kept showing the frame before.
            //
            //
            // Except after a write that ends a line: that is a program printing
            // lines, and a paint per write made it pay one full-screen move per
            // line (step169). Its line break already painted if one was due;
            // the rest waits for the pump, the next write or a key read.
            //
            // Not "paint when due" for everything. A text UI sends a frame in
            // several writes and redraws it whole even when nothing changed;
            // painted at the end of every write, identical frames are
            // invisible. Deferring all but the first write of a burst put the
            // first piece of each new frame on screen alone — the launcher
            // flickered, and came up half-drawn until a key was pressed.
            if (last == '\n')
                OS.Hal.Platform.FlushConsoleWhenDue();
            else
                OS.Hal.Platform.FlushConsole();
            OS.Kernel.Diagnostics.PerfCounters.CountProgramWrite(started, characters);
        }

        /// <summary>
        /// The channel an application's output belongs on right now.
        /// </summary>
        /// <remarks>
        /// While it is drawing a full-screen interface its output is frames, not
        /// messages: copying every escape byte to the UART and the disk log cost
        /// more than drawing did (step165), and nobody reads a screen out of a
        /// log. Decided per write from the terminal's own state rather than by
        /// asking the app, because the app already said so: it switched to the
        /// alternate screen.
        ///
        /// Only the process that switched. The launcher hands the screen to a
        /// child while staying in the alternate screen (leaving it would bring
        /// the boot log back), and "the screen is alternate" alone then read
        /// the child's ordinary lines as the launcher's frames: an AOTTESTS run
        /// reached the display and no log at all.
        /// </remarks>
        private static OS.Hal.OutputChannel AppOutputChannel()
            => OS.Hal.TerminalConsole.IsAlternateScreen && s_alternateScreenOwner == s_runExternalDepth
                ? OS.Hal.OutputChannel.Ui
                : OS.Hal.OutputChannel.AppOut;

        // Launch depth of the process that put the terminal in the alternate
        // screen (0 = the app the kernel started, 1 = its child); -1 while the
        // terminal is on the main screen.
        private static int s_alternateScreenOwner = -1;

        /// <summary>
        /// Records who switched the terminal to the alternate screen, after a
        /// write that may have done it.
        /// </summary>
        /// <remarks>
        /// Caught on the transition, so a child that enters the alternate
        /// screen while its parent already holds it is not seen as the owner:
        /// its frames then reach the logs as output. Noisy, never lost — and no
        /// such child exists yet (the emulators and DOOM draw on the
        /// framebuffer, not through the terminal).
        /// </remarks>
        private static void NoteAlternateScreenOwner()
        {
            if (!OS.Hal.TerminalConsole.IsAlternateScreen)
                s_alternateScreenOwner = -1;
            else if (s_alternateScreenOwner < 0)
                s_alternateScreenOwner = s_runExternalDepth;
        }

        // The number services are an application's output like WriteString, and
        // go where it goes. Through UiText they were the kernel's: a label
        // written with WriteString and its value written with WriteUInt left on
        // different channels, so the log got the digits and lost the label.
        private static void WriteUInt(uint value)
        {
            WriteAppText(SharpOS.Std.NoRuntime.NumberFormatting.UIntToString(value));
        }

        private static void WriteHex(ulong value)
        {
            WriteAppText("0x");
            WriteAppText(SharpOS.Std.NoRuntime.NumberFormatting.ULongToHex(value, 16));
        }

        private static void WriteAppText(string text)
        {
            if (OS.Hal.Console.Quiet)
                return;

            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            OS.Hal.OutputChannel channel = AppOutputChannel();
            for (int i = 0; i < text.Length; i++)
                OS.Hal.Platform.WriteChar(text[i], channel);

            OS.Hal.Platform.FlushConsole();
            OS.Kernel.Diagnostics.PerfCounters.CountProgramWrite(started, text.Length);
        }

        private static void WriteChar(uint codePoint)
        {
            // Same rules as WriteString: Quiet silences it, and a character that
            // belongs to a full-screen frame is not a log line.
            if (OS.Hal.Console.Quiet)
                return;

            OS.Hal.Platform.WriteChar((char)codePoint, AppOutputChannel());
            NoteAlternateScreenOwner();
        }

        private static void WriteBuildId()
        {
            WriteAppText(OS.Kernel.SystemBanner.BuildId);
        }

        private static uint GetAbiVersion()
        {
            return s_publishedAbiVersion;
        }

        private static void Exit(int exitCode)
        {
            s_exitCode = exitCode;
            s_exitRequested = 1;
        }

        private static uint FileExists(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppFileExistsRequest* request = (AppFileExistsRequest*)requestAddress;
            if (request->PathAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileExists == null)
                return (uint)AppServiceStatus.Unsupported;

            uint status = bootInfo.FileExists(pathBuffer);
            return (uint)MapBootFileStatus(status);
        }

        private static uint ReadFile(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppReadFileRequest* request = (AppReadFileRequest*)requestAddress;
            request->BytesRead = 0;

            if (request->PathAddress == 0 || request->BufferAddress == 0 || request->BufferCapacity == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadIntoBuffer == null)
                return (uint)AppServiceStatus.Unsupported;

            uint bytesRead = 0;
            uint status = bootInfo.FileReadIntoBuffer(
                pathBuffer,
                (void*)request->BufferAddress,
                request->BufferCapacity,
                &bytesRead);

            request->BytesRead = bytesRead;
            return (uint)MapBootFileStatus(status);
        }

        private static uint ReadDirEntry(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppReadDirectoryEntryRequest* request = (AppReadDirectoryEntryRequest*)requestAddress;
            request->NameLength = 0;
            request->IsDirectory = 0;

            if (request->DirectoryPathAddress == 0 || request->NameBufferAddress == 0 || request->NameBufferCapacity == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->DirectoryPathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.DirectoryReadEntry == null)
                return (uint)AppServiceStatus.Unsupported;

            char* nameBufferUtf16 = stackalloc char[(int)MaxNameChars];
            uint nameLengthUtf16 = 0;
            ulong attributes = 0;

            uint status = bootInfo.DirectoryReadEntry(
                pathBuffer,
                request->EntryIndex,
                nameBufferUtf16,
                MaxNameChars,
                &nameLengthUtf16,
                &attributes);

            AppServiceStatus mappedStatus = MapBootFileStatus(status);
            if (mappedStatus != AppServiceStatus.Ok)
                return (uint)mappedStatus;

            AppServiceStatus copyStatus = TryWriteAsciiName(
                nameBufferUtf16,
                nameLengthUtf16,
                (byte*)request->NameBufferAddress,
                request->NameBufferCapacity,
                out uint asciiNameLength);

            request->NameLength = asciiNameLength;
            if (copyStatus != AppServiceStatus.Ok)
                return (uint)copyStatus;

            request->IsDirectory = (attributes & EfiFileAttributeDirectory) == EfiFileAttributeDirectory ? 1U : 0U;
            return (uint)AppServiceStatus.Ok;
        }

        // V3 — threads for apps.
        //
        // The app runs mapped into the kernel's address space and on the
        // kernel's scheduler, so "spawn a thread" is the scheduler's own Spawn
        // with the app's entry pointer. No new machinery, just a door that was
        // not there: without it an app can only do one thing at a time, and a
        // library that keeps its input decoding on a background loop cannot run
        // at all.
        // The app entry is wrapped rather than spawned directly, so that a
        // thread whose work is done ENDS instead of returning off the end of
        // its entry point into whatever follows. The app cannot do this for
        // itself — Scheduler.Exit is kernel-side — and without it the first app
        // thread to finish left the machine in a state where the thread waiting
        // on it never came back.

        // Why a child could not be started, named at the point it happened.
        //
        // Every one of these used to be the same "device error", and the status
        // alone could not tell reading the file apart from mapping its pages —
        // a launcher that silently redrew its menu was the whole diagnosis.
        // Numbers rather than strings because this is the loader: no allocation,
        // and the step is trivially findable in this file.
        private static AppServiceStatus FailedAtStep(uint step)
        {
            DebugLog.Begin(LogLevel.Warn);
            UiText.Write("---- child failed at step ");
            UiText.WriteUInt(step);
            DebugLog.EndLine();
            return AppServiceStatus.DeviceError;
        }

        private static uint SpawnThread(ulong entryAddress)
        {
            if (entryAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            const uint AppThreadStackBytes = 64 * 1024;

            // Created suspended so that where it enters the app and which run
            // it belongs to are on the thread before it can run. The entry used
            // to wait in a 32-slot ring for whichever thread started first,
            // which capped the threads not yet started at 32 and could not
            // survive threads being taken away when an app ends.
            var t = global::OS.Kernel.Threading.Scheduler.Spawn(&AppThreadThunk, AppThreadStackBytes, startRunnable: false);
            if (t == null)
                return (uint)AppServiceStatus.DeviceError;

            global::OS.Kernel.Threading.Preemption.Suppress();
            t.AppEntry = entryAddress;
            global::OS.Kernel.Threading.Thread? spawner = global::OS.Kernel.Threading.Scheduler.Current;
            t.AppGeneration = spawner == null ? 0u : spawner.AppGeneration;
            global::OS.Kernel.Threading.Scheduler.MakeRunnable(t);
            global::OS.Kernel.Threading.Preemption.Allow();
            return (uint)AppServiceStatus.Ok;
        }

        /// <summary>
        /// After an app returns and before its pages go: its threads go
        /// (Scheduler.LeaveApp), and the log says how many were still there.
        /// </summary>
        internal static void EndAppRun(uint generation, uint previousGeneration)
        {
            uint ended = global::OS.Kernel.Threading.Scheduler.LeaveApp(generation, previousGeneration);
            if (ended == 0) return;
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("app threads ended with the app: ");
            UiText.WriteInt((int)ended);
            DebugLog.EndLine();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void AppThreadThunk()
        {
            global::OS.Kernel.Threading.Thread? self = global::OS.Kernel.Threading.Scheduler.Current;
            ulong entryAddress = self == null ? 0 : self.AppEntry;

            if (entryAddress != 0)
                ((delegate* unmanaged<void>)entryAddress)();

            global::OS.Kernel.Threading.Scheduler.Exit();
        }

        // Sleeping is half of what a thread is for here: a background loop that
        // polls without sleeping is the spin we spent step157 removing.
        // Which thread is asking. An app cannot answer this for itself:
        // thread-statics need runtime support neither tier has, and the
        // scheduler that owns the threads lives here. Monitor is the caller
        // that matters — without distinct ids a reentrant lock lets every
        // thread straight through.
        private static uint CurrentThreadId()
        {
            OS.Kernel.Threading.Thread? current = OS.Kernel.Threading.Scheduler.Current;
            return current == null ? 1u : (uint)current.Id;
        }

        // Columns in the low 16 bits, rows in the high 16. Zero when there is
        // no terminal front-end — a caller must treat that as "unknown" rather
        // than as a screen of size zero.
        private static uint ConsoleSize()
        {
            if (!OS.Hal.TerminalConsole.IsReady) return 0;

            var engine = OS.Hal.TerminalConsole.Engine;
            if (engine == null) return 0;

            uint cols = (uint)engine.Cols;
            uint rows = (uint)engine.Rows;
            return (cols & 0xFFFFu) | ((rows & 0xFFFFu) << 16);
        }

        private static void SleepMilliseconds(uint milliseconds)
            => global::OS.Kernel.Threading.Scheduler.Sleep(milliseconds);

        // The app's addresses are valid here: apps run in the kernel's address
        // space (JumpStub loads the pager root the kernel itself runs on).
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static uint AppWaitOnAddress(void* address, void* compare, uint size, uint timeoutMs)
            => global::OS.Kernel.Threading.AddressWait.WaitOnAddress(address, compare, size, timeoutMs) ? 1u : 0u;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void AppWakeByAddressAll(void* address)
            => global::OS.Kernel.Threading.AddressWait.WakeByAddressAll(address);

        private static uint TryReadKey(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            // Asking for a key means the output before it has to be on screen.
            // Lines may still be waiting (see WriteUtf8), and while an app
            // polls instead of sleeping the input pump never gets to run.
            if (OS.Hal.TerminalConsole.HasPendingOutput)
                OS.Hal.Platform.FlushConsole();

            AppReadKeyRequest* request = (AppReadKeyRequest*)requestAddress;
            request->UnicodeChar = 0;
            request->ScanCode = 0;
            request->Reserved = 0;

            // Raw variant (step143): Reserved carries the set-1 make/break
            // event (see Platform.TryReadKeyRaw packing) so apps can track
            // held keys; legacy fields keep the launcher contract.
            KeyReadStatus keyReadStatus = Keyboard.TryReadKeyRaw(out KeyInfo key, out uint raw);
            if (keyReadStatus == KeyReadStatus.NoKey)
                return (uint)AppServiceStatus.NoData;

            if (keyReadStatus == KeyReadStatus.Unsupported)
                return (uint)AppServiceStatus.Unsupported;

            if (keyReadStatus == KeyReadStatus.DeviceError)
                return (uint)AppServiceStatus.DeviceError;

            request->UnicodeChar = key.UnicodeChar;
            request->ScanCode = key.ScanCode;
            request->Reserved = raw;
            return (uint)AppServiceStatus.Ok;
        }

        /// <summary>
        /// Hands a managed assembly to the hosted runtime and waits for it.
        /// </summary>
        /// <remarks>
        /// No process is built and nothing is mapped: the assembly runs inside
        /// the runtime that boot already brought up, on the stack that runtime
        /// needs. The caller is blocked meanwhile, exactly as it is for a PE
        /// app, and gets the exit code back the same way.
        /// </remarks>
        private static uint RunManagedApp(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppRunManagedRequest* request = (AppRunManagedRequest*)requestAddress;
            request->ExitCode = 0;

            if (request->PathAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            if (!global::OS.Kernel.Exec.CoreClrHost.IsRunning)
                return (uint)AppServiceStatus.Unsupported;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            string path = string.FromUtf16Z(pathBuffer, (int)MaxPathChars);
            if (path.Length == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            DebugLog.Begin(LogLevel.Info);
            Console.Write("---- managed child start: ");
            Console.Write(path);
            Console.Write(" ----");
            DebugLog.EndLine();

            OS.Kernel.Diagnostics.PerfCounters.Mark();
            bool ok = global::OS.Kernel.Exec.CoreClrHost.TryExecute(path, out int exitCode);
            request->ExitCode = exitCode;

            DebugLog.Begin(LogLevel.Info);
            Console.Write("---- managed child end: exit=");
            Console.WriteInt(exitCode);
            Console.Write(ok ? " ----" : " (host refused) ----");
            DebugLog.EndLine();
            OS.Kernel.Diagnostics.PerfCounters.Report(RunScope(path));

            return ok ? (uint)AppServiceStatus.Ok : (uint)AppServiceStatus.DeviceError;
        }

        // "\SHARPOS\Bench.dll" -> "run.Bench": the scope a run's [perf] lines
        // are filed under, so runs of different programs never mix.
        private static string RunScope(string path)
        {
            int start = path.LastIndexOf('\\') + 1;
            int end = path.LastIndexOf('.');
            if (end <= start)
                end = path.Length;
            return "run." + path.Substring(start, end - start);
        }

        private static uint RunApp(ulong requestAddress)
        {
            if (requestAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            AppRunAppRequest* request = (AppRunAppRequest*)requestAddress;
            request->ExitCode = 0;

            if (request->PathAddress == 0)
                return (uint)AppServiceStatus.InvalidParameter;

            char* pathBuffer = stackalloc char[(int)MaxPathChars];
            if (!TryReadAsciiPath(request->PathAddress, pathBuffer, MaxPathChars))
                return (uint)AppServiceStatus.InvalidParameter;

            if (!TryResolveRunAppAbi(
                pathBuffer,
                request->AppAbiVersion,
                request->ServiceAbi,
                out uint appAbiVersion,
                out AppServiceAbi serviceAbi,
                out AbiResolveSource abiSource))
            {
                return (uint)AppServiceStatus.InvalidParameter;
            }

            LogRunAppAbiSelection(abiSource, appAbiVersion, serviceAbi);

            int savedExitRequested = s_exitRequested;
            int savedExitCode = s_exitCode;
            uint savedPublishedAbi = s_publishedAbiVersion;

            s_exitRequested = 0;
            s_exitCode = 0;

            // The same [perf] scope a managed run gets: the NativeAOT benchmark
            // (BENCHAOT.EXE) is a PE child, and its output and clock costs are
            // the kernel's to count.
            OS.Kernel.Diagnostics.PerfCounters.Mark();
            AppServiceStatus runStatus = RunExternalApp(pathBuffer, appAbiVersion, serviceAbi,
                abiFromRequest: abiSource == AbiResolveSource.Request,
                out int childExitCode);
            request->ExitCode = childExitCode;
            OS.Kernel.Diagnostics.PerfCounters.Report(RunScope(string.FromUtf16Z(pathBuffer, (int)MaxPathChars)));

            s_exitRequested = savedExitRequested;
            s_exitCode = savedExitCode;
            s_publishedAbiVersion = savedPublishedAbi;

            return (uint)runStatus;
        }

        private static bool TryReadAsciiPath(ulong pathAddress, char* destination, uint destinationChars)
        {
            if (pathAddress == 0 || destination == null || destinationChars < 2)
                return false;

            byte* source = (byte*)pathAddress;
            uint i = 0;
            for (; i < destinationChars - 1; i++)
            {
                byte value = source[i];
                if (value == 0)
                {
                    destination[i] = '\0';
                    return i != 0;
                }

                destination[i] = (char)value;
            }

            destination[destinationChars - 1] = '\0';
            return false;
        }

        private static bool TryResolveRunAppAbi(
            char* path,
            uint requestedAbiVersion,
            uint requestedServiceAbi,
            out uint appAbiVersion,
            out AppServiceAbi serviceAbi,
            out AbiResolveSource source)
        {
            appAbiVersion = AppServiceTable.AbiVersionV1;
            serviceAbi = AppServiceAbi.WindowsX64;
            source = AbiResolveSource.Fallback;

            bool autoAppAbi = requestedAbiVersion == AppServiceTable.AutoSelectAbiVersion;
            bool autoServiceAbi = requestedServiceAbi == (uint)AppServiceAbi.Auto;

            if (!autoAppAbi && !autoServiceAbi)
            {
                if (!TryParseServiceAbi(requestedServiceAbi, out serviceAbi))
                    return false;

                appAbiVersion = NormalizeAbiVersion(requestedAbiVersion);
                source = AbiResolveSource.Request;
                return true;
            }

            uint resolvedFromRequestAbi = NormalizeAbiVersion(requestedAbiVersion);
            AppServiceAbi resolvedFromRequestService = AppServiceAbi.WindowsX64;
            if (!autoServiceAbi && !TryParseServiceAbi(requestedServiceAbi, out resolvedFromRequestService))
                return false;

            // Nothing to read from here any more: the record lives inside the
            // image, and the image is not loaded yet. What is chosen here is a
            // starting point, refined in RunExternalApp once the manifest
            // resource is addressable.
            appAbiVersion = autoAppAbi ? AppServiceTable.AbiVersionV1 : resolvedFromRequestAbi;
            serviceAbi = autoServiceAbi ? AppServiceAbi.WindowsX64 : resolvedFromRequestService;
            source = AbiResolveSource.Fallback;
            return true;
        }
        private static ushort ReadU16(byte* source)
        {
            return (ushort)(source[0] | (source[1] << 8));
        }

        private static bool TryParseServiceAbi(uint value, out AppServiceAbi serviceAbi)
        {
            if (value == (uint)AppServiceAbi.WindowsX64)
            {
                serviceAbi = AppServiceAbi.WindowsX64;
                return true;
            }

            if (value == (uint)AppServiceAbi.SystemV)
            {
                serviceAbi = AppServiceAbi.SystemV;
                return true;
            }

            serviceAbi = AppServiceAbi.WindowsX64;
            return false;
        }

        private static void LogRunAppAbiSelection(AbiResolveSource source, uint appAbiVersion, AppServiceAbi serviceAbi)
        {
            DebugLog.Begin(LogLevel.Info);
            UiText.Write("runapp abi source=");
            UiText.Write(AbiResolveSourceName(source));
            UiText.Write(" app=");
            UiText.WriteUInt(appAbiVersion);
            UiText.Write(" service=");
            UiText.Write(ServiceAbiName(serviceAbi));
            DebugLog.EndLine();
        }

        private static string AbiResolveSourceName(AbiResolveSource source)
        {
            switch (source)
            {
                case AbiResolveSource.Request: return "request";
                case AbiResolveSource.ImageManifest: return "image-manifest";
                case AbiResolveSource.Fallback: return "fallback";
                default: return "fallback";
            }
        }

        private static string ServiceAbiName(AppServiceAbi serviceAbi)
        {
            switch (serviceAbi)
            {
                case AppServiceAbi.WindowsX64: return "win64";
                case AppServiceAbi.SystemV: return "sysv";
                default: return "unknown";
            }
        }

        private static AppServiceStatus TryWriteAsciiName(
            char* source,
            uint sourceLength,
            byte* destination,
            uint destinationCapacity,
            out uint outputLength)
        {
            outputLength = sourceLength;
            if (source == null || destination == null || destinationCapacity == 0)
                return AppServiceStatus.InvalidParameter;

            if (sourceLength + 1 > destinationCapacity)
                return AppServiceStatus.BufferTooSmall;

            for (uint i = 0; i < sourceLength; i++)
            {
                char value = source[i];
                destination[i] = value <= 0x7F ? (byte)value : (byte)'?';
            }

            destination[sourceLength] = 0;
            return AppServiceStatus.Ok;
        }

        private static AppServiceStatus MapBootFileStatus(uint status)
        {
            if (status == (uint)BootFileStatus.Ok)
                return AppServiceStatus.Ok;

            if (status == (uint)BootFileStatus.NotFound)
                return AppServiceStatus.NotFound;

            if (status == (uint)BootFileStatus.EndOfDirectory)
                return AppServiceStatus.EndOfDirectory;

            if (status == (uint)BootFileStatus.BufferTooSmall)
                return AppServiceStatus.BufferTooSmall;

            if (status == (uint)BootFileStatus.InvalidParameter)
                return AppServiceStatus.InvalidParameter;

            return AppServiceStatus.DeviceError;
        }

        private static void LogImageManifest(ref LoadedImage loadedImage,
            uint appAbiVersion, AppServiceAbi serviceAbi)
        {
            DebugLog.Begin(LogLevel.Info);
            Console.Write("[abi] from image manifest schema=");
            Console.WriteUInt(loadedImage.ManifestSchema);
            Console.Write(" abi=");
            Console.WriteUInt(appAbiVersion);
            Console.Write(" serviceAbi=");
            Console.WriteUInt((uint)serviceAbi);
            DebugLog.EndLine();
        }

        private static AppServiceStatus RunExternalApp(
            char* path,
            uint appAbiVersion,
            AppServiceAbi serviceAbi,
            bool abiFromRequest,
            out int exitCode)
        {
            exitCode = 0;

            // A chain has to end somewhere, and nothing here grows a stack to
            // meet it: every level keeps a frame of RunExternalApp plus the
            // whole load-and-build path alive on the kernel stack. Four is
            // past anything asked for — a shell starting a program that starts
            // another — and far short of what the stack would notice.
            if (s_runExternalDepth >= MaxNestedLaunchDepth)
            {
                DebugLog.Begin(LogLevel.Info);
                Console.Write("nested app launch rejected (depth limit ");
                Console.WriteUInt(MaxNestedLaunchDepth);
                Console.Write(")");
                DebugLog.EndLine();
                return AppServiceStatus.Unsupported;
            }
            s_runExternalDepth++;
            try
            {

            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadAll == null)
                return AppServiceStatus.Unsupported;

            if (!ProcessManager.TrySuspendCurrentForNested(out MappingContext parentMappingContext, out bool parentSuspended))
                return AppServiceStatus.Unsupported;

            if (parentSuspended)
                DebugLog.Write(LogLevel.Info, "---- child start ----");

            AppServiceStatus result = AppServiceStatus.DeviceError;
            LoadedImage loadedImage = default;
            ProcessImage processImage = default;
            bool imageLoaded = false;
            bool processBuilt = false;

            try
            {
                do
                {
                    void* imagePointer = null;
                    uint imageSize = 0;
                    uint readStatus = bootInfo.FileReadAll(path, &imagePointer, &imageSize);
                    AppServiceStatus mappedReadStatus = MapBootFileStatus(readStatus);
                    if (mappedReadStatus != AppServiceStatus.Ok)
                    {
                        result = mappedReadStatus;
                        break;
                    }

                    MemoryBlock image = new MemoryBlock(imagePointer, imageSize);
                    if (!image.IsValid)
                    {
                        result = FailedAtStep(1);
                        break;
                    }

                    // PE only (step137): anything without the "MZ" magic is
                    // not an application this kernel can run.
                    image.TryReadUInt16(0, out ushort imageMagic);
                    if (imageMagic != global::OS.Kernel.Pe.PeLoader.DosMagicMZ)
                    {
                        result = AppServiceStatus.Unsupported;
                        break;
                    }

                    if (!global::OS.Kernel.Pe.PeLoader.TryLoad(image, out loadedImage, out _))
                    {
                        result = FailedAtStep(2);
                        break;
                    }

                    imageLoaded = true;

                    // The image's own manifest outranks the sidecar and the
                    // fallback, but not a caller who named the ABI outright.
                    // Read here rather than before the load because this is
                    // where the resource directory is addressable — see
                    // PeLoader.TryReadManifest.
                    if (loadedImage.ManifestFound && !abiFromRequest)
                    {
                        appAbiVersion = NormalizeAbiVersion(loadedImage.ManifestAbi);

                        if (TryParseServiceAbi(loadedImage.ManifestServiceAbi, out AppServiceAbi imageServiceAbi))
                            serviceAbi = imageServiceAbi;

                        LogImageManifest(ref loadedImage, appAbiVersion, serviceAbi);
                    }
                    else if (!loadedImage.ManifestFound && !abiFromRequest)
                    {
                        // Said out loud, because the fallback is V1 and an app
                        // built against a later table would find its services
                        // simply missing — which reads as the app misbehaving.
                        // Every app built from this tree carries a manifest, so
                        // this means a stale image or a build that skipped
                        // SharpAppManifest.props.
                        DebugLog.Begin(LogLevel.Warn);
                        Console.Write("[abi] image carries no manifest — falling back to V");
                        Console.WriteUInt(appAbiVersion);
                        DebugLog.EndLine();
                    }

                    // One stack region per level: the parent's stack stays
                    // mapped while the child runs, so they must not share one.
                    ulong childStackTop =
                        ProcessImageBuilder.StackMappedTopForDepth((uint)s_runExternalDepth);

                    if (!ProcessImageBuilder.TryBuild(ref loadedImage, 0, serviceAbi, appAbiVersion, childStackTop, out processImage))
                    {
                        result = FailedAtStep(4);
                        break;
                    }

                    processBuilt = true;

                    if (!TryValidateProcess(ref processImage, appAbiVersion))
                    {
                        result = FailedAtStep(5);
                        break;
                    }

                    if (!JumpStub.EnsureInitialized())
                    {
                        result = FailedAtStep(6);
                        break;
                    }

                    if (!TrySyncKernelLowMappings(ref processImage))
                    {
                        result = FailedAtStep(7);
                        break;
                    }

                    if (!Pager.TryGetPagerCr3(out ulong pagerCr3))
                    {
                        result = FailedAtStep(8);
                        break;
                    }

                    pagerCr3 &= 0x000FFFFFFFFFF000UL;
                    if (pagerCr3 == 0)
                    {
                        result = FailedAtStep(9);
                        break;
                    }

                    int returnExitCode = 0;
                    bool jumped;
                    uint previousGeneration = OS.Kernel.Threading.Scheduler.EnterApp(out uint appGeneration);

                    // The child is the current process while it runs, so that
                    // a launch of its own suspends ITS image rather than the
                    // one at the top of the chain. The displaced context goes
                    // on this frame and comes back below — the kernel stack is
                    // the stack of process contexts.
                    ProcessContext parentContext = ProcessManager.ExchangeCurrent(
                        ref processImage, ref loadedImage, out bool hadParentContext);
                    try
                    {
                        jumped = JumpStub.Run(
                            processImage.EntryPoint,
                            processImage.StackTop,
                            processImage.StartupBlockVirtual,
                            pagerCr3,
                            out returnExitCode);
                    }
                    finally
                    {
                        ProcessManager.RestoreCurrent(ref parentContext, hadParentContext);
                        EndAppRun(appGeneration, previousGeneration);
                    }

                    if (!jumped)
                    {
                        result = FailedAtStep(10);
                        break;
                    }

                    bool exitByService = TryConsumeExit(out int serviceExitCode);
                    exitCode = exitByService ? serviceExitCode : returnExitCode;
                    if (parentSuspended)
                    {
                        DebugLog.Begin(LogLevel.Info);
                        UiText.Write("---- child end: exit=");
                        UiText.WriteInt(exitCode);
                        UiText.Write(" ----");
                        DebugLog.EndLine();
                    }

                    result = AppServiceStatus.Ok;
                }
                while (false);

                if (processBuilt)
                {
                    if (!CleanupProcessMappings(ref processImage, ref loadedImage))
                    {
                        DebugLog.Write(LogLevel.Warn, "child cleanup mappings failed");
                        result = FailedAtStep(11);
                    }
                }
                else if (imageLoaded)
                {
                    CleanupLoadedImageMappings(ref loadedImage);
                }
            }
            finally
            {
                if (parentSuspended)
                {
                    if (!ProcessManager.TryRestoreAfterNested(ref parentMappingContext))
                    {
                        DebugLog.Write(LogLevel.Warn, "parent context restore failed");
                        result = AppServiceStatus.DeviceError;
                    }
                    // else: success path silenced — the kernel's
                    // "---- child end ----" line is the authoritative
                    // end-of-child marker; this was redundant noise.
                    // else { DebugLog.Write(LogLevel.Info, "parent context restored"); }
                }
            }

            // A child that never started said nothing at all: the launcher just
            // redrew its menu, and "the app is broken" and "the loader refused"
            // looked identical. Name the status on the way out.
            if (result != AppServiceStatus.Ok)
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write("---- child failed: status=");
                UiText.WriteUInt((uint)result);
                DebugLog.EndLine();
            }

            return result;
            }
            finally { s_runExternalDepth--; }
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

        private static void CleanupLoadedImageMappings(ref LoadedImage loadedImage)
        {
            UnmapMappedRange(loadedImage.LowestVirtualAddress,
                             loadedImage.HighestVirtualAddressExclusive,
                             returnPhysicalPages: true);
        }

        private static bool CleanupProcessMappings(ref ProcessImage processImage, ref LoadedImage loadedImage)
        {
            // Both ranges are ours alone — the loader allocated the image's
            // pages and the builder the stack's — so their physical pages go
            // back to the allocator here. Without this, every launch cost the
            // machine its image and stack for good, and enough launches ended
            // in "heap grow failed: no physical pages".
            bool imageCleanupOk = UnmapMappedRange(loadedImage.LowestVirtualAddress,
                                                   loadedImage.HighestVirtualAddressExclusive,
                                                   returnPhysicalPages: true);
            bool stackCleanupOk = UnmapMappedRange(processImage.StackBase,
                                                   processImage.StackMappedTop,
                                                   returnPhysicalPages: true);
            return imageCleanupOk && stackCleanupOk;
        }

        /// <summary>
        /// Unmaps a range, optionally handing its physical pages back.
        /// </summary>
        /// <param name="returnPhysicalPages">
        /// True only for memory this kernel allocated for the process. Freeing
        /// whatever happens to be mapped would eventually hand the allocator a
        /// device window or a piece of the kernel image — both are mapped the
        /// same way and neither was ever ours to give away.
        /// </param>
        private static bool UnmapMappedRange(ulong startInclusive, ulong endExclusive,
            bool returnPhysicalPages = false)
        {
            if (endExclusive <= startInclusive)
                return true;

            // Drop any managed-EH .pdata registration for this base (step140).
            // No-op unless startInclusive is a registered app image base.
            global::OS.Boot.EH.CoffRuntimeFunctionTable.UnregisterImage((byte*)startInclusive);

            ulong current = AlignDown(startInclusive);
            ulong limit = AlignUp(endExclusive);
            while (current < limit)
            {
                // The physical address has to be read BEFORE the mapping goes:
                // afterwards there is nothing left to ask.
                bool mapped = Pager.TryQuery(current, out ulong physical, out _);

                if (mapped && !Pager.Unmap(current))
                    return false;

                if (mapped && returnPhysicalPages && physical != 0)
                    global::OS.Kernel.PhysicalMemory.FreePage(physical);

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

        private static bool TrySyncKernelLowMappings(ref ProcessImage processImage)
        {
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

                // Skip pages already in pager — don't overwrite intentional mappings
                // (JumpStub, service thunks etc. need executable flags that differ from
                // the kernel CR3 view on real hardware with NX-protected data pages).
                if (Pager.TryQuery(current, out _, out _))
                    continue;

                if (!Pager.Map(current, kernelPagePhysical, kernelFlags))
                    return false;
            }

            return true;
        }

        private static bool IsInRange(ulong address, ulong startInclusive, ulong endExclusive)
        {
            return address >= startInclusive && address < endExclusive;
        }
    }
}
