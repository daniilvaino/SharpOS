using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 2 — managed view of the kernel's `.pdata` section.
    //
    // Locates the PE image in memory (UEFI maps the kernel binary at its
    // PE ImageBase virtual address) by scanning down from any pointer
    // known to live inside the image until the 'MZ' DOS signature is
    // found. From there we follow PE -> Optional Header -> DataDirectory
    // entry 3 (IMAGE_DIRECTORY_ENTRY_EXCEPTION) to get RVA + size of the
    // RUNTIME_FUNCTION array.
    //
    // Each RUNTIME_FUNCTION record on AMD64 is 12 bytes:
    //   uint32 BeginAddress       — RVA of method body start
    //   uint32 EndAddress         — RVA of method body end (exclusive)
    //   uint32 UnwindInfoAddress  — RVA of the UNWIND_INFO blob
    //
    // The records are sorted by BeginAddress, which lets binary search
    // resolve IP -> RUNTIME_FUNCTION in O(log N). Funclet records (catch /
    // finally / filter bodies emitted by ILC as separate functions)
    // appear immediately after their parent ROOT record, sorted by IP —
    // CoffMethodLookup walks backwards through the table to find the
    // parent.
    //
    // Reference:
    //   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/windows/CoffNativeCodeManager.cpp:983-994
    //     for the IMAGE_DIRECTORY_ENTRY_EXCEPTION lookup pattern.
    internal static unsafe class CoffRuntimeFunctionTable
    {
        // Maximum bytes we'll scan downward from anchor looking for the MZ
        // signature. Our kernel image is a few MB; 16 MB is comfortable
        // margin without scanning into unmapped memory.
        private const long ScanRadiusBytes = 16L * 1024 * 1024;
        private const int PageSize = 0x1000;

        // PE constants.
        private const ushort DosSignature = 0x5A4D;          // 'MZ'
        private const uint PeSignature = 0x00004550;          // 'PE\0\0'
        private const ushort PeMagicPe32Plus = 0x020B;
        private const int IMAGE_DIRECTORY_ENTRY_EXCEPTION = 3;

        private static byte* s_imageBase;
        private static RuntimeFunction* s_records;
        private static int s_recordCount;
        private static bool s_initialized;

        public static bool IsInitialized => s_initialized;
        public static byte* ImageBase => s_imageBase;
        public static RuntimeFunction* Records => s_records;
        public static int Count => s_recordCount;

        public static RuntimeFunction* GetRecord(int index)
        {
            if (!s_initialized || index < 0 || index >= s_recordCount)
                return null;
            return &s_records[index];
        }

        // Locates the PE image, parses its header, and caches the
        // RUNTIME_FUNCTION array. `anchorInImage` must point into our
        // kernel binary (e.g. an EEType from .rdata, or any kernel code
        // address). Returns true on success. Idempotent.
        public static bool TryInitialize(byte* anchorInImage)
        {
            if (s_initialized) return true;
            if (anchorInImage == null) return false;

            byte* dosHeader = ScanForDosHeader(anchorInImage);
            if (dosHeader == null) return false;

            // e_lfanew at offset 0x3C points to the PE header.
            int peOffset = *(int*)(dosHeader + 0x3C);
            if (peOffset <= 0 || peOffset > 0x10000) return false;

            byte* peHeader = dosHeader + peOffset;
            if (*(uint*)peHeader != PeSignature) return false;

            // COFF File Header sits at peHeader + 4. Optional Header
            // immediately follows the COFF header (which is 20 bytes).
            byte* optHeader = peHeader + 4 + 20;
            ushort magic = *(ushort*)optHeader;
            if (magic != PeMagicPe32Plus) return false;     // we are PE32+

            // DataDirectory begins at optHeader + 112 for PE32+.
            // Each entry is 8 bytes (RVA + Size).
            byte* dataDir = optHeader + 112;
            uint pdataRva = *(uint*)(dataDir + IMAGE_DIRECTORY_ENTRY_EXCEPTION * 8);
            uint pdataSize = *(uint*)(dataDir + IMAGE_DIRECTORY_ENTRY_EXCEPTION * 8 + 4);

            if (pdataRva == 0 || pdataSize == 0) return false;
            if ((pdataSize % 12) != 0) return false;        // must be N records of 12 bytes

            s_imageBase = dosHeader;
            s_records = (RuntimeFunction*)(dosHeader + pdataRva);
            s_recordCount = (int)(pdataSize / 12);
            s_initialized = true;

            Log.Begin(LogLevel.Info);
            Console.Write("coff-pdata: imageBase=0x");
            Console.WriteHexRaw((ulong)s_imageBase, 16);
            Console.Write(" pdataRva=0x");
            Console.WriteHexRaw(pdataRva, 8);
            Console.Write(" records=");
            Console.WriteUIntRaw((uint)s_recordCount);
            Log.EndLine();

            return true;
        }

        // Scan downward from anchor in 4KB strides for the 'MZ' DOS
        // signature. Validates by checking PE signature at e_lfanew so a
        // random byte pair that happens to be 0x4D 0x5A doesn't fool us.
        private static byte* ScanForDosHeader(byte* anchor)
        {
            // Align anchor down to page boundary. The PE image is loaded
            // at a page-aligned VA, so the DOS header sits at offset 0
            // of some 4KB-aligned address below anchor.
            nint addr = (nint)anchor & ~(nint)(PageSize - 1);

            for (long offset = 0; offset <= ScanRadiusBytes; offset += PageSize)
            {
                byte* candidate = (byte*)(addr - offset);

                if (*(ushort*)candidate != DosSignature)
                    continue;

                int peOffset = *(int*)(candidate + 0x3C);
                if (peOffset <= 0 || peOffset > 0x10000)
                    continue;

                if (*(uint*)(candidate + peOffset) == PeSignature)
                    return candidate;
            }
            return null;
        }
    }

    // 12-byte AMD64 RUNTIME_FUNCTION record. Sequential layout matches
    // the on-disk PE format exactly.
    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct RuntimeFunction
    {
        public uint BeginAddress;       // RVA of method body start
        public uint EndAddress;         // RVA of method body end (exclusive)
        public uint UnwindInfoAddress;  // RVA of UNWIND_INFO blob
    }
}
