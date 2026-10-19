using System;
using SharpOS.Std.Pipes;

namespace SharpOS.AppSdk
{
    /// <summary>
    /// A program started by this one (step194): it runs on its own main thread,
    /// in an address range of its own, while this one goes on. Started with
    /// pipe ends for its standard input and output, if any.
    /// </summary>
    /// <remarks>
    /// Not System.Diagnostics.Process: the shape is that one's where it has the
    /// same thing (Start, WaitForExit, HasExited, ExitCode, Id, Kill), the
    /// rest — ends instead of redirected streams, no ProcessStartInfo — is not.
    /// The record of an ended process stays until <see cref="Dispose"/> or until
    /// this program ends.
    /// </remarks>
    public sealed unsafe class Process : IDisposable
    {
        private const int OpStart = 1, OpWait = 2, OpHasExited = 3, OpKill = 4, OpRelease = 5, OpCurrentId = 6, OpStartWithEnvironment = 17, OpImagePath = 18;

        private bool _released;
        private bool _exited;
        private int _exitCode;

        private Process(uint id) => Id = id;

        /// <summary>The kernel's number for the process; its pipe ends and blocks are held under it.</summary>
        public uint Id { get; }

        /// <summary>This program's own id.</summary>
        public static uint CurrentId
        {
            get
            {
                ulong* request = stackalloc ulong[6];
                return Call(OpCurrentId, request) == AppServiceStatus.Ok ? (uint)request[1] : 0u;
            }
        }

        /// <summary>
        /// What the kernel holds right now, for tests that check a run gives
        /// everything back: [0] physical pages in use, [1] processes running,
        /// [2] process records, [3] exchange blocks, [4] pipes, [5] names
        /// waiting for a second end, [6] threads, [7] objects the kernel's heap
        /// ever allocated.
        /// </summary>
        public static ulong[] KernelStats()
        {
            ulong* request = stackalloc ulong[9];
            var stats = new ulong[8];
            if (Call(7, request) == AppServiceStatus.Ok)
                for (int i = 0; i < 8; i++) stats[i] = request[i + 1];
            return stats;
        }

        /// <summary>
        /// Whether programs started from now on print "[app] NAME build ID"
        /// (step197). On for the autorun battery, off at a prompt; a kernel
        /// setting, not this process's.
        /// </summary>
        public static bool AnnounceBuild
        {
            get => (Settings(0, 0) & AppServiceTable.SettingAnnounceBuild) != 0;
            set => Settings(AppServiceTable.SettingAnnounceBuild, value ? AppServiceTable.SettingAnnounceBuild : 0);
        }

        // The kernel's settings for app starts: the bits in mask set to value; the settings after.
        private static ulong Settings(ulong mask, ulong value)
        {
            ulong* request = stackalloc ulong[4];
            request[0] = mask;
            request[1] = value;
            return Call(14, request) == AppServiceStatus.Ok ? request[2] : 0;
        }

        /// <summary>
        /// The process's working directory (step197): where relative paths
        /// start. Inherited from whoever started it; "\" for what the kernel
        /// started. The shell's `cd` sets it.
        /// </summary>
        public static string WorkingDirectory
        {
            get => Environment.CurrentDirectory;
            set => Environment.CurrentDirectory = value;
        }

        /// <summary>The file this process was loaded from (step207); "" when the kernel does not say.</summary>
        public static string ImagePath
        {
            get
            {
                ulong* request = stackalloc ulong[4];
                byte* buffer = stackalloc byte[780];
                request[0] = (ulong)buffer;
                request[1] = 780;
                if (Call(OpImagePath, request) != AppServiceStatus.Ok || request[2] > 780) return "";
                return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer, (int)request[2]));
            }
        }

        /// <summary>
        /// A path as the kernel takes it: '/' turned into '\', and a relative
        /// one joined to the working directory.
        /// </summary>
        public static string ResolvePath(string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            var built = new System.Text.StringBuilder();
            bool absolute = path.Length > 0 && (path[0] == '\\' || path[0] == '/');
            if (!absolute)
            {
                string cwd = WorkingDirectory;
                built.Append(cwd);
                if (cwd.Length == 0 || cwd[cwd.Length - 1] != '\\') built.Append('\\');
            }
            for (int i = 0; i < path.Length; i++) built.Append(path[i] == '/' ? '\\' : path[i]);
            return NormalizePath(built.ToString());
        }

        /// <summary>
        /// A full path with "." and ".." taken (step208), the way
        /// Path.GetFullPath does: "\bin\..\home\." is "\home", ".." above the
        /// root stays at the root, doubled and trailing separators go. Before,
        /// ".." reached the file system as a name — and the root has no ".."
        /// entry, so "cd ../" from \bin led to \bin\.. and nowhere after.
        /// </summary>
        public static string NormalizePath(string full)
        {
            var parts = new System.Collections.Generic.List<string>();
            int start = 0;
            for (int i = 0; i <= full.Length; i++)
            {
                if (i < full.Length && full[i] != '\\' && full[i] != '/') continue;
                int length = i - start;
                if (length == 1 && full[start] == '.') { }
                else if (length == 2 && full[start] == '.' && full[start + 1] == '.')
                {
                    if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                }
                else if (length > 0) parts.Add(full.Substring(start, length));
                start = i + 1;
            }
            if (parts.Count == 0) return "\\";
            var built = new System.Text.StringBuilder();
            foreach (string part in parts) built.Append('\\').Append(part);
            return built.ToString();
        }

        /// <summary>Tells the kernel this process's runtime is set up: 0 before its banner, 1 after (step196).</summary>
        internal static void Mark(int which)
        {
            ulong* request = stackalloc ulong[9];
            request[0] = (ulong)which;
            Call(10, request);
        }

        /// <summary>
        /// Milliseconds summed over ended processes: [0] count, [1] runnable to
        /// first instruction, [2] runtime setup, [3] banner, [4] the code, [5]
        /// the ending; [6] waits woken, [7] wake to waiter running; the ending's
        /// parts over all endings: [8] other threads, [9] resources, [10] heap
        /// pages, [11] image and stack mappings, [12] the log line.
        /// </summary>
        public static double[] LifeTimes()
        {
            ulong* request = stackalloc ulong[23];
            var times = new double[21];
            if (Call(11, request) != AppServiceStatus.Ok || request[9] == 0) return times;
            times[0] = request[1];
            for (int i = 1; i < 6; i++) times[i] = request[i + 1] * 1000.0 / request[9];
            times[6] = request[7];
            times[7] = request[8] * 1000.0 / request[9];
            for (int i = 8; i < 21; i++) times[i] = request[i + 2] * 1000.0 / request[9];
            return times;
        }

        /// <summary>Pages: [0] held by the heaps of running processes, [1] still free, [2] in the kernel's program cache.</summary>
        public static ulong[] MemoryPages()
        {
            ulong* request = stackalloc ulong[9];
            var pages = new ulong[3];
            if (Call(9, request) == AppServiceStatus.Ok)
                for (int i = 0; i < 3; i++) pages[i] = request[i + 1];
            return pages;
        }

        /// <summary>The kernel's program cache: [0] starts it served, [1] starts that read the file.</summary>
        public static ulong[] ProgramCacheCounts()
        {
            ulong* request = stackalloc ulong[9];
            var counts = new ulong[2];
            if (Call(20, request) == AppServiceStatus.Ok)
            {
                counts[0] = request[0];
                counts[1] = request[1];
            }
            return counts;
        }

        /// <summary>Objects the kernel's heap ever allocated, read into a caller's buffer: no allocation of its own.</summary>
        public static ulong KernelAllocations()
        {
            ulong* request = stackalloc ulong[9];
            return Call(7, request) == AppServiceStatus.Ok ? request[8] : 0;
        }

        /// <summary>
        /// Where the kernel's starts spent their time, all starts so far: [0]
        /// starts, then milliseconds reading files, loading and relocating
        /// images, building, copying the kernel's low mappings, and the rest.
        /// </summary>
        public static double[] StartTimes()
        {
            ulong* request = stackalloc ulong[8];
            var times = new double[6];
            if (Call(8, request) != AppServiceStatus.Ok || request[7] == 0) return times;
            times[0] = request[1];
            for (int i = 1; i < 6; i++) times[i] = request[i + 1] * 1000.0 / request[7];
            return times;
        }

        private static bool Available => AppRuntime.Services != null && AppRuntime.Services->ProcessAddress != 0;

        private static AppServiceStatus Call(int op, ulong* request)
        {
            if (!Available) return AppServiceStatus.Unsupported;
            return (AppServiceStatus)((delegate* unmanaged<int, ulong*, int>)AppRuntime.Services->ProcessAddress)(op, request);
        }

        /// <summary>
        /// Starts the program at <paramref name="path"/> (a bare name is looked
        /// for along PATH, with .EXE added when it has no extension) and returns
        /// at once. The ends given become the new process's standard input and
        /// output and are gone from here; when the start fails they stay.
        /// </summary>
        /// <exception cref="InvalidOperationException">The process table is full, or the program could not be started.</exception>
        /// <param name="environment">
        /// The child's environment, NAME=value each (step207); null for a copy
        /// of this process's.
        /// </param>
        public static Process Start(string path, string[] arguments = null,
                                    PipeReadEnd input = null, PipeWriteEnd output = null,
                                    string[] environment = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (!Available) throw new InvalidOperationException("this kernel does not start processes");

            string full = FullPath(path);
            // UTF-8 (step208; before, ASCII only).
            byte[] encoded = System.Text.Encoding.UTF8.GetBytes(full);
            byte[] pathBytes = new byte[encoded.Length + 1];
            Array.Copy(encoded, pathBytes, encoded.Length);
            byte[] list = EncodeArguments(arguments);

            // The ends are spent now and given back if the start fails.
            int inputHandle = input?.Take("the end was handed to " + full) ?? 0;
            int outputHandle = output?.Take("the end was handed to " + full) ?? 0;

            char[] block = null;
            if (environment != null)
            {
                foreach (string entry in environment)
                {
                    int eq = entry == null ? -1 : entry.IndexOf('=');
                    if (eq <= 0 || entry.IndexOf('\0') >= 0)
                        throw new ArgumentException("an environment entry is NAME=value: '" + entry + "'", nameof(environment));
                }
                int length = SharpOS.Std.NoRuntime.EnvironmentBlock.Encode(environment, null, 0);
                block = new char[length + 1];
                fixed (char* b = block) SharpOS.Std.NoRuntime.EnvironmentBlock.Encode(environment, b, length);
            }

            ulong* request = stackalloc ulong[8];
            AppServiceStatus status;
            fixed (byte* pathPointer = pathBytes)
            fixed (byte* listPointer = list)
            fixed (char* blockPointer = block)
            {
                request[0] = (ulong)pathPointer;
                request[1] = (ulong)listPointer;
                request[2] = list == null ? 0UL : (ulong)list.Length;
                request[3] = (ulong)(uint)inputHandle;
                request[4] = (ulong)(uint)outputHandle;
                request[5] = 0;
                request[6] = (ulong)blockPointer;
                request[7] = block == null ? 0UL : (ulong)(block.Length - 1);
                status = Call(block == null ? OpStart : OpStartWithEnvironment, request);
            }
            if (status != AppServiceStatus.Ok)
            {
                if (inputHandle != 0) input.Restore(inputHandle);
                if (outputHandle != 0) output.Restore(outputHandle);
                throw new InvalidOperationException(status == AppServiceStatus.LimitReached
                    ? "cannot start " + full + ": too many processes are running"
                    : "cannot start " + full + ": " + Explain(status));
            }
            return new Process((uint)request[5]);
        }

        /// <summary>Waits, without polling, until the process has ended.</summary>
        public void WaitForExit()
        {
            if (_exited) return;
            ThrowIfReleased();
            ulong* request = stackalloc ulong[3];
            request[0] = Id;
            AppServiceStatus status = Call(OpWait, request);
            if (status != AppServiceStatus.Ok)
                throw new InvalidOperationException("waiting for process " + Id + ": " + Explain(status));
            _exitCode = (int)(long)request[1];
            _exited = true;
        }

        /// <summary>Whether it has ended, its resources returned.</summary>
        public bool HasExited
        {
            get
            {
                if (_exited) return true;
                ThrowIfReleased();
                ulong* request = stackalloc ulong[3];
                request[0] = Id;
                if (Call(OpHasExited, request) != AppServiceStatus.Ok || request[1] == 0) return false;
                _exitCode = (int)(long)request[2];
                _exited = true;
                return true;
            }
        }

        /// <summary>The code it ended with: its own, 134 after an unhandled exception, 137 after Kill.</summary>
        public int ExitCode
        {
            get
            {
                if (!HasExited) throw new InvalidOperationException("process " + Id + " has not exited");
                return _exitCode;
            }
        }

        /// <summary>
        /// Ends the process with 137: its threads leave when they are next in
        /// its code or out of the kernel service they are in. Returns at once;
        /// <see cref="WaitForExit"/> waits for the end.
        /// </summary>
        public void Kill()
        {
            if (_exited) return;
            ThrowIfReleased();
            ulong* request = stackalloc ulong[3];
            request[0] = Id;
            Call(OpKill, request);
        }

        /// <summary>Lets the kernel forget the record (now, or when the process ends).</summary>
        public void Dispose()
        {
            if (_released) return;
            _released = true;
            ulong* request = stackalloc ulong[3];
            request[0] = Id;
            Call(OpRelease, request);
        }

        private void ThrowIfReleased()
        {
            if (_released) throw new ObjectDisposedException("Process", "the process record was released");
        }

        private static string FullPath(string path)
        {
            bool bare = true;
            bool extension = false;
            for (int i = 0; i < path.Length; i++)
            {
                char c = path[i];
                if (c == '\\' || c == '/') { bare = false; extension = false; }
                else if (c == '.') extension = true;
            }
            if (bare) return FindProgram(path) ?? (extension ? path : path + ".EXE");
            string full = ResolvePath(path);
            return extension ? full : full + ".EXE";
        }

        /// <summary>
        /// A program by its bare name (step207): the first directory of PATH
        /// (separated by ';') holding NAME, or NAME.EXE when the name has no
        /// extension. Null when none does.
        /// </summary>
        public static string FindProgram(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            bool extension = name.IndexOf('.') >= 0;
            string path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string directory in path.Split(';'))
            {
                if (directory.Length == 0) continue;
                string dir = directory.Replace('/', '\\');
                string folder = dir[dir.Length - 1] == '\\' ? dir : dir + "\\";
                string file = name + (extension ? "" : ".EXE");
                if (AppHost.FileExistsEx(folder + file) != AppServiceStatus.Ok) continue;
                // The name as the directory holds it (step208), not as typed:
                // `which pipegen` said \bin\pipegen.EXE.
                foreach (System.IO.Directory.Entry e in System.IO.Directory.Entries(NormalizePath(folder)))
                    if (!e.IsDirectory && string.Equals(e.Name, file, StringComparison.OrdinalIgnoreCase)) return folder + e.Name;
                return folder + file;
            }
            return null;
        }

        private static byte[] EncodeArguments(string[] arguments)
        {
            if (arguments == null || arguments.Length == 0) return null;
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
                for (int k = 0; k < encoded[i].Length; k++) list[at++] = encoded[i][k];
                list[at++] = 0;
            }
            return list;
        }

        private static string Explain(AppServiceStatus status)
        {
            switch (status)
            {
                case AppServiceStatus.NotFound: return "not found";
                case AppServiceStatus.InvalidParameter: return "invalid parameter (a pipe end not this program's, or of the wrong role?)";
                case AppServiceStatus.Unsupported: return "not a program this kernel runs";
                case AppServiceStatus.DeviceError: return "the start failed (see the kernel log)";
                default: return "status " + ((uint)status).ToString();
            }
        }
    }
}
