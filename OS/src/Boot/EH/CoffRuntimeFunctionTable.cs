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

        // Where ILC's own code lives in the kernel image, as an RVA range.
        //
        // The image is NOT all managed. CoreCLR and the CRT are linked into it
        // statically and land in .text; ILC's output lands in .managed. Both
        // contribute .pdata records, and only ILC's carry the NativeAOT
        // trailer byte after UNWIND_INFO. Reading that byte for a native
        // record reads whatever happens to follow it - usually the next
        // UNWIND_INFO's version byte, which is 1, which decodes as "funclet".
        //
        // Measured on this image: 25747 records, of which 22768 are native and
        // 7540 of those (a third) read as funclets. Record 0 is one of them,
        // which is why the L5 probe answered 3 instead of 7 from the moment
        // CoreCLR was linked in, and why nobody connected the two.
        private static uint s_managedStart, s_managedEnd;

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

            // Whether the image's pages are mapped right now.
            //
            // A parent process is unmapped while a nested one runs — its image
            // and this one cannot both live at the same address — but its
            // registration used to stay, so every stack walk read .pdata that
            // was no longer there. The entry is kept rather than removed
            // because it comes back unchanged when the parent resumes.
            public fixed byte Mapped[MaxExtraImages];

            // The image's ILC code range, the same question asked of the
            // kernel. An app is very nearly all managed - its .text holds a
            // few hundred bytes of startup stub - but the property belongs to
            // the image, not to the tier, so it is asked the same way.
            public fixed uint ManagedStart[MaxExtraImages];
            public fixed uint ManagedEnd[MaxExtraImages];
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
            s_extra.Mapped[i] = 1;

            // Parsed from the image's own headers. A failure here is not fatal
            // and must not be: the fallback is "the whole image is managed",
            // which is what every caller assumed before this existed, so an
            // unparseable image behaves exactly as it used to.
            if (!TryFindManagedRange(imageBase, out uint managedStart, out uint managedEnd))
            {
                managedStart = 0;
                managedEnd = 0xFFFFFFFFu;
            }
            s_extra.ManagedStart[i] = managedStart;
            s_extra.ManagedEnd[i] = managedEnd;

            s_extraCount++;
            return i;
        }

        /// <summary>
        /// Removes a registered image by base. No-op if not found.
        /// </summary>
        /// <remarks>
        /// Searches from the END, and that is the whole correctness of it:
        /// every app is linked at the SAME base, so a base does not identify an
        /// image — a parent and the child it launched are both registered at
        /// 0x100000000. Nesting is strictly stacked, so the last entry with a
        /// given base is the innermost image, which is the one being torn down.
        ///
        /// Searching from the front removed the PARENT's entry when a child
        /// exited, leaving the child's registration behind. The dead entry then
        /// pointed at unmapped memory, and the next stack walk — a GC in the
        /// parent, moments later — faulted inside SearchImage with nothing to
        /// connect it to the launch that had just finished.
        /// </remarks>
        public static void UnregisterImage(byte* imageBase)
        {
            for (int i = s_extraCount - 1; i >= 0; i--)
            {
                if (s_extra.Bases[i] != (ulong)imageBase) continue;
                for (int j = i; j < s_extraCount - 1; j++)
                {
                    s_extra.Bases[j] = s_extra.Bases[j + 1];
                    s_extra.Records[j] = s_extra.Records[j + 1];
                    s_extra.Counts[j] = s_extra.Counts[j + 1];
                    s_extra.Mapped[j] = s_extra.Mapped[j + 1];
                }
                s_extraCount--;
                return;
            }
        }

        /// <summary>
        /// Marks a registered image as mapped or not. An unmapped one is
        /// skipped by every lookup.
        /// </summary>
        /// <remarks>
        /// Called around a nested launch, where the parent's pages go away and
        /// come back. Searching it in between is not a stale answer — it is a
        /// read of memory that is not there, and it faults inside the GC's
        /// stack walk, a long way from the launch that caused it.
        /// </remarks>
        public static void SetImageMapped(byte* imageBase, bool mapped)
        {
            // From the end, for the same reason as UnregisterImage: the base
            // alone does not say which image. At suspension the innermost entry
            // at that base IS the process being suspended.
            for (int i = s_extraCount - 1; i >= 0; i--)
            {
                if (s_extra.Bases[i] != (ulong)imageBase) continue;
                s_extra.Mapped[i] = mapped ? (byte)1 : (byte)0;
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
                // Skip an image whose pages are gone: reading its .pdata would
                // fault, and the PC being looked for cannot be in it anyway.
                if (s_extra.Mapped[i] == 0) continue;

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

        // Was this record emitted by ILC?
        //
        // Only an ILC record carries the NativeAOT trailer byte after its
        // UNWIND_INFO. For anything else - CoreCLR, the CRT, the startup stub
        // - the byte at that offset belongs to whatever the linker put next,
        // and reading it as function-kind flags is reading noise as metadata.
        // Callers ask this before trusting the trailer.
        public static bool RecordIsManaged(RuntimeFunction* rf)
        {
            if (rf == null) return false;

            if (s_records != null && rf >= s_records && rf < s_records + s_recordCount)
                return rf->BeginAddress >= s_managedStart && rf->BeginAddress < s_managedEnd;

            for (int i = 0; i < s_extraCount; i++)
            {
                RuntimeFunction* r = (RuntimeFunction*)s_extra.Records[i];
                int c = s_extra.Counts[i];
                if (rf >= r && rf < r + c)
                    return rf->BeginAddress >= s_extra.ManagedStart[i]
                        && rf->BeginAddress < s_extra.ManagedEnd[i];
            }

            // An unrecognised record is treated as the kernel's, the same
            // fallback ImageBaseForRecord makes, for the same reason.
            return rf->BeginAddress >= s_managedStart && rf->BeginAddress < s_managedEnd;
        }

        /// <summary>The kernel image's ILC code range, for diagnostics.</summary>
        public static uint ManagedRvaStart => s_managedStart;

        /// <summary>End of the kernel image's ILC code range, exclusive.</summary>
        public static uint ManagedRvaEnd => s_managedEnd;

        // Find the `.managed` section in a PE image and return its RVA range.
        // False when the headers cannot be read or the section is absent.
        private static bool TryFindManagedRange(byte* dosHeader, out uint start, out uint end)
        {
            start = 0;
            end = 0;
            if (dosHeader == null) return false;
            if (*(ushort*)dosHeader != DosSignature) return false;

            int peOffset = *(int*)(dosHeader + 0x3C);
            if (peOffset <= 0 || peOffset > 0x10000) return false;

            byte* peHeader = dosHeader + peOffset;
            if (*(uint*)peHeader != PeSignature) return false;

            ushort sectionCount = *(ushort*)(peHeader + 4 + 2);
            ushort optionalSize = *(ushort*)(peHeader + 4 + 16);
            if (sectionCount == 0 || sectionCount > 96) return false;

            byte* section = peHeader + 4 + 20 + optionalSize;
            for (int i = 0; i < sectionCount; i++, section += 40)
            {
                // Exactly eight characters, so the name fills the field and
                // there is no terminator to skip.
                if (section[0] != 0x2E) continue;        // '.'
                if (section[1] != 0x6D) continue;        // 'm'
                if (section[2] != 0x61) continue;        // 'a'
                if (section[3] != 0x6E) continue;        // 'n'
                if (section[4] != 0x61) continue;        // 'a'
                if (section[5] != 0x67) continue;        // 'g'
                if (section[6] != 0x65) continue;        // 'e'
                if (section[7] != 0x64) continue;        // 'd'

                uint virtualSize = *(uint*)(section + 8);
                uint virtualAddress = *(uint*)(section + 12);
                if (virtualAddress == 0 || virtualSize == 0) return false;

                start = virtualAddress;
                end = virtualAddress + virtualSize;
                return true;
            }
            return false;
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

            if (!TryFindManagedRange(dosHeader, out s_managedStart, out s_managedEnd))
            {
                // No .managed section: a pure ILC image, where every record is
                // ILC's. That is what everything assumed before this existed.
                s_managedStart = 0;
                s_managedEnd = 0xFFFFFFFFu;
            }

            s_initialized = true;

            Log.Begin(LogLevel.Info);
            Console.Write("coff-pdata: imageBase=0x");
            Console.WriteHexRaw((ulong)s_imageBase, 16);
            Console.Write(" pdataRva=0x");
            Console.WriteHexRaw(pdataRva, 8);
            Console.Write(" records=");
            Console.WriteUIntRaw((uint)s_recordCount);
            // Without this the split between ILC and native records is
            // invisible, and that split is the difference between a trailer
            // byte that means something and one that does not.
            Console.Write(" managed=0x");
            Console.WriteHexRaw(s_managedStart, 8);
            Console.Write("..0x");
            Console.WriteHexRaw(s_managedEnd, 8);
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
