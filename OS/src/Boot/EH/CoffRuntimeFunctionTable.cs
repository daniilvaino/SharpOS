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

        // ---- Multi-image registry (step140) --------------------------------
        //
        // The primary image (index 0) is the kernel — s_imageBase/s_records/
        // s_recordCount above, kept EXACTLY as-is so every kernel-only consumer
        // (SEH engine, GC precise walk, diagnostics) that reads ImageBase/Count/
        // GetRecord is untouched. This registry holds ADDITIONAL images (loaded
        // PE apps at 0x400000) whose `.pdata` PeLoader registers after mapping,
        // so the managed EH walk (CoffMethodLookup/CoffEhDecoder/StackFrameIterator)
        // can resolve app frames. Apps nest LIFO and unregister on exit.
        //
        // Storage is a fixed-capacity value struct (no static reference field →
        // no ClassConstructorRunner trap); pointers/ints only, zero-initialized.
        private const int MaxExtraImages = 4;

        private unsafe struct ExtraImageTable
        {
            public fixed ulong Bases[MaxExtraImages];
            public fixed ulong Records[MaxExtraImages];   // RuntimeFunction*
            public fixed int Counts[MaxExtraImages];
        }

        private static ExtraImageTable s_extra;
        private static int s_extraCount;

        // Register an additional image's .pdata. Returns the slot, or -1 if the
        // table is full. Records must point at `count` RUNTIME_FUNCTION entries
        // sorted by BeginAddress, addressable at imageBase + rva.
        public static int RegisterImage(byte* imageBase, RuntimeFunction* records, int count)
        {
            if (imageBase == null || records == null || count <= 0) return -1;
            if (s_extraCount >= MaxExtraImages) return -1;
            int i = s_extraCount;
            s_extra.Bases[i] = (ulong)imageBase;
            s_extra.Records[i] = (ulong)records;
            s_extra.Counts[i] = count;
            s_extraCount++;
            return i;
        }

        // Remove a registered image by base (LIFO-friendly compaction). No-op if
        // not found. Called when an app image is torn down.
        public static void UnregisterImage(byte* imageBase)
        {
            for (int i = 0; i < s_extraCount; i++)
            {
                if (s_extra.Bases[i] != (ulong)imageBase) continue;
                for (int j = i; j < s_extraCount - 1; j++)
                {
                    s_extra.Bases[j] = s_extra.Bases[j + 1];
                    s_extra.Records[j] = s_extra.Records[j + 1];
                    s_extra.Counts[j] = s_extra.Counts[j + 1];
                }
                s_extraCount--;
                return;
            }
        }

        // Resolve which image owns `ip`: kernel (image 0) first, then extras.
        // Yields the owning image's base/records/count plus the record index
        // within that image. This is the image-aware entry the managed EH walk
        // uses instead of assuming the kernel base.
        public static bool TryResolvePc(
            byte* ip,
            out byte* imageBase, out RuntimeFunction* records,
            out int count, out int localIndex)
        {
            imageBase = null; records = null; count = 0; localIndex = -1;
            if (!s_initialized) return false;

            int idx = SearchImage(s_imageBase, s_records, s_recordCount, ip);
            if (idx >= 0)
            {
                imageBase = s_imageBase; records = s_records;
                count = s_recordCount; localIndex = idx;
                return true;
            }

            for (int i = 0; i < s_extraCount; i++)
            {
                byte* b = (byte*)s_extra.Bases[i];
                RuntimeFunction* r = (RuntimeFunction*)s_extra.Records[i];
                int c = s_extra.Counts[i];
                idx = SearchImage(b, r, c, ip);
                if (idx >= 0)
                {
                    imageBase = b; records = r; count = c; localIndex = idx;
                    return true;
                }
            }
            return false;
        }

        // Given a RUNTIME_FUNCTION pointer, return the base of the image whose
        // records array contains it. Used to turn rf->UnwindInfoAddress (an RVA)
        // into an absolute address. Falls back to the kernel base for back-compat
        // (a kernel rf, or an unknown pointer treated as kernel).
        public static byte* ImageBaseForRecord(RuntimeFunction* rf)
        {
            if (rf != null && s_records != null &&
                rf >= s_records && rf < s_records + s_recordCount)
                return s_imageBase;

            for (int i = 0; i < s_extraCount; i++)
            {
                RuntimeFunction* r = (RuntimeFunction*)s_extra.Records[i];
                int c = s_extra.Counts[i];
                if (rf >= r && rf < r + c) return (byte*)s_extra.Bases[i];
            }
            return s_imageBase;
        }

        // Binary search one image's sorted record array for the record covering
        // `ip`. Returns the local index, or -1 if `ip` is outside this image or
        // falls between records. Shared by TryResolvePc; identical algorithm to
        // CoffMethodLookup's kernel-only FindRecordIndex.
        private static int SearchImage(byte* imageBase, RuntimeFunction* records, int count, byte* ip)
        {
            if (imageBase == null || records == null || count <= 0) return -1;
            nint diff = (nint)ip - (nint)imageBase;
            if (diff < 0) return -1;
            ulong rva = (ulong)diff;
            if (rva > 0xFFFFFFFFUL) return -1;
            uint targetRva = (uint)rva;

            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                RuntimeFunction* rf = &records[mid];
                if (targetRva < rf->BeginAddress) hi = mid - 1;
                else if (targetRva >= rf->EndAddress) lo = mid + 1;
                else return mid;
            }
            return -1;
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
