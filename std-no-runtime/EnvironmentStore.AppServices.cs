using System;
using SharpOS.AppSdk;

namespace SharpOS.Std.NoRuntime
{
    /// <summary>
    /// Where an app's environment and working directory are (step207): with
    /// the kernel, in its process record, through the Process service. Read
    /// on every call — a variable is not read often enough to cache, and a
    /// cache would be a second copy to keep right.
    /// </summary>
    public static unsafe class EnvironmentStore
    {
        // AppServiceBuilder.ProcessOp*.
        private const int OpGetDirectory = 12, OpSetDirectory = 13, OpGetEnvironment = 15, OpSetEnvironment = 16;
        private const int MaxPath = 260;

        private static bool Available => AppRuntime.Services != null && AppRuntime.Services->ProcessAddress != 0;

        private static AppServiceStatus Call(int op, ulong* request)
            => (AppServiceStatus)((delegate* unmanaged<int, ulong*, int>)AppRuntime.Services->ProcessAddress)(op, request);

        /// <summary>The process's variables, NAME=value; none when the kernel has no such service.</summary>
        public static string[] GetAll()
        {
            if (!Available) return new string[0];
            ulong* request = stackalloc ulong[4];
            request[0] = 0;
            request[1] = 0;
            if (Call(OpGetEnvironment, request) != AppServiceStatus.Ok) return new string[0];
            int length = (int)request[2];
            if (length == 0) return new string[0];
            var block = new char[length];
            fixed (char* p = block)
            {
                request[0] = (ulong)p;
                request[1] = (ulong)length;
                if (Call(OpGetEnvironment, request) != AppServiceStatus.Ok || (int)request[2] != length) return new string[0];
                return EnvironmentBlock.Decode(p, length) ?? new string[0];
            }
        }

        /// <summary>The process's variables from now on.</summary>
        public static void SetAll(string[] environment)
        {
            if (!Available) throw new PlatformNotSupportedException("this kernel keeps no environment");
            int length = EnvironmentBlock.Encode(environment, null, 0);
            if (length > EnvironmentBlock.MaxChars)
                throw new InvalidOperationException("the environment would take " + length.ToString() + " characters, more than " + EnvironmentBlock.MaxChars.ToString());
            var block = new char[length];
            fixed (char* p = block)
            {
                EnvironmentBlock.Encode(environment, p, length);
                ulong* request = stackalloc ulong[4];
                request[0] = (ulong)p;
                request[1] = (ulong)length;
                if (Call(OpSetEnvironment, request) != AppServiceStatus.Ok)
                    throw new InvalidOperationException("the kernel refused the environment");
            }
        }

        public static string GetDirectory()
        {
            if (!Available) return "\\";
            // UTF-8 (step208; before, a byte a character): up to three bytes each.
            ulong* request = stackalloc ulong[4];
            byte* buffer = stackalloc byte[MaxPath * 3];
            request[0] = (ulong)buffer;
            request[1] = MaxPath * 3;
            if (Call(OpGetDirectory, request) != AppServiceStatus.Ok || request[2] > MaxPath * 3 || request[2] == 0) return "\\";
            return System.Text.Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer, (int)request[2]));
        }

        public static void SetDirectory(string path)
        {
            if (!Available) throw new PlatformNotSupportedException("this kernel keeps no working directory");
            if (path.Length >= MaxPath) throw new ArgumentException("the path is longer than " + (MaxPath - 1).ToString() + " characters", nameof(path));
            int length = System.Text.Encoding.UTF8.GetByteCount(path);
            byte* encoded = stackalloc byte[length + 1];
            System.Text.Encoding.UTF8.GetBytes(path, new Span<byte>(encoded, length));
            encoded[length] = 0;
            ulong* request = stackalloc ulong[4];
            request[0] = (ulong)encoded;
            if (Call(OpSetDirectory, request) != AppServiceStatus.Ok)
                throw new InvalidOperationException("the working directory cannot be set");
        }
    }
}
