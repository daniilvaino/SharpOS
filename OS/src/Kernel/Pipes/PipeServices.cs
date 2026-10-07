using System.Runtime.InteropServices;
using SharpOS.Std.Pipes;

namespace OS.Kernel.Pipes
{
    // The pipe operations an app calls through its service table (pipe spec
    // Р44): published directly, Win64 ABI, no thunk. The caller is the run on
    // the calling thread; every handle is checked against it. App memory is
    // readable here: apps run in the kernel's address space.
    internal static unsafe class PipeServices
    {
        [UnmanagedCallersOnly]
        public static int Create(uint capacity, uint overflow, int* handles)
        {
            if (handles == null) return (int)PipeStatus.BadHandle;
            PipeStatus status = KernelPipes.Create(KernelPipes.CallerHolder(), capacity, (PipeOverflow)overflow,
                                                   out int writer, out int reader);
            handles[0] = writer;
            handles[1] = reader;
            return (int)status;
        }

        // request: [0] name, [1] name bytes (UTF-8), [2] role, [3] capacity,
        // [4] overflow, [5] schema, [6] schema bytes, [7] root key, [8] error
        // buffer, [9] its capacity; out: [10] handle, [11] error bytes written.
        [UnmanagedCallersOnly]
        public static int Connect(ulong* request)
        {
            if (request == null) return (int)PipeStatus.BadHandle;
            request[10] = 0;
            request[11] = 0;
            if (request[1] == 0 || request[1] > 256 || request[6] > 64 * 1024)
                return (int)PipeStatus.BadHandle;

            string name = System.Text.Encoding.UTF8.GetString(
                new System.ReadOnlySpan<byte>((byte*)request[0], (int)request[1]));
            byte[] schema = Bytes((byte*)request[5], request[6]);

            PipeStatus status = KernelPipes.Connect(KernelPipes.CallerHolder(), name, (PipeRole)request[2],
                (uint)request[3], (PipeOverflow)request[4], schema, request[7], out int handle, out string error);
            request[10] = (ulong)handle;
            if (error != null && request[8] != 0)
            {
                byte[] text = System.Text.Encoding.UTF8.GetBytes(error);
                ulong n = (ulong)text.Length < request[9] ? (ulong)text.Length : request[9];
                for (ulong i = 0; i < n; i++) ((byte*)request[8])[i] = text[i];
                request[11] = n;
            }
            return (int)status;
        }

        // request: [0] handle, [1] schema, [2] schema bytes, [3] root key (0:
        // a reader without a type), [4] error buffer, [5] its capacity; out
        // [6] error bytes written. The type an end of a pair is opened with.
        [UnmanagedCallersOnly]
        public static int OpenEnd(ulong* request)
        {
            if (request == null || request[2] > 64 * 1024) return (int)PipeStatus.BadHandle;
            request[6] = 0;
            PipeStatus status = KernelPipes.DeclareEnd(KernelPipes.CallerHolder(), (int)request[0],
                Bytes((byte*)request[1], request[2]), request[3], out string error);
            if (error != null && request[4] != 0)
            {
                byte[] text = System.Text.Encoding.UTF8.GetBytes(error);
                ulong n = (ulong)text.Length < request[5] ? (ulong)text.Length : request[5];
                for (ulong i = 0; i < n; i++) ((byte*)request[4])[i] = text[i];
                request[6] = n;
            }
            return (int)status;
        }

        [UnmanagedCallersOnly]
        public static int Declare(int handle, byte* schema, ulong length, ulong rootKey)
        {
            if (length > 64 * 1024) return (int)PipeStatus.BadHandle;
            return (int)KernelPipes.Declare(KernelPipes.CallerHolder(), handle, Bytes(schema, length), rootKey);
        }

        [UnmanagedCallersOnly]
        public static int Send(int handle, void* block, ulong length)
            => (int)KernelPipes.Send(KernelPipes.CallerHolder(), handle, block, length);

        // answer: [0] block, [1] length, [2] dropped before it.
        [UnmanagedCallersOnly]
        public static int Receive(int handle, uint wait, ulong* answer)
        {
            PipeStatus status = KernelPipes.Receive(KernelPipes.CallerHolder(), handle, wait != 0,
                                                    out void* block, out ulong length, out uint dropped);
            if (answer != null)
            {
                answer[0] = (ulong)block;
                answer[1] = length;
                answer[2] = dropped;
            }
            return (int)status;
        }

        [UnmanagedCallersOnly]
        public static int Close(int handle)
            => (int)KernelPipes.Close(KernelPipes.CallerHolder(), handle);

        // answer: [0] schema bytes, [1] root key. Copies when the buffer is large enough.
        [UnmanagedCallersOnly]
        public static int Schema(int handle, byte* buffer, ulong capacity, ulong* answer)
        {
            byte[] schema = KernelPipes.SchemaOf(KernelPipes.CallerHolder(), handle, out ulong rootKey);
            if (answer != null)
            {
                answer[0] = schema == null ? 0UL : (ulong)schema.Length;
                answer[1] = rootKey;
            }
            if (schema == null) return (int)PipeStatus.Ok;
            if (buffer != null && capacity >= (ulong)schema.Length)
                for (int i = 0; i < schema.Length; i++) buffer[i] = schema[i];
            return (int)PipeStatus.Ok;
        }

        private static byte[] Bytes(byte* p, ulong length)
        {
            if (p == null || length == 0) return null;
            var bytes = new byte[(int)length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = p[i];
            return bytes;
        }
    }
}
