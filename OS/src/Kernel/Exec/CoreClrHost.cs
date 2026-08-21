using OS.Hal;
using OS.Kernel.Memory;

namespace OS.Kernel.Exec
{
    /// <summary>
    /// The hosted CoreCLR session, kept alive past boot so assemblies can be
    /// started later.
    /// </summary>
    /// <remarks>
    /// The runtime was only ever brought up inside the boot probe, which held
    /// the host handle in a local and dropped it on return — one assembly per
    /// machine, chosen at build time. Running a second one is not a new
    /// capability: the sequential-assembly probe established that a session
    /// takes more than one. What was missing is somewhere to keep the handle.
    ///
    /// Execution goes back onto the boot session's 16 MiB stack. A hosted
    /// assembly JITs as it runs, and reflection-mode recursion overruns an
    /// ordinary stack — the same overflow that made the boot session need this
    /// buffer in the first place. The caller's stack here belongs to a launcher
    /// app and is smaller still.
    /// </remarks>
    internal static unsafe class CoreClrHost
    {
        private const int MaxPathBytes = 512;

#if !SKIP_CORECLR
        // Declared only when the runtime is linked in. An import is resolved at
        // link time whether or not the call can ever happen, so leaving this
        // visible breaks a build with CoreCLR left out — the same way
        // SharpOSHost_AttachCurrentThread did (step159).
        [System.Runtime.InteropServices.DllImport("*", EntryPoint = "coreclr_execute_assembly")]
        private static extern int coreclr_execute_assembly(
            void* hostHandle,
            uint domainId,
            int argc,
            byte** argv,
            byte* managedAssemblyPath,
            uint* exitCode);
#endif

        private static void* s_hostHandle;
        private static uint s_domainId;

        private static void* s_bigStack;
        private static uint s_bigStackSize;

        // Arguments and result for the thunk: it takes none and returns none,
        // because it is entered on a different stack.
        private static byte* s_pathUtf8;
        private static int s_exitCode;
        private static int s_hr;

        /// <summary>True once a runtime is up and holding a domain.</summary>
        public static bool IsRunning => s_hostHandle != null;

        /// <summary>Records the session the boot probe created.</summary>
        public static void Publish(void* hostHandle, uint domainId)
        {
            s_hostHandle = hostHandle;
            s_domainId = domainId;
        }

        /// <summary>Records the stack the boot session ran on, to reuse it.</summary>
        public static void PublishBigStack(void* buffer, uint size)
        {
            s_bigStack = buffer;
            s_bigStackSize = size;
        }

        /// <summary>
        /// Runs a managed assembly and waits for it.
        /// </summary>
        /// <returns>False when there is no runtime, or the host refused.</returns>
        public static bool TryExecute(string path, out int exitCode)
        {
            exitCode = 0;

            if (s_hostHandle == null || path == null || path.Length == 0)
                return false;

            if (!TryEncodePath(path))
                return false;

            s_exitCode = 0;
            s_hr = -1;

            if (s_bigStack != null && s_bigStackSize != 0 && BigStack.IsInitialized)
            {
                if (!BigStack.RunOn(s_bigStack, s_bigStackSize, &ExecuteOnBigStack))
                    return false;
            }
            else
            {
                // Better than refusing outright, and loud about why it may not
                // survive: without the big stack a hosted assembly is one deep
                // JIT recursion away from overrunning whatever it is standing
                // on.
                Log.Write(LogLevel.Warn, "[clr] no big stack — running on the caller's");
                ExecuteCore();
            }

            exitCode = s_exitCode;
            return s_hr == 0;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void ExecuteOnBigStack() => ExecuteCore();

        private static void ExecuteCore()
        {
#if SKIP_CORECLR
            // Nothing publishes a host in this configuration, so TryExecute
            // refuses before reaching here. Kept as a failure rather than a
            // silent zero in case that ever stops being true.
            s_hr = -1;
            s_exitCode = 0;
#else
            uint rawExitCode = 0xFFFFFFFF;

            s_hr = coreclr_execute_assembly(
                s_hostHandle, s_domainId,
                argc: 0, argv: null,
                managedAssemblyPath: s_pathUtf8,
                exitCode: &rawExitCode);

            s_exitCode = (int)rawExitCode;
#endif
        }

        /// <summary>
        /// Copies the path into a kernel buffer as null-terminated UTF-8.
        /// </summary>
        /// <remarks>
        /// Kept off the caller's stack because the thunk runs on a different
        /// one, and out of managed memory because a collection during hosted
        /// execution must not be able to move it.
        /// </remarks>
        private static bool TryEncodePath(string path)
        {
            if (s_pathUtf8 == null)
            {
                s_pathUtf8 = (byte*)KernelHeap.Alloc(MaxPathBytes);
                if (s_pathUtf8 == null)
                    return false;
            }

            int written = 0;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];

                // Paths here are file-system paths on our FAT: ASCII in
                // practice, and anything else is a mistake worth refusing
                // rather than half-encoding.
                if (c == 0 || c > 0x7F)
                    return false;

                if (written + 1 >= MaxPathBytes)
                    return false;

                s_pathUtf8[written++] = (byte)c;
            }

            if (written == 0)
                return false;

            s_pathUtf8[written] = 0;
            return true;
        }
    }
}
