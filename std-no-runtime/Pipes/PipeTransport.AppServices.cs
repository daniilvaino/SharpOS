using SharpOS.AppSdk;

namespace SharpOS.Std.Pipes
{
    // An app's side of the pipe transport: every operation is a call through
    // the service table (pipe spec Р44). Zero addresses — an older kernel —
    // answer Unsupported.
    internal static unsafe class PipeTransport
    {
        private static AppServiceTable* Services => AppRuntime.Services;

        public static bool Available => Services != null && Services->PipeSendAddress != 0;

        public static void* Allocate(ulong size) => AppHost.ExchangeAllocate(size);

        public static bool Free(void* block) => AppHost.ExchangeFree(block);

        public static PipeStatus Create(uint capacity, PipeOverflow overflow, out int writer, out int reader)
        {
            writer = reader = 0;
            if (!Available) return PipeStatus.Unsupported;
            int* handles = stackalloc int[2];
            var status = (PipeStatus)((delegate* unmanaged<uint, uint, int*, int>)Services->PipeCreateAddress)(
                capacity, (uint)overflow, handles);
            writer = handles[0];
            reader = handles[1];
            return status;
        }

        public static PipeStatus Connect(string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                         byte[] schema, ulong rootKey, out int handle, out string error)
        {
            handle = 0;
            error = null;
            if (!Available) return PipeStatus.Unsupported;

            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name ?? "");
            byte[] errorBytes = new byte[512];
            ulong* request = stackalloc ulong[12];
            PipeStatus status;
            fixed (byte* namePtr = nameBytes)
            fixed (byte* schemaPtr = schema)
            fixed (byte* errorPtr = errorBytes)
            {
                request[0] = (ulong)namePtr;
                request[1] = (ulong)nameBytes.Length;
                request[2] = (ulong)role;
                request[3] = capacity;
                request[4] = (ulong)overflow;
                request[5] = (ulong)schemaPtr;
                request[6] = schema == null ? 0UL : (ulong)schema.Length;
                request[7] = rootKey;
                request[8] = (ulong)errorPtr;
                request[9] = (ulong)errorBytes.Length;
                request[10] = 0;
                request[11] = 0;
                status = (PipeStatus)((delegate* unmanaged<ulong*, int>)Services->PipeConnectAddress)(request);
            }
            handle = (int)request[10];
            if (request[11] != 0)
                error = System.Text.Encoding.UTF8.GetString(errorBytes, 0, (int)request[11]);
            return status;
        }

        public static PipeStatus Declare(int handle, byte[] schema, ulong rootKey)
        {
            if (!Available) return PipeStatus.Unsupported;
            fixed (byte* schemaPtr = schema)
                return (PipeStatus)((delegate* unmanaged<int, byte*, ulong, ulong, int>)Services->PipeDeclareAddress)(
                    handle, schemaPtr, schema == null ? 0UL : (ulong)schema.Length, rootKey);
        }

        public static PipeStatus Send(int handle, void* block, ulong length)
        {
            if (!Available) return PipeStatus.Unsupported;
            return (PipeStatus)((delegate* unmanaged<int, void*, ulong, int>)Services->PipeSendAddress)(handle, block, length);
        }

        public static PipeStatus Receive(int handle, bool wait, out void* block, out ulong length, out uint dropped)
        {
            block = null;
            length = 0;
            dropped = 0;
            if (!Available) return PipeStatus.Unsupported;
            ulong* answer = stackalloc ulong[3];
            var status = (PipeStatus)((delegate* unmanaged<int, uint, ulong*, int>)Services->PipeReceiveAddress)(
                handle, wait ? 1u : 0u, answer);
            if (status == PipeStatus.Ok)
            {
                block = (void*)answer[0];
                length = answer[1];
            }
            dropped = (uint)answer[2];
            return status;
        }

        /// <summary>The type an end of a pair is opened with, checked against the other end's.</summary>
        public static PipeStatus OpenEnd(int handle, byte[] schema, ulong rootKey, out string error)
        {
            error = null;
            if (!Available) return PipeStatus.Unsupported;
            if (Services->PipeOpenEndAddress == 0)
                return schema == null ? PipeStatus.Ok : Declare(handle, schema, rootKey);   // an older kernel: no check
            byte[] errorBytes = new byte[512];
            ulong* request = stackalloc ulong[7];
            PipeStatus status;
            fixed (byte* schemaPtr = schema)
            fixed (byte* errorPtr = errorBytes)
            {
                request[0] = (ulong)handle;
                request[1] = (ulong)schemaPtr;
                request[2] = schema == null ? 0UL : (ulong)schema.Length;
                request[3] = rootKey;
                request[4] = (ulong)errorPtr;
                request[5] = (ulong)errorBytes.Length;
                request[6] = 0;
                status = (PipeStatus)((delegate* unmanaged<ulong*, int>)Services->PipeOpenEndAddress)(request);
            }
            if (request[6] != 0)
                error = System.Text.Encoding.UTF8.GetString(errorBytes, 0, (int)request[6]);
            return status;
        }

        /// <summary>
        /// The end this program was handed at start in <paramref name="role"/>
        /// (0 input, 1 output): a pipe-end record of the startup data. Zero
        /// when none was.
        /// </summary>
        public static int StandardEnd(uint role)
        {
            AppServiceTable* services = Services;
            if (services == null || services->StartupDataAddress == 0 || services->StartupDataLength < 16)
                return 0;
            byte* block = (byte*)services->StartupDataAddress;
            uint length = services->StartupDataLength;
            if (*(uint*)block != 0x54445353) return 0;      // "SSDT"
            uint count = *(uint*)(block + 4);
            for (uint at = 16, n = 0; n < count && at + 8 <= length; n++)
            {
                uint kind = *(uint*)(block + at);
                uint len = *(uint*)(block + at + 4);
                if (at + 8 + len > length) break;
                if (kind == 2 && len >= 8 && *(uint*)(block + at + 8) == role)
                    return *(int*)(block + at + 12);
                at += 8 + ((len + 7) & ~7u);
            }
            return 0;
        }

        /// <summary>Waits until the other end of the pipe has come; at once on an older kernel.</summary>
        public static PipeStatus WaitPeer(int handle)
        {
            if (!Available || Services->PipeWaitPeerAddress == 0) return PipeStatus.Ok;
            return (PipeStatus)((delegate* unmanaged<int, int>)Services->PipeWaitPeerAddress)(handle);
        }

        /// <summary>A line on the screen: where a message goes when there is no output to send it to.</summary>
        public static void Print(string line) => System.Console.WriteLine(line);

        /// <summary>Text on the screen as it is, no line added (step197: a file's bytes arrive in pieces).</summary>
        public static void PrintText(string text) => System.Console.Write(text);

        public static PipeStatus Close(int handle)
        {
            if (!Available) return PipeStatus.Unsupported;
            return (PipeStatus)((delegate* unmanaged<int, int>)Services->PipeCloseAddress)(handle);
        }

        public static byte[] Schema(int handle, out ulong rootKey)
        {
            rootKey = 0;
            if (!Available) return null;
            ulong* answer = stackalloc ulong[2];
            var call = (delegate* unmanaged<int, byte*, ulong, ulong*, int>)Services->PipeSchemaAddress;
            if ((PipeStatus)call(handle, null, 0, answer) != PipeStatus.Ok || answer[0] == 0)
                return null;
            byte[] schema = new byte[(int)answer[0]];
            fixed (byte* buffer = schema)
                if ((PipeStatus)call(handle, buffer, (ulong)schema.Length, answer) != PipeStatus.Ok)
                    return null;
            rootKey = answer[1];
            return schema;
        }
    }
}
