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
        //   Stats:     out [1] physical pages in use outside the kernel heap
        //              and the page tables (both keep what they grew to: the
        //              heap's garbage is the collector's business, and the
        //              tables of a process slot are made once and used by
        //              every process in it), [2] processes running,
        //              [3] process records, [4] exchange blocks live, [5] pipes
        //              live, [6] names waiting, [7] threads live, [8] objects
        //              the kernel's heap ever allocated. For tests that check
        //              what a run gives back, and what a message costs.
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
                             - paging.TablePages - paging.SpareTablePages;
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
                request[5] = 0;
                char* path = stackalloc char[(int)MaxPathChars];
                if (!TryReadAsciiPath(request[0], path, MaxPathChars))
                    return (int)AppServiceStatus.InvalidParameter;
                if (!TryResolveRunAppAbi(path, AppServiceTable.AutoSelectAbiVersion, (uint)AppServiceAbi.Auto, out uint abi, out AppServiceAbi serviceAbi, out AbiResolveSource source))
                    return (int)AppServiceStatus.InvalidParameter;
                AppServiceStatus started = StartFromPath(path, abi, serviceAbi, source == AbiResolveSource.Request,
                    request[2] != 0 ? (byte*)request[1] : null, (uint)request[2],
                    (int)request[3], (int)request[4], out AppProcess child);
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
            process = null;
            BootInfo bootInfo = Platform.GetBootInfo();
            if (bootInfo.FileReadAll == null)
                return AppServiceStatus.Unsupported;

            void* file = null;
            uint fileSize = 0;
            ulong t0 = Ticks();
            AppServiceStatus read = MapBootFileStatus(bootInfo.FileReadAll(path, &file, &fileSize));
            s_tRead += Ticks() - t0;
            if (read != AppServiceStatus.Ok)
                return read;

            // The ends handed over must be the caller's, of the right role; a
            // start that fails leaves them with it (step194 §5).
            uint holder = global::OS.Kernel.Pipes.KernelPipes.CallerHolder();
            if ((inputEnd > 0 && !global::OS.Kernel.Pipes.KernelPipes.IsEnd(holder, inputEnd, SharpOS.Std.Pipes.PipeRole.Reader)) ||
                (outputEnd > 0 && !global::OS.Kernel.Pipes.KernelPipes.IsEnd(holder, outputEnd, SharpOS.Std.Pipes.PipeRole.Writer)))
            {
                global::OS.Kernel.Memory.NativeArena.FreeLarge(file, fileSize);
                return AppServiceStatus.InvalidParameter;
            }

            AppServiceStatus result;
            EnterStart();
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
                    result = StartProcess(new MemoryBlock(file, fileSize), FileNameOf(path),
                        appAbiVersion, serviceAbi, abiFromRequest,
                        AppProcesses.Current?.Id ?? 0, out process, holder, inputEnd, outputEnd);
                }
                StartupData.Clear();   // a start that failed before its build took it
            }
            finally
            {
                LeaveStart();
                global::OS.Kernel.Memory.NativeArena.FreeLarge(file, fileSize);
            }
            return result;
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
                LauncherId = launcherId,
                LauncherHolds = true,
                State = AppProcessState.Running,
            };
            int slot = AppProcesses.TryReserve(proc);
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
                    LogImageManifest(ref proc.Image, appAbiVersion, serviceAbi);
                }
                else if (!proc.Image.ManifestFound && !abiFromRequest)
                {
                    DebugLog.Begin(LogLevel.Warn);
                    Console.Write("[abi] image carries no manifest — falling back to V");
                    Console.WriteUInt(appAbiVersion);
                    DebugLog.EndLine();
                }

                t1 = Ticks();
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
            bool jumped = JumpStub.Run(proc.Built.EntryPoint, proc.Built.StackTop,
                                       proc.Built.StartupBlockVirtual, proc.PagerCr3, out returned);
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

            uint threads = Scheduler.EndProcessThreads(proc);

            // Its threads are gone; now what it held outside them.
            ProcessResources.OnAppEnded(proc.Id, proc.Failed);

            if (!CleanupProcessMappings(ref proc.Built, ref proc.Image))
                DebugLog.Write(LogLevel.Warn, "process cleanup mappings failed");
            AppProcesses.FreeSlot(proc);

            proc.ExitCode = code;
            proc.State = AppProcessState.Exited;
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

        /// <summary>Blocks until the process has ended. False when the waiting thread is itself being ended.</summary>
        internal static bool WaitForExit(AppProcess proc)
        {
            uint running = 0;
            while (true)
            {
                if (proc.ExitedWord != 0) return true;
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
