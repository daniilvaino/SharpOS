namespace SharpOS.AppSdk
{
    // Threads for a freestanding app, via the kernel's service table (ABI V3).
    //
    // The app is mapped into the kernel's address space and runs on the kernel's
    // scheduler, so a thread here is the same thing a kernel thread is — the
    // service call hands over an entry pointer and the scheduler does the rest.
    //
    // Why an app needs them at all: Terminal.Gui keeps input decoding and the
    // resize watch on background loops, and a loop that never returns cannot be
    // run inline. Without threads the library does not merely run slowly, it
    // does not run.
    internal static unsafe class AppThreads
    {
        /// <summary>
        /// True when the kernel published the V3 services. An app built against
        /// a V3 SDK can still be launched by an older kernel, and finding out by
        /// calling through a null pointer is not a diagnosis.
        /// </summary>
        public static bool IsAvailable
        {
            get
            {
                var services = AppRuntime.Services;
                return services != null
                    && services->AbiVersion >= AppServiceTable.AbiVersionV3
                    && services->SpawnThreadAddress != 0
                    && services->SleepAddress != 0;
            }
        }

        /// <summary>
        /// Starts <paramref name="entry"/> on a thread of its own. The entry is
        /// a plain function pointer: nothing travels with it, so whatever it
        /// needs has to be reachable from statics.
        /// </summary>
        public static bool Spawn(delegate* unmanaged<void> entry)
        {
            if (entry == null || !IsAvailable) return false;

            var spawn = (delegate* unmanaged<ulong, uint>)AppRuntime.Services->SpawnThreadAddress;
            return spawn((ulong)entry) == 0;   // AppServiceStatus.Ok
        }

        /// <summary>
        /// Which thread is running, as the scheduler names it. Zero when the
        /// kernel does not publish it, which callers must treat as "unknown"
        /// rather than as an id — Monitor keys ownership on this, and a shared
        /// value would make every lock look like it was already ours.
        /// </summary>
        public static int CurrentThreadId()
        {
            // Gated on the address, not on a version number: the table grows
            // at the end and an unfilled service reads as zero, which is the
            // same question asked more directly.
            var services = AppRuntime.Services;
            if (services == null || services->CurrentThreadIdAddress == 0)
                return 0;

            var current = (delegate* unmanaged<uint>)services->CurrentThreadIdAddress;
            return (int)current();
        }

        /// <summary>
        /// Gives up the CPU for a while. Not optional for a polling loop: one
        /// that spins instead is the shape we spent step157 removing from the
        /// kernel's own waits.
        /// </summary>
        public static void Sleep(uint milliseconds)
        {
            if (!IsAvailable) return;

            var sleep = (delegate* unmanaged<uint, void>)AppRuntime.Services->SleepAddress;
            sleep(milliseconds);
        }
    }
}
