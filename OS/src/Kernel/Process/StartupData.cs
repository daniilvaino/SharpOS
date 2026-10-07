namespace OS.Kernel.Process
{
    // What a program is handed when it starts, beyond its image and its
    // service table (pipe_plan.md "Подготовить под трубы", item 3).
    //
    // One mechanism for everything of the kind: a block of records the kernel
    // writes onto the new program's stack, under its startup block, and
    // publishes in the service table (StartupDataAddress/Length). Today the
    // records are arguments; pipe ends handed over at launch (Р28) become
    // another kind of record, not another mechanism.
    //
    //   header  uint Magic, uint Count, uint Length (whole block), uint Reserved
    //   record  uint Kind, uint Length, Length bytes, padded to 8
    //
    //   kind 1  an argument, UTF-8
    //   kind 2  a pipe end handed over: uint Role, int Handle (step194 §5);
    //           Role 0 is the standard input, 1 the standard output, the
    //           rest kept for other roles
    //
    // Starts are serialized (AppServiceBuilder.EnterStart), so one pending
    // block suffices: the start fills it, the new process's build takes it.
    internal static unsafe class StartupData
    {
        public const uint Magic = 0x54445353;           // "SSDT"
        public const uint KindArgument = 1;
        public const uint KindPipeEnd = 2;
        public const uint RoleInput = 0;
        public const uint RoleOutput = 1;
        public const int HeaderSize = 16;
        public const int Capacity = 4096;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = Capacity)]
        private struct Buffer { }

        private static Buffer s_pending;
        private static uint s_pendingLength;

        public static uint PendingLength => s_pendingLength;

        public static void Clear() => s_pendingLength = 0;

        /// <summary>
        /// Arguments from a RunApp request: UTF-8 strings, each ended by a NUL,
        /// <paramref name="length"/> bytes in all. False when they do not fit.
        /// </summary>
        public static bool SetArguments(byte* list, uint length)
        {
            s_pendingLength = 0;
            if (list == null || length == 0) return true;

            uint start = 0;
            for (uint i = 0; i < length; i++)
            {
                if (list[i] != 0) continue;
                if (!Append(KindArgument, list + start, i - start)) { s_pendingLength = 0; return false; }
                start = i + 1;
            }
            if (start != length) { s_pendingLength = 0; return false; }   // last argument not terminated
            return true;
        }

        /// <summary>A pipe end the new process gets in <paramref name="role"/>.</summary>
        public static bool AddPipeEnd(uint role, int handle)
        {
            uint* record = stackalloc uint[2];
            record[0] = role;
            record[1] = (uint)handle;
            return Append(KindPipeEnd, (byte*)record, 8);
        }

        private static bool Append(uint kind, byte* data, uint len)
        {
            fixed (Buffer* b = &s_pending)
            {
                byte* block = (byte*)b;
                if (s_pendingLength == 0)
                {
                    *(uint*)(block + 0) = Magic;
                    *(uint*)(block + 4) = 0;
                    *(uint*)(block + 8) = HeaderSize;
                    *(uint*)(block + 12) = 0;
                }
                uint at = *(uint*)(block + 8);
                uint padded = (len + 7) & ~7u;
                if (at + 8 + padded > Capacity) return false;
                *(uint*)(block + at) = kind;
                *(uint*)(block + at + 4) = len;
                for (uint k = 0; k < padded; k++)
                    block[at + 8 + k] = k < len ? data[k] : (byte)0;
                at += 8 + padded;
                *(uint*)(block + 4) += 1;
                *(uint*)(block + 8) = at;
                s_pendingLength = at;
            }
            return true;
        }

        /// <summary>Copies the pending block to <paramref name="destination"/> and clears it.</summary>
        public static uint Take(byte* destination)
        {
            uint length = s_pendingLength;
            if (length == 0) return 0;
            fixed (Buffer* b = &s_pending)
                for (uint i = 0; i < length; i++)
                    destination[i] = ((byte*)b)[i];
            s_pendingLength = 0;
            return length;
        }
    }
}
