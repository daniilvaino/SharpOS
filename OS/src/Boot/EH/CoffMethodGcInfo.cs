namespace OS.Boot.EH
{
    // Phase F prep / step 110 part 1 — locate per-method GcInfo blob.
    //
    // NativeAOT bakes precise GC info for every compiled method into the
    // trailing portion of its UNWIND_INFO blob in .xdata. Layout (matches
    // CoffNativeCodeManager.cpp GetCodeOffset / FindMethodInfo, AMD64):
    //
    //   RUNTIME_FUNCTION (in .pdata)
    //     BeginAddress       — method start RVA
    //     EndAddress         — method end RVA
    //     UnwindInfoAddress  — RVA into .xdata
    //         ↓ imageBase + UnwindInfoAddress
    //   UNWIND_INFO
    //     +0 verFlags        — version (3 bits) | flags (5 bits)
    //     +1 sizeOfProlog
    //     +2 countOfCodes
    //     +3 frameReg+frameRegOffset
    //     +4 unwindCode[countOfCodes]   — 2 bytes each
    //     [align to 4 bytes]
    //     [+4 bytes personality routine] — only if EHANDLER|UHANDLER set
    //         ↓ p = unwindInfo + unwindSize
    //   unwindBlockFlags (1 byte)
    //     bit 0..1: UBF_FUNC_KIND (0=ROOT, 1=HANDLER, 2=FILTER)
    //     bit 2:    UBF_FUNC_HAS_EHINFO         (0x04)
    //     bit 4:    UBF_FUNC_HAS_ASSOCIATED_DATA (0x10)
    //         ↓ p += 1
    //   [+4 bytes associatedData RVA] — only if UBF_FUNC_HAS_ASSOCIATED_DATA
    //   [+4 bytes ehInfoRva]          — only if UBF_FUNC_HAS_EHINFO
    //         ↓ p
    //   gcInfo blob ← варинт-кодированный, decoder reads it
    //
    // This helper resolves IP → (methodStart, gcInfo*). The actual decoder
    // (varint slot enumeration at a given codeOffset = IP - methodStart) is
    // the next part of step 110; here we only build the locator on top of
    // existing .pdata infrastructure (CoffRuntimeFunctionTable +
    // CoffMethodLookup) so callers can experiment with the raw blob.
    internal static unsafe class CoffMethodGcInfo
    {
        public struct Result
        {
            public byte* MethodStart;     // imageBase + BeginAddress (root)
            public byte* MethodEnd;       // imageBase + EndAddress (root)
            public byte* GcInfo;          // start of the per-method GcInfo blob
            public uint  CodeOffset;      // IP - MethodStart, for the decoder
            public RuntimeFunction* RuntimeFunction;   // root (non-funclet) entry — for VirtualUnwind
        }

        // PE UNWIND_INFO flags (upper 5 bits of byte 0).
        private const byte UNW_FLAG_EHANDLER = 0x01;
        private const byte UNW_FLAG_UHANDLER = 0x02;

        // Resolve `ip` → method GcInfo. Returns false when ip is outside
        // the kernel image's managed code range, or .pdata isn't mounted.
        public static bool TryResolve(byte* ip, out Result result)
        {
            result = default;

            if (!CoffMethodLookup.TryFindMethod(ip, out CoffMethodLookup.MethodInfo info))
                return false;

            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            byte* methodStart = imageBase + info.RootRuntimeFunction->BeginAddress;
            byte* methodEnd   = imageBase + info.RootRuntimeFunction->EndAddress;
            byte* unwindInfo  = imageBase + info.RootRuntimeFunction->UnwindInfoAddress;

            // UNWIND_INFO header walk — mirrors CoffEhDecoder.EhEnumInit.
            byte verFlags     = unwindInfo[0];
            byte flags        = (byte)((verFlags >> 3) & 0x1F);
            byte countOfCodes = unwindInfo[2];

            int unwindSize = 4 + 2 * countOfCodes;
            if ((flags & (UNW_FLAG_EHANDLER | UNW_FLAG_UHANDLER)) != 0)
            {
                unwindSize = (unwindSize + 3) & ~3;
                unwindSize += 4;   // personality routine RVA
            }

            byte* p = unwindInfo + unwindSize;
            byte unwindBlockFlags = *p++;

            if ((unwindBlockFlags & CoffMethodLookup.UBF_FUNC_HAS_ASSOCIATED_DATA) != 0)
                p += 4;
            if ((unwindBlockFlags & CoffMethodLookup.UBF_FUNC_HAS_EHINFO) != 0)
                p += 4;

            // p now points at the start of the GcInfo blob. The blob itself
            // is varint-encoded; the decoder lives in step 110 part 2.
            result.MethodStart     = methodStart;
            result.MethodEnd       = methodEnd;
            result.GcInfo          = p;
            result.CodeOffset      = (uint)(ip - methodStart);
            result.RuntimeFunction = info.RootRuntimeFunction;
            return true;
        }
    }
}
