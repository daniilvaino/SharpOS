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
    // Launches are nested and synchronous — the parent waits inside RunApp
    // while the child is built and runs — so one pending block suffices:
    // RunApp fills it from the parent's memory while the parent is still
    // mapped, and the child's startup build takes it.
    internal static unsafe class StartupData
    {
        public const uint Magic = 0x54445353;           // "SSDT"
        public const uint KindArgument = 1;
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

            fixed (Buffer* b = &s_pending)
            {
                byte* block = (byte*)b;
                uint at = HeaderSize;
                uint count = 0;
                uint start = 0;
                for (uint i = 0; i < length; i++)
                {
                    if (list[i] != 0) continue;
                    uint len = i - start;
                    uint padded = (len + 7) & ~7u;
                    if (at + 8 + padded > Capacity) return false;
                    *(uint*)(block + at) = KindArgument;
                    *(uint*)(block + at + 4) = len;
                    for (uint k = 0; k < padded; k++)
                        block[at + 8 + k] = k < len ? list[start + k] : (byte)0;
                    at += 8 + padded;
                    count++;
                    start = i + 1;
                }
                if (start != length) return false;      // last argument not terminated

                *(uint*)(block + 0) = Magic;
                *(uint*)(block + 4) = count;
                *(uint*)(block + 8) = at;
                *(uint*)(block + 12) = 0;
                s_pendingLength = count == 0 ? 0 : at;
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
