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
        private const int OpStart = 1, OpWait = 2, OpHasExited = 3, OpKill = 4, OpRelease = 5, OpCurrentId = 6;

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
        /// for in \apps, with .EXE added when it has no extension) and returns
        /// at once. The ends given become the new process's standard input and
        /// output and are gone from here; when the start fails they stay.
        /// </summary>
        /// <exception cref="InvalidOperationException">The process table is full, or the program could not be started.</exception>
        public static Process Start(string path, string[] arguments = null,
                                    PipeReadEnd input = null, PipeWriteEnd output = null)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (!Available) throw new InvalidOperationException("this kernel does not start processes");

            string full = FullPath(path);
            byte[] pathBytes = new byte[full.Length + 1];
            for (int i = 0; i < full.Length; i++)
            {
                if (full[i] > 0x7F) throw new ArgumentException("the path is not ASCII", nameof(path));
                pathBytes[i] = (byte)full[i];
            }
            byte[] list = EncodeArguments(arguments);

            // The ends are spent now and given back if the start fails.
            int inputHandle = input?.Take("the end was handed to " + full) ?? 0;
            int outputHandle = output?.Take("the end was handed to " + full) ?? 0;

            ulong* request = stackalloc ulong[6];
            AppServiceStatus status;
            fixed (byte* pathPointer = pathBytes)
            fixed (byte* listPointer = list)
            {
                request[0] = (ulong)pathPointer;
                request[1] = (ulong)listPointer;
                request[2] = list == null ? 0UL : (ulong)list.Length;
                request[3] = (ulong)(uint)inputHandle;
                request[4] = (ulong)(uint)outputHandle;
                request[5] = 0;
                status = Call(OpStart, request);
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
            string full = bare ? "\\apps\\" + path : path.Replace('/', '\\');
            return extension ? full : full + ".EXE";
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
