namespace OS.Boot.EH
{
    // Phase 1 step 2 — IP -> method resolution + funclet-to-ROOT walk.
    //
    // Given any instruction pointer that lives inside our kernel image,
    // CoffMethodLookup answers two questions:
    //
    //   1. Which RUNTIME_FUNCTION does this IP belong to? (binary search
    //      through the sorted .pdata array)
    //   2. If that record is a catch / finally / filter funclet, which
    //      ROOT record is its parent? (linear walk backwards through
    //      .pdata until UBF_FUNC_KIND_ROOT is found)
    //
    // ILC emits funclet RUNTIME_FUNCTION records immediately after their
    // parent ROOT record, sorted by IP. Stock NativeAOT walks this way:
    //
    //   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/windows/CoffNativeCodeManager.cpp:271-283
    //
    // The walk reads the NativeAOT trailer byte that lives immediately
    // after the standard UNWIND_INFO blob. Trailer-byte size formula
    // (sage-2 source-of-truth):
    //
    //   unwindSize = offsetof(UNWIND_INFO, UnwindCode) + 2 * CountOfUnwindCodes
    //              = 4 + 2 * N
    //   if (UNWIND_INFO.Flags has EHANDLER or UHANDLER):
    //       unwindSize = ALIGN_UP(unwindSize, 4) + 4   // for personality RVA
    //   trailerByte = unwindBlob[unwindSize]
    //
    // Empirically all 698 records in our current kernel binary have
    // Unwind flags = None, so the EHANDLER/UHANDLER path never fires;
    // we still implement it correctly for forward compatibility.
    internal static unsafe class CoffMethodLookup
    {
        // Bits of unwindBlockFlags trailer byte.
        public const byte UBF_FUNC_KIND_MASK = 0x03;
        public const byte UBF_FUNC_KIND_ROOT = 0x00;
        public const byte UBF_FUNC_KIND_HANDLER = 0x01;
        public const byte UBF_FUNC_KIND_FILTER = 0x02;
        public const byte UBF_FUNC_HAS_EHINFO = 0x04;
        public const byte UBF_FUNC_REVERSE_PINVOKE = 0x08;
        public const byte UBF_FUNC_HAS_ASSOCIATED_DATA = 0x10;

        // UNWIND_INFO Flags bits (in the upper 5 bits of byte 0).
        private const byte UNW_FLAG_EHANDLER = 0x01;
        private const byte UNW_FLAG_UHANDLER = 0x02;

        // Resolved method info — both the immediate record (which may be
        // a funclet) and the ROOT parent (which carries the EH clauses).
        public struct MethodInfo
        {
            public int CurrentIndex;
            public int RootIndex;
            public RuntimeFunction* CurrentRuntimeFunction;
            public RuntimeFunction* RootRuntimeFunction;
            public byte CurrentBlockFlags;
            public byte RootBlockFlags;
        }

        // Binary search the .pdata array for the record covering `ip`.
        // Returns -1 if `ip` falls outside the image's code range or
        // between records.
        public static int FindRecordIndex(byte* ip)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;
            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            int count = CoffRuntimeFunctionTable.Count;
            if (count <= 0) return -1;

            // Convert IP to RVA.
            nint diff = (nint)ip - (nint)imageBase;
            if (diff < 0) return -1;
            ulong rva = (ulong)diff;
            if (rva > 0xFFFFFFFFUL) return -1;     // RVAs are 32-bit
            uint targetRva = (uint)rva;

            int lo = 0;
            int hi = count - 1;
            while (lo <= hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                RuntimeFunction* rf = CoffRuntimeFunctionTable.GetRecord(mid);
                if (rf == null) return -1;

                if (targetRva < rf->BeginAddress)
                    hi = mid - 1;
                else if (targetRva >= rf->EndAddress)
                    lo = mid + 1;
                else
                    return mid;
            }
            return -1;
        }

        // Read the NativeAOT trailer byte (unwindBlockFlags) immediately
        // following the standard UNWIND_INFO blob.
        public static byte ReadUnwindBlockFlags(RuntimeFunction* rf)
        {
            if (rf == null) return 0;
            byte* unwindInfo = CoffRuntimeFunctionTable.ImageBase + rf->UnwindInfoAddress;

            // Standard UNWIND_INFO header (Win x64 unwind spec):
            //   byte 0: Version (low 3 bits) | Flags (high 5 bits)
            //   byte 1: SizeOfProlog
            //   byte 2: CountOfUnwindCodes
            //   byte 3: FrameRegister | FrameOffset
            //   bytes 4..: array of UNWIND_CODE (2 bytes each)

            byte verFlags = unwindInfo[0];
            byte flags = (byte)((verFlags >> 3) & 0x1F);
            byte countOfCodes = unwindInfo[2];

            int unwindSize = 4 + 2 * countOfCodes;

            // If a Windows-style language-specific exception handler is
            // present, ILC pads to dword and reserves 4 bytes for the
            // personality RVA before the NativeAOT trailer.
            if ((flags & (UNW_FLAG_EHANDLER | UNW_FLAG_UHANDLER)) != 0)
            {
                unwindSize = (unwindSize + 3) & ~3;
                unwindSize += 4;
            }

            return unwindInfo[unwindSize];
        }

        // Walk backwards through .pdata from `startIndex` until we find a
        // record whose unwindBlockFlags has UBF_FUNC_KIND_ROOT set. Used
        // to resolve a funclet record to its parent method body. Returns
        // -1 if no ROOT is found within reasonable distance (which would
        // indicate a corrupt .pdata).
        public static int WalkToRoot(int startIndex)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return -1;
            int count = CoffRuntimeFunctionTable.Count;
            if (startIndex < 0 || startIndex >= count) return -1;

            // Cap the walk so a corrupt section doesn't loop forever.
            // No real method has more than ~30 funclets (filter+catch+
            // finally per try block), so 64 is comfortable.
            const int MaxWalk = 64;

            int idx = startIndex;
            for (int step = 0; step < MaxWalk; step++)
            {
                if (idx < 0) return -1;
                RuntimeFunction* rf = CoffRuntimeFunctionTable.GetRecord(idx);
                if (rf == null) return -1;

                byte blockFlags = ReadUnwindBlockFlags(rf);
                int kind = blockFlags & UBF_FUNC_KIND_MASK;
                if (kind == UBF_FUNC_KIND_ROOT)
                    return idx;

                idx--;
            }
            return -1;
        }

        // Full method-info resolution: binary search for the IP, then
        // walk to ROOT if the immediate record is a funclet. Both
        // CurrentRuntimeFunction and RootRuntimeFunction are populated;
        // for a non-funclet IP they're identical.
        public static bool TryFindMethod(byte* ip, out MethodInfo info)
        {
            info = default;

            int currentIdx = FindRecordIndex(ip);
            if (currentIdx < 0) return false;

            RuntimeFunction* currentRf = CoffRuntimeFunctionTable.GetRecord(currentIdx);
            if (currentRf == null) return false;

            byte currentFlags = ReadUnwindBlockFlags(currentRf);

            int rootIdx = currentIdx;
            if ((currentFlags & UBF_FUNC_KIND_MASK) != UBF_FUNC_KIND_ROOT)
            {
                rootIdx = WalkToRoot(currentIdx);
                if (rootIdx < 0) return false;
            }

            RuntimeFunction* rootRf = CoffRuntimeFunctionTable.GetRecord(rootIdx);
            if (rootRf == null) return false;

            byte rootFlags = (rootIdx == currentIdx)
                ? currentFlags
                : ReadUnwindBlockFlags(rootRf);

            info.CurrentIndex = currentIdx;
            info.RootIndex = rootIdx;
            info.CurrentRuntimeFunction = currentRf;
            info.RootRuntimeFunction = rootRf;
            info.CurrentBlockFlags = currentFlags;
            info.RootBlockFlags = rootFlags;
            return true;
        }
    }
}
