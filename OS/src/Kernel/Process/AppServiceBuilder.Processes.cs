using OS.Boot;
using OS.Hal;
using OS.Kernel.Exec;
using OS.Kernel.Paging;
using OS.Kernel.Threading;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    // Native apps as processes (step194): started asynchronously, each on a
    // main thread of its own, at an address range of its own; waited for
    // without polling; ended with every thread and resource it had.
    internal static unsafe partial class AppServiceBuilder
    {
        // Starts are serialized: the startup data block and the table builder
        // are single (StartupData, TryBuild), and a load is long enough that
        // suppressing preemption across it is not an option.
        private static Mutex s_startLock;

        /// <summary>Takes the start lock; the caller then fills StartupData and calls <see cref="StartProcess"/>.</summary>
        internal static void EnterStart()
        {
            if (s_startLock == null)
            {
                Preemption.Suppress();
                s_startLock ??= new Mutex();
                Preemption.Allow();
            }
            s_startLock.Acquire();
        }

        internal static void LeaveStart() => s_startLock.Release();

        // ---- the Process service (SharpOS.AppSdk.Process) ----

        public const int ProcessOpStart = 1;
        public const int ProcessOpWait = 2;
        public const int ProcessOpHasExited = 3;
        public const int ProcessOpKill = 4;
        public const int ProcessOpRelease = 5;
        public const int ProcessOpCurrentId = 6;
        public const int ProcessOpStats = 7;
        public const int ProcessOpStartTimes = 8;
        public const int ProcessOpMemory = 9;
        public const int ProcessOpMark = 10;
        public const int ProcessOpLifeTimes = 11;
        public const int ProcessOpGetDirectory = 12;
        public const int ProcessOpSetDirectory = 13;
        // [0] bits to change, [1] their values; [2] answers the settings now.
        public const int ProcessOpSettings = 14;

        /// <summary>
        /// AppServiceTable.Settings for every app started from now on (step197);
        /// LauncherBoot turns the build line on for an autorun battery.
        /// </summary>
        internal static ulong Settings;

        // Where a start-and-exit goes after the start (step196), HPET ticks
        // summed over every process that ended: runnable → its first
        // instruction, → its runtime set up, → past its banner, → its code
        // done, → its ending done, → its waiter running again.
        // The ending's parts, summed the same way: other threads, resources,
        // heap pages, image and stack mappings, the log line.
        private static ulong s_eThreads, s_eResources, s_eHeap, s_eMappings, s_eLog;
        // The start service whole, and StartFromPath whole.
        private static ulong s_sService, s_sPath;
        // Between the phases: the start lock, the arguments, StartProcess
        // whole, the slot reserve, the tail (lock release, file back).
        private static ulong s_gLock, s_gArgs, s_gProcess, s_gReserve, s_gTail, s_gManifest;
        private static ulong s_lives, s_lToEntry, s_lInit, s_lBanner, s_lCode, s_lEnd, s_woken, s_lWake;

        // Where a start's time goes (HPET ticks, all starts so far): reading
        // the file, loading and relocating the image, building the startup
        // block and stack, copying the kernel's low mappings, and the rest
        // (validation, the main thread).
        private static ulong s_starts, s_tRead, s_tLoad, s_tBuild, s_tSync, s_tRest;

        private static ulong Ticks() => OS.Hal.Timer.Hpet.IsInitialized ? OS.Hal.Timer.Hpet.ReadCounter() : 0;

        /// <summary>Exit code of a process ended by Kill.</summary>
        public const int KilledExitCode = 137;

        // int (int op, ulong* request), Win64, published directly.
        //   Start:     [0] path (NUL-ended ASCII), [1] arguments (NUL-ended
        //              UTF-8 each), [2] their bytes, [3] input end, [4] output
        //              end (0: none); out [5] process id.
        //   Wait:      [0] id; out [1] exit code. Blocks until it has ended.
        //   HasExited: [0] id; out [1] 1 or 0, [2] exit code.
        //   Kill:      [0] id. Ends it with 137; returns at once.
        //   Release:   [0] id. The caller is done with the record.
        //   CurrentId: out [1] the caller's own id.
        //   Stats:     out [1] physical pages in use outside the kernel heap,
        //              the program cache, the page tables and the heaps of
        //              running processes
        //              (they keep what they grew to: a heap's garbage is its
        //              collector's business, and the tables of a process slot
        //              are made once and used by every process in it; a
        //              process's heap is given back when it ends),
        //              [2] processes running,
        //              [3] process records, [4] exchange blocks live, [5] pipes
        //              live, [6] names waiting, [7] threads live, [8] objects
        //              the kernel's heap ever allocated. For tests that check
        //              what a run gives back, and what a message costs.
        //   Memory:    out [1] pages the heaps of running processes hold, [2]
        //              pages that can still be handed out, [3] pages of the
        //              program cache. How many processes fit (step196).
        //   GetDirectory: [0] buffer, [1] its bytes; out [2] the length of the
        //              caller's working directory (ASCII, no NUL), written when
        //              it fits. SetDirectory: [0] NUL-ended ASCII path (step197).
        //   Mark:      [0] 0 or 1: the caller's runtime is set up (before and
        //              after its banner). Once each.
        //   LifeTimes: out [1] processes ended, HPET ticks summed in [2] to the
        //              first instruction, [3] runtime setup, [4] banner, [5]
        //              the code, [6] the ending; [7] waits woken, [8] ticks
        //              from the wake to the waiter running; [9] HPET Hz; all
        //              endings: [10] other threads, [11] resources, [12] heap
        //              pages, [13] image and stack mappings, [14] the log line.
        //   StartTimes: out [1] starts, HPET ticks spent in [2] reading files,
        //              [3] loading images, [4] building, [5] syncing the
        //              kernel's low mappings, [6] the rest; [7] HPET Hz.
        // Only the process that started one may wait for, kill or release it.
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static int ProcessService(int op, ulong* request)
        {
            if (request == null) return (int)AppServiceStatus.InvalidParameter;
            AppProcess self = AppProcesses.Current;
            uint caller = self?.Id ?? 0;

            if (op == ProcessOpCurrentId)
            {
                request[1] = caller;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpGetDirectory)
            {
                string cwd = self?.WorkingDirectory ?? "\\";
                request[2] = (ulong)cwd.Length;
                if (request[0] != 0 && request[1] >= (ulong)cwd.Length)
                    for (int i = 0; i < cwd.Length; i++) ((byte*)request[0])[i] = (byte)cwd[i];
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpSetDirectory)
            {
                if (self == null) return (int)AppServiceStatus.Unsupported;
                char* path = stackalloc char[(int)MaxPathChars];
                if (!TryReadAsciiPath(request[0], path, MaxPathChars)) return (int)AppServiceStatus.InvalidParameter;
                self.WorkingDirectory = string.FromUtf16Z(path, (int)MaxPathChars);
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpSettings)
            {
                Settings = (Settings & ~request[0]) | (request[1] & request[0]);
                request[2] = Settings;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpMark)
            {
                if (self != null)
                {
                    if (request[0] == 0 && self.TMark0 == 0) self.TMark0 = Ticks();
                    else if (request[0] == 1 && self.TMark1 == 0) self.TMark1 = Ticks();
                }
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpLifeTimes)
            {
                request[1] = s_lives;
                request[2] = s_lToEntry;
                request[3] = s_lInit;
                request[4] = s_lBanner;
                request[5] = s_lCode;
                request[6] = s_lEnd;
                request[7] = s_woken;
                request[8] = s_lWake;
                request[9] = OS.Hal.Timer.Hpet.FrequencyHz;
                request[10] = s_eThreads;
                request[11] = s_eResources;
                request[12] = s_eHeap;
                request[13] = s_eMappings;
                request[14] = s_eLog;
                request[15] = s_sService;
                request[16] = s_sPath;
                request[17] = s_gLock;
                request[18] = s_gArgs;
                request[19] = s_gProcess;
                request[20] = s_gReserve;
                request[21] = s_gTail;
                request[22] = s_gManifest;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpMemory)
            {
                Scheduler.ReapDead();
                request[1] = AppProcesses.RunningHeapPages();
                request[2] = PhysicalMemory.AvailablePages();
                request[3] = ProgramCache.Pages;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpStartTimes)
            {
                request[1] = s_starts;
                request[2] = s_tRead;
                request[3] = s_tLoad;
                request[4] = s_tBuild;
                request[5] = s_tSync;
                request[6] = s_tRest;
                request[7] = OS.Hal.Timer.Hpet.FrequencyHz;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpStats)
            {
                Scheduler.ReapDead();
                Pager.GetSummary(out global::OS.Kernel.Paging.PagingSummary paging);
                request[1] = PhysicalMemory.HandedOutPages - PhysicalMemory.FreedPages
                             - global::OS.Kernel.Memory.KernelHeap.HeapPages
                             - ProgramCache.Pages
                             - paging.TablePages - paging.SpareTablePages
                             - AppProcesses.RunningHeapPages();
                request[2] = (ulong)AppProcesses.Running;
                request[3] = (ulong)AppProcesses.Records;
                request[4] = global::OS.Kernel.Memory.ExchangeHeap.LiveBlocks;
                request[5] = global::OS.Kernel.Pipes.KernelPipes.LivePipes;
                request[6] = (ulong)global::OS.Kernel.Pipes.KernelPipes.NamedWaiting;
                request[7] = Scheduler.ThreadsLive;
                request[8] = SharpOS.Std.NoRuntime.GcHeap.AllocCount;
                return (int)AppServiceStatus.Ok;
            }

            if (op == ProcessOpStart)
            {
                ulong serviceStarted = Ticks();
                request[5] = 0;
                char* path = stackalloc char[(int)MaxPathChars];
                if (!TryReadAsciiPath(request[0], path, MaxPathChars))
                    return (int)AppServiceStatus.InvalidParameter;
                if (!TryResolveRunAppAbi(path, AppServiceTable.AutoSelectAbiVersion, (uint)AppServiceAbi.Auto, out uint abi, out AppServiceAbi serviceAbi, out AbiResolveSource source))
                    return (int)AppServiceStatus.InvalidParameter;
                AppServiceStatus started = StartFromPath(path, abi, serviceAbi, source == AbiResolveSource.Request,
                    request[2] != 0 ? (byte*)request[1] : null, (uint)request[2],
                    (int)request[3], (int)request[4], out AppProcess child);
                s_sService += Ticks() - serviceStarted;
                if (started == AppServiceStatus.Ok) request[5] = child.Id;
                return (int)started;
            }

            AppProcess p = AppProcesses.Find((uint)request[0]);
            if (p == null || p.LauncherId != caller || !p.LauncherHolds)
                return (int)AppServiceStatus.InvalidParameter;

            switch (op)
            {
                case ProcessOpWait:
                    if (!WaitForExit(p)) return (int)AppServiceStatus.DeviceError;   // the waiter is being ended
                    request[1] = (ulong)(long)p.ExitCode;
                    return (int)AppServiceStatus.Ok;
                case ProcessOpHasExited:
                    request[1] = p.ExitedWord != 0 ? 1UL : 0UL;
                    request[2] = (ulong)(long)p.ExitCode;
                    return (int)AppServiceStatus.Ok;
                case ProcessOpKill:
                    if (p.ExitedWord == 0) RequestEnd(p, KilledExitCode, failed: true);
                    return (int)AppServiceStatus.Ok;
                case ProcessOpRelease:
                    ReleaseProcess(p);
                    return (int)AppServiceStatus.Ok;
                default:
                    return (int)AppServiceStatus.InvalidParameter;
            }
        }

        /// <summary>
        /// Reads the program at <paramref name="path"/> and starts it with the
        /// arguments (NUL-ended UTF-8, as the RunApp request carries them) and
        /// the pipe ends given. The file buffer is given back either way.
        /// </summary>
        internal static AppServiceStatus StartFromPath(
            char* path, uint appAbiVersion, AppServiceAbi serviceAbi, bool abiFromRequest,
            byte* arguments, uint argumentsLength, int inputEnd, int outputEnd, out AppProcess process)
        {
            ulong pathStarted = Ticks();
            try
            {
                return StartFromPathCore(path, appAbiVersion, serviceAbi, abiFromRequest, arguments, argumentsLength, inputEnd, outputEnd, out process);
            }
            finally
            {
                s_sPath += Ticks() - pathStarted;
            }
        }

        private static AppServiceStatus StartFromPathCore(
            char* path, uint appAbiVersion, AppServiceAbi serviceAbi, bool abiFromRequest,
            byte* arguments, uint argumentsLength, int inputEnd, int outputEnd, out AppProcess process)
        {
            process = null;
            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadAll == null)
                return AppServiceStatus.Unsupported;

            void* file = null;
            uint fileSize = 0;
            ulong t0 = Ticks();
            bool cached = ProgramCache.TryTake(string.FromUtf16Z(path, (int)MaxPathChars), out file, out fileSize, out AppServiceStatus read);
            if (!cached)
                read = MapBootFileStatus(bootInfo.FileReadAll(path, &file, &fileSize));
            s_tRead += Ticks() - t0;
            if (read != AppServiceStatus.Ok)
                return read;

            // The ends handed over must be the caller's, of the right role; a
            // start that fails leaves them with it (step194 §5).
            uint holder = global::OS.Kernel.Pipes.KernelPipes.CallerHolder();
            if ((inputEnd > 0 && !global::OS.Kernel.Pipes.KernelPipes.IsEnd(holder, inputEnd, SharpOS.Std.Pipes.PipeRole.Reader)) ||
                (outputEnd > 0 && !global::OS.Kernel.Pipes.KernelPipes.IsEnd(holder, outputEnd, SharpOS.Std.Pipes.PipeRole.Writer)))
            {
                GiveBackFile(cached, file, fileSize);
                return AppServiceStatus.InvalidParameter;
            }

            AppServiceStatus result;
            ulong g0 = Ticks();
            EnterStart();
            ulong g1 = Ticks();
            try
            {
                StartupData.Clear();
                if ((arguments != null && !StartupData.SetArguments(arguments, argumentsLength)) ||
                    (inputEnd > 0 && !StartupData.AddPipeEnd(StartupData.RoleInput, inputEnd)) ||
                    (outputEnd > 0 && !StartupData.AddPipeEnd(StartupData.RoleOutput, outputEnd)))
                {
                    result = AppServiceStatus.InvalidParameter;
                }
                else
                {
                    string name = FileNameOf(path);
                    ulong g2 = Ticks();
                    s_gArgs += g2 - g1;
                    result = StartProcess(new MemoryBlock(file, fileSize), name,
                        appAbiVersion, serviceAbi, abiFromRequest,
                        AppProcesses.Current?.Id ?? 0, out process, holder, inputEnd, outputEnd);
                    s_gProcess += Ticks() - g2;
                }
                StartupData.Clear();   // a start that failed before its build took it
            }
            finally
            {
                ulong g3 = Ticks();
                LeaveStart();
                GiveBackFile(cached, file, fileSize);
                s_gTail += Ticks() - g3;
            }
            s_gLock += g1 - g0;
            return result;
        }

        private static void GiveBackFile(bool cached, void* file, uint fileSize)
        {
            if (cached) ProgramCache.Return(file);
            else global::OS.Kernel.Memory.NativeArena.FreeLarge(file, fileSize);
        }

        private static string FileNameOf(char* path)
        {
            string full = string.FromUtf16Z(path, (int)MaxPathChars);
            int cut = full.LastIndexOf('\\');
            int slash = full.LastIndexOf('/');
            if (slash > cut) cut = slash;
            return cut >= 0 ? full.Substring(cut + 1) : full;
        }

        /// <summary>
        /// Asks a process to end with <paramref name="code"/>: every thread of
        /// it is marked, and leaves when it is next in the app's code or out of
        /// the service it is in (a wait there is cut short). The main thread
        /// then ends the process. The first request's code wins.
        /// </summary>
        internal static void RequestEnd(AppProcess proc, int code, bool failed)
        {
            if (proc == null) return;
            Preemption.Suppress();
            if (!proc.KillRequested)
            {
                proc.KillRequested = true;
                proc.KillCode = code;
            }
            proc.Failed |= failed;
            Preemption.Allow();
            if (failed) ProcessResources.MarkFailed(proc.Id);
            Scheduler.RequestKillAll(proc);
        }

        /// <summary>
        /// Loads <paramref name="image"/> as a new process and starts its main
        /// thread. Returns at once; the process runs on its own. Call between
        /// <see cref="EnterStart"/> and <see cref="LeaveStart"/>, with the
        /// startup data in place.
        /// </summary>
        internal static AppServiceStatus StartProcess(
            MemoryBlock image, string name,
            uint appAbiVersion, AppServiceAbi serviceAbi, bool abiFromRequest,
            uint launcherId, out AppProcess process,
            uint endHolder = 0, int inputEnd = 0, int outputEnd = 0)
        {
            process = null;
            var proc = new AppProcess
            {
                Name = name,
                WorkingDirectory = AppProcesses.Current?.WorkingDirectory ?? "\\",
                LauncherId = launcherId,
                LauncherHolds = true,
                State = AppProcessState.Running,
            };
            ulong r0 = Ticks();
            int slot = AppProcesses.TryReserve(proc);
            s_gReserve += Ticks() - r0;
            if (slot < 0)
            {
                DebugLog.Begin(LogLevel.Warn);
                UiText.Write("app start refused: ");
                UiText.WriteInt(AppProcesses.MaxRunning);
                UiText.Write(" processes running");
                DebugLog.EndLine();
                return AppServiceStatus.LimitReached;
            }

            bool imageLoaded = false, processBuilt = false;
            AppServiceStatus result = AppServiceStatus.DeviceError;
            do
            {
                image.TryReadUInt16(0, out ushort imageMagic);
                if (!image.IsValid || imageMagic != global::OS.Kernel.Pe.PeLoader.DosMagicMZ)
                {
                    result = AppServiceStatus.Unsupported;
                    break;
                }

                ulong t0 = Ticks();
                bool loaded = global::OS.Kernel.Pe.PeLoader.TryLoad(image, AppProcesses.ImageBaseForSlot(slot), out proc.Image, out int stage);
                ulong t1 = Ticks();
                s_tLoad += t1 - t0;
                if (!loaded)
                {
                    DebugLog.Begin(LogLevel.Warn);
                    UiText.Write("pe load failed at stage = ");
                    UiText.WriteInt(stage);
                    DebugLog.EndLine();
                    result = FailedAtStep(2);
                    break;
                }
                imageLoaded = true;

                // The image's own manifest outranks the sidecar and the
                // fallback, but not a caller who named the ABI outright.
                if (proc.Image.ManifestFound && !abiFromRequest)
                {
                    appAbiVersion = NormalizeAbiVersion(proc.Image.ManifestAbi);
                    if (TryParseServiceAbi(proc.Image.ManifestServiceAbi, out AppServiceAbi imageServiceAbi))
                        serviceAbi = imageServiceAbi;
                    // Said for what the kernel or the launcher starts, and for an
                    // ABI that is not the current one (step196): on the laptop
                    // this line cost ≈7.8 ms of an 11 ms start-and-exit, on
                    // every start an app made.
                    if (launcherId == 0 || appAbiVersion != AppServiceTable.CurrentAbiVersion)
                        LogImageManifest(ref proc.Image, appAbiVersion, serviceAbi);
                }
                else if (!proc.Image.ManifestFound && !abiFromRequest)
                {
                    DebugLog.Begin(LogLevel.Warn);
                    Console.Write("[abi] image carries no manifest — falling back to V");
                    Console.WriteUInt(appAbiVersion);
                    DebugLog.EndLine();
                }

                ulong tManifest = Ticks();
                s_gManifest += tManifest - t1;
                t1 = tManifest;
                bool built = ProcessImageBuilder.TryBuild(ref proc.Image, 0, serviceAbi, appAbiVersion,
                        AppProcesses.StackTopForSlot(slot), out proc.Built);
                ulong t2 = Ticks();
                s_tBuild += t2 - t1;
                if (!built)
                {
                    result = FailedAtStep(4);
                    break;
                }
                processBuilt = true;

                if (!TryValidateProcess(ref proc.Built, appAbiVersion)) { result = FailedAtStep(5); break; }

                // The heap's range and budget go in this process's table.
                AppServiceTable* table = (AppServiceTable*)proc.Built.ServiceTableVirtual;
                table->HeapBase = AppProcesses.ImageBaseForSlot(slot) + AppProcesses.HeapRegionOffset;
                table->HeapBytes = AppProcesses.HeapRegionBytes;
                table->HeapBudget = AppProcesses.HeapBudgetBytes;
                table->HeapCommitAddress = (ulong)(nint)(delegate* unmanaged<ulong, ulong, int>)&HeapCommit;
                table->HeapReleaseAddress = (ulong)(nint)(delegate* unmanaged<ulong, ulong, int>)&HeapRelease;
                if (!JumpStub.EnsureInitialized()) { result = FailedAtStep(6); break; }
                ulong t3 = Ticks();
                bool synced = TrySyncKernelLowMappings(ref proc.Built);
                ulong t4 = Ticks();
                s_tSync += t4 - t3;
                s_tRest += t3 - t2;
                if (!synced) { result = FailedAtStep(7); break; }
                if (!Pager.TryGetPagerCr3(out ulong pagerCr3) || (pagerCr3 & 0x000FFFFFFFFFF000UL) == 0)
                {
                    result = FailedAtStep(8);
                    break;
                }

                proc.PagerCr3 = pagerCr3 & 0x000FFFFFFFFFF000UL;
                proc.ImageBase = proc.Image.LowestVirtualAddress;
                proc.ImageEnd = proc.Image.HighestVirtualAddressExclusive;
                proc.AbiVersion = s_publishedAbiVersion;

                // The ends become the new process's before any of its code
                // runs; the caller's handles go dead. Either both move or
                // neither does.
                if (!TransferEnds(endHolder, proc.Id, inputEnd, outputEnd)) { result = AppServiceStatus.InvalidParameter; break; }

                Thread main = Scheduler.Spawn(&ProcessMainEntry, MainThreadKernelStackBytes, startRunnable: false);
                if (main == null)
                {
                    TransferEnds(proc.Id, endHolder, inputEnd, outputEnd);
                    result = FailedAtStep(9);
                    break;
                }
                main.App = proc;
                main.AppGeneration = proc.Id;
                proc.MainThread = main;
                proc.LiveThreads = 1;
                ProcessResources.OnAppStarted(proc.Id);

                // A block the process owns and never frees: its end must
                // return it (OnAppEnded logs the count).
                if (OS.Kernel.Diagnostics.Probes.ExchangeHeap)
                    OS.Kernel.Memory.ExchangeHeap.Allocate(64, proc.Id);

                proc.TRunnable = Ticks();
                Scheduler.MakeRunnable(main);
                s_tRest += Ticks() - t4;
                s_starts++;
                process = proc;
                return AppServiceStatus.Ok;
            }
            while (false);

            if (processBuilt)
                CleanupProcessMappings(ref proc.Built, ref proc.Image);
            else if (imageLoaded)
                CleanupLoadedImageMappings(ref proc.Image);
            AppProcesses.FreeSlot(proc);
            AppProcesses.Forget(proc);
            return result;
        }

        private static bool TransferEnds(uint from, uint to, int inputEnd, int outputEnd)
        {
            if (inputEnd > 0 && global::OS.Kernel.Pipes.KernelPipes.Transfer(from, inputEnd, to) != SharpOS.Std.Pipes.PipeStatus.Ok)
                return false;
            if (outputEnd > 0 && global::OS.Kernel.Pipes.KernelPipes.Transfer(from, outputEnd, to) != SharpOS.Std.Pipes.PipeStatus.Ok)
            {
                if (inputEnd > 0) global::OS.Kernel.Pipes.KernelPipes.Transfer(to, inputEnd, from);
                return false;
            }
            return true;
        }

        // The kernel half of a main thread: its stack holds JumpStub.Run while
        // the app runs on the app's own stack, and the ending afterwards.
        private const uint MainThreadKernelStackBytes = 64 * 1024;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void ProcessMainEntry()
        {
            AppProcess proc = Scheduler.Current.App;
            int returned = 0;
            proc.TEntry = Ticks();
            bool jumped = JumpStub.Run(proc.Built.EntryPoint, proc.Built.StackTop,
                                       proc.Built.StartupBlockVirtual, proc.PagerCr3, out returned);
            proc.TCodeDone = Ticks();
            int code = proc.KillRequested ? proc.KillCode
                     : proc.ExitRequested ? proc.RequestedExitCode
                     : jumped ? returned : JumpStub.UnhandledExitCode;
            EndProcess(proc, code);
            Scheduler.Exit();
        }

        /// <summary>
        /// The main thread's own ending: the other threads go, then what the
        /// process held, then its memory; waiters wake.
        /// </summary>
        private static void EndProcess(AppProcess proc, int code)
        {
            proc.State = AppProcessState.Exiting;
            // Any code but 0 breaks the process's pipe ends (step194 §3): the
            // reader drains the queue and gets "broken", not "end of stream".
            if (code != 0) proc.Failed = true;
            if (proc.Failed) ProcessResources.MarkFailed(proc.Id);

            ulong e0 = Ticks();
            uint threads = Scheduler.EndProcessThreads(proc);
            ulong e1 = Ticks();

            // Its threads are gone; now what it held outside them.
            // A child of another app that ended cleanly is not logged (step196):
            // its launcher has the code, and on the laptop each line cost ≈5 ms
            // (screen, serial over USB, disk) — two lines were two thirds of a
            // start-and-exit. Failures, and what the kernel or the launcher
            // started, still are.
            bool quiet = proc.LauncherId != 0 && code == 0;
            ProcessResources.OnAppEnded(proc.Id, proc.Failed, quiet);
            ulong e2 = Ticks();
            ReleaseHeap(proc);
            CloseFilesOf(proc.Id);
            ulong e3 = Ticks();

            if (!CleanupProcessMappings(ref proc.Built, ref proc.Image))
                DebugLog.Write(LogLevel.Warn, "process cleanup mappings failed");
            AppProcesses.FreeSlot(proc);
            ulong e4 = Ticks();

            proc.ExitCode = code;
            proc.State = AppProcessState.Exited;
            if (!quiet || threads != 0)
            {
                DebugLog.Begin(LogLevel.Info);
                UiText.Write("---- ");
                UiText.Write(proc.Name);
                UiText.Write(" end: exit=");
                UiText.WriteInt(code);
                if (threads != 0)
                {
                    UiText.Write(", threads ended with it: ");
                    UiText.WriteInt((int)threads);
                }
                UiText.Write(" ----");
                DebugLog.EndLine();
            }
            ulong e5 = Ticks();
            Preemption.Suppress();
            s_eThreads += e1 - e0;
            s_eResources += e2 - e1;
            s_eHeap += e3 - e2;
            s_eMappings += e4 - e3;
            s_eLog += e5 - e4;
            Preemption.Allow();

            proc.TEnded = Ticks();
            NoteLife(proc);
            Preemption.Suppress();
            proc.ExitedWord = 1;
            bool forget = !proc.LauncherHolds;
            Preemption.Allow();
            fixed (uint* word = &proc.ExitedWord)
                AddressWait.WakeByAddressAll(word);
            if (forget) AppProcesses.Forget(proc);

            // What it started and still held: records of those that ended go,
            // the running ones free theirs when they end.
            AppProcesses.OnLauncherEnded(proc.Id);
        }

        // A complete life only (it reached its code and set up its runtime);
        // a process killed or failed early is left out.
        private static void NoteLife(AppProcess p)
        {
            if (p.TRunnable == 0 || p.TEntry == 0 || p.TMark0 == 0 || p.TMark1 == 0 || p.TCodeDone == 0) return;
            Preemption.Suppress();
            s_lives++;
            s_lToEntry += p.TEntry - p.TRunnable;
            s_lInit += p.TMark0 - p.TEntry;
            s_lBanner += p.TMark1 - p.TMark0;
            s_lCode += p.TCodeDone - p.TMark1;
            s_lEnd += p.TEnded - p.TCodeDone;
            Preemption.Allow();
        }

        // ---- the app's heap (step196) ----

        /// <summary>
        /// Pages under [address, address + bytes) of the caller's heap range,
        /// zeroed. 0 done; LimitReached past the budget; InvalidParameter for a
        /// range not page-aligned or outside; DeviceError without memory.
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int HeapCommit(ulong address, ulong bytes)
        {
            AppProcess p = AppProcesses.Current;
            if (p == null || !InHeap(p, address, bytes)) return (int)AppServiceStatus.InvalidParameter;
            if (p.HeapCommitted + bytes > AppProcesses.HeapBudgetBytes) return (int)AppServiceStatus.LimitReached;
            for (ulong va = address; va < address + bytes; va += 4096)
            {
                ulong pa = PhysicalMemory.AllocPage();
                if (pa == 0 || !Pager.Map(va, pa, PageFlags.Writable | PageFlags.NoExecute))
                {
                    if (pa != 0) PhysicalMemory.FreePage(pa);
                    UnmapMappedRange(address, va, returnPhysicalPages: true);
                    return (int)AppServiceStatus.DeviceError;
                }
                OS.Kernel.Util.Memory.Zero((void*)va, 4096);
            }
            Preemption.Suppress();
            p.HeapCommitted += bytes;
            (p.HeapRanges ??= new System.Collections.Generic.List<ulong>()).Add(address);
            p.HeapRanges.Add(bytes);
            Preemption.Allow();
            return 0;
        }

        /// <summary>Gives back a range HeapCommit gave, whole.</summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static int HeapRelease(ulong address, ulong bytes)
        {
            AppProcess p = AppProcesses.Current;
            if (p == null || !InHeap(p, address, bytes)) return (int)AppServiceStatus.InvalidParameter;
            Preemption.Suppress();
            int found = -1;
            if (p.HeapRanges != null)
                for (int i = 0; i < p.HeapRanges.Count; i += 2)
                    if (p.HeapRanges[i] == address && p.HeapRanges[i + 1] == bytes) { found = i; break; }
            if (found >= 0)
            {
                p.HeapRanges.RemoveAt(found);
                p.HeapRanges.RemoveAt(found);
                p.HeapCommitted -= bytes;
            }
            Preemption.Allow();
            if (found < 0) return (int)AppServiceStatus.InvalidParameter;
            UnmapMappedRange(address, address + bytes, returnPhysicalPages: true);
            return 0;
        }

        private static bool InHeap(AppProcess p, ulong address, ulong bytes)
        {
            ulong low = AppProcesses.ImageBaseForSlot(p.Slot) + AppProcesses.HeapRegionOffset;
            return bytes != 0 && (address & 4095) == 0 && (bytes & 4095) == 0
                   && address >= low && address + bytes <= low + AppProcesses.HeapRegionBytes;
        }

        // The process's end: every page its heap still held.
        private static void ReleaseHeap(AppProcess p)
        {
            System.Collections.Generic.List<ulong> ranges = p.HeapRanges;
            if (ranges == null) return;
            for (int i = 0; i < ranges.Count; i += 2)
                UnmapMappedRange(ranges[i], ranges[i] + ranges[i + 1], returnPhysicalPages: true);
            ranges.Clear();
            p.HeapCommitted = 0;
        }

        /// <summary>Blocks until the process has ended. False when the waiting thread is itself being ended.</summary>
        internal static bool WaitForExit(AppProcess proc)
        {
            uint running = 0;
            bool waited = false;
            while (true)
            {
                if (proc.ExitedWord != 0)
                {
                    if (waited && proc.TEnded != 0)
                    {
                        Preemption.Suppress();
                        s_woken++;
                        s_lWake += Ticks() - proc.TEnded;
                        Preemption.Allow();
                    }
                    return true;
                }
                waited = true;
                Thread self = Scheduler.Current;
                if (self != null && self.KillRequested) return false;
                fixed (uint* word = &proc.ExitedWord)
                    AddressWait.WaitOnAddress(word, &running, 4, 0xFFFFFFFFu);
            }
        }

        /// <summary>A launcher is done with the record: it goes now if the process has ended, else when it does.</summary>
        internal static void ReleaseProcess(AppProcess proc)
        {
            Preemption.Suppress();
            proc.LauncherHolds = false;
            bool forget = proc.State == AppProcessState.Exited;
            Preemption.Allow();
            if (forget) AppProcesses.Forget(proc);
        }

        /// <summary>A thread of the process left the machine: the main thread's ending may be waiting for it.</summary>
        internal static void OnAppThreadGone(AppProcess proc)
        {
            if (proc == null) return;
            System.Threading.Interlocked.Decrement(ref proc.LiveThreads);
            fixed (int* live = &proc.LiveThreads)
                AddressWait.WakeByAddressAll(live);
        }
    }
}
