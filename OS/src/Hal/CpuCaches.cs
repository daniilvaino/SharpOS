namespace OS.Hal
{
    /// <summary>Cache kind, numbered as Windows' PROCESSOR_CACHE_TYPE.</summary>
    internal enum CacheKind : byte
    {
        Unified = 0,
        Instruction = 1,
        Data = 2,
    }

    internal struct CacheInfo
    {
        public byte Level;
        public CacheKind Kind;
        /// <summary>0 when CPUID does not say.</summary>
        public byte Ways;
        public ushort LineSize;
        public uint Size;
    }

    /// <summary>
    /// The processor's caches, as CPUID describes them.
    /// </summary>
    /// <remarks>
    /// The hosted GC sizes its youngest generation from the largest cache
    /// (gen0 budget = 4/5 of it). Nothing answered that question, so the
    /// budget sat at its 256 KB floor: a benchmark allocating 17 MB of short-
    /// lived objects paid for ~80 collections where the same program on
    /// Linux, in the same QEMU, paid for 2 (step169).
    ///
    /// Three sources, tried in order, because no single one covers every
    /// machine: leaf 4 (Intel's deterministic cache parameters), leaf
    /// 0x8000001D (the same format on AMD, when TOPOEXT says it exists), and
    /// the older AMD leaves 0x80000005/6 — which is all QEMU's default qemu64
    /// model, an "AuthenticAMD" part, may offer.
    /// </remarks>
    internal static unsafe class CpuCaches
    {
        public const int Max = 8;

        private static bool s_logged;

        /// <summary>Fills up to <paramref name="capacity"/> entries; returns how many.</summary>
        public static int Read(CacheInfo* caches, int capacity)
        {
            if (caches == null || capacity <= 0)
                return 0;

            uint* r = stackalloc uint[4];
            if (!X64Asm.Cpuid(0, 0, r))
                return 0;
            uint maxBasic = r[0];

            X64Asm.Cpuid(0x80000000, 0, r);
            uint maxExtended = r[0];

            string source = "none";
            int count = 0;

            if (maxBasic >= 4)
            {
                count = ReadDeterministic(4, caches, capacity);
                source = "leaf 4";
            }

            if (count == 0 && maxExtended >= 0x8000001D)
            {
                X64Asm.Cpuid(0x80000001, 0, r);
                bool topologyExtensions = (r[2] & (1u << 22)) != 0;
                if (topologyExtensions)
                {
                    count = ReadDeterministic(0x8000001D, caches, capacity);
                    source = "leaf 0x8000001D";
                }
            }

            if (count == 0 && maxExtended >= 0x80000006)
            {
                count = ReadLegacyAmd(caches, capacity);
                source = "leaves 0x80000005/6";
            }

            if (!s_logged)
            {
                s_logged = true;
                Log(caches, count, source);
            }

            return count;
        }

        // Leaf 4 and 0x8000001D share a layout: one subleaf per cache until
        // the type field reads 0.
        private static int ReadDeterministic(uint leaf, CacheInfo* caches, int capacity)
        {
            uint* r = stackalloc uint[4];
            int count = 0;
            for (uint sub = 0; sub < 16 && count < capacity; sub++)
            {
                X64Asm.Cpuid(leaf, sub, r);
                uint type = r[0] & 0x1F;
                if (type == 0)
                    break;

                uint ways = ((r[1] >> 22) & 0x3FF) + 1;
                uint partitions = ((r[1] >> 12) & 0x3FF) + 1;
                uint line = (r[1] & 0xFFF) + 1;
                uint sets = r[2] + 1;

                caches[count].Level = (byte)((r[0] >> 5) & 7);
                caches[count].Kind = type == 1 ? CacheKind.Data : type == 2 ? CacheKind.Instruction : CacheKind.Unified;
                caches[count].Ways = ways > 255 ? (byte)255 : (byte)ways;
                caches[count].LineSize = (ushort)line;
                caches[count].Size = ways * partitions * line * sets;
                count++;
            }
            return count;
        }

        private static int ReadLegacyAmd(CacheInfo* caches, int capacity)
        {
            uint* r = stackalloc uint[4];
            int count = 0;

            X64Asm.Cpuid(0x80000005, 0, r);
            // ECX = L1 data, EDX = L1 instruction: size in KB [31:24],
            // associativity [23:16], line size [7:0].
            count = AddLegacy(caches, capacity, count, 1, CacheKind.Data, (r[2] >> 24) * 1024, (byte)((r[2] >> 16) & 0xFF), r[2] & 0xFF);
            count = AddLegacy(caches, capacity, count, 1, CacheKind.Instruction, (r[3] >> 24) * 1024, (byte)((r[3] >> 16) & 0xFF), r[3] & 0xFF);

            X64Asm.Cpuid(0x80000006, 0, r);
            // ECX = L2: size in KB [31:16]; EDX = L3: size in 512 KB units
            // [31:18]. Associativity here is an encoded field, not a count.
            count = AddLegacy(caches, capacity, count, 2, CacheKind.Unified, (r[2] >> 16) * 1024, Ways((r[2] >> 12) & 0xF), r[2] & 0xFF);
            count = AddLegacy(caches, capacity, count, 3, CacheKind.Unified, (r[3] >> 18) * 512 * 1024, Ways((r[3] >> 12) & 0xF), r[3] & 0xFF);
            return count;
        }

        private static int AddLegacy(CacheInfo* caches, int capacity, int count, byte level, CacheKind kind, uint size, byte ways, uint line)
        {
            if (size == 0 || count >= capacity)
                return count;
            caches[count].Level = level;
            caches[count].Kind = kind;
            caches[count].Ways = ways;
            caches[count].LineSize = (ushort)line;
            caches[count].Size = size;
            return count + 1;
        }

        // AMD's encoded L2/L3 associativity.
        private static byte Ways(uint code)
        {
            switch (code)
            {
                case 1: return 1;
                case 2: return 2;
                case 4: return 4;
                case 6: return 8;
                case 8: return 16;
                case 0xA: return 32;
                case 0xB: return 48;
                case 0xC: return 64;
                case 0xD: return 96;
                case 0xE: return 128;
                case 0xF: return 255;   // fully associative
                default: return 0;
            }
        }

        // Once, in the kernel log: the gen0 budget follows from this line, and
        // a machine that reports no caches is worth knowing about.
        private static void Log(CacheInfo* caches, int count, string source)
        {
            DebugLog.Begin(LogLevel.Info);
            Console.Write("cpu caches (");
            Console.Write(source);
            Console.Write("):");
            if (count == 0)
                Console.Write(" none reported");
            for (int i = 0; i < count; i++)
            {
                Console.Write(" L");
                Console.WriteUInt(caches[i].Level);
                Console.Write(caches[i].Kind == CacheKind.Data ? "d " : caches[i].Kind == CacheKind.Instruction ? "i " : " ");
                Console.WriteUInt(caches[i].Size / 1024);
                Console.Write("K");
            }
            DebugLog.EndLine();
        }
    }
}
