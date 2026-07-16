namespace OS.Boot.EH
{
    // Phase 1 step 3 — EH info decoder.
    //
    // Given an IP inside a method, EhEnumInit walks .pdata -> UNWIND_INFO ->
    // NativeAOT trailer to locate the per-method EH clause table, then
    // EhEnumNext yields one clause at a time (typed catch / fault / filter).
    //
    // Reference (stock): gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime/windows/CoffNativeCodeManager.cpp:841-926
    //
    // Per-clause encoding (from stock source):
    //
    //   varuint nClauses
    //   for each clause:
    //       varuint tryStartOffset
    //       varuint ((tryEndOffset - tryStartOffset) << 2 | clauseKind)
    //       if (kind == TYPED  (0)): varuint handlerOffset; uint32 typeRVA
    //       if (kind == FAULT  (1)): varuint handlerOffset
    //       if (kind == FILTER (2)): varuint handlerOffset; varuint filterOffset
    //
    // Note on FINALLY: NativeAOT does not encode finally as a separate
    // clause kind. Finally blocks emit as kind=FAULT in the EH info table —
    // the dispatcher invokes them on every unwind through their try region,
    // which matches finally semantics during exception propagation. Normal
    // exit paths invoke finally inline (no EH dispatch involved).
    //
    // Step 3 only enumerates; step 5 (DispatchEx) acts on the clauses.
    internal static unsafe class CoffEhDecoder
    {
        // Bits of UNWIND_INFO header byte 0 (Flags field, upper 5 bits).
        private const byte UNW_FLAG_EHANDLER = 0x01;
        private const byte UNW_FLAG_UHANDLER = 0x02;

        public enum ClauseKind
        {
            Typed = 0,
            Fault = 1,    // also used for IL `finally` blocks
            Filter = 2,
            Unused = 3,
        }

        public struct EHEnum
        {
            // Method body start (absolute pointer, used to translate
            // handler/filter offsets back into IPs).
            public byte* MethodStartAddress;

            // Cursor into the EH clause blob. Advanced by VarInt.ReadUnsigned
            // calls in EhEnumNext.
            public byte* EHInfo;

            // Base of the image that owns this method (step140 multi-image):
            // kernel or a loaded app. Used to turn a clause's type RVA into an
            // absolute MethodTable pointer in EhEnumNext.
            public byte* ImageBase;

            public uint TotalClauses;
            public uint CurrentClauseIndex;
        }

        public struct RhEHClause
        {
            public ClauseKind Kind;
            public uint TryStartOffset;
            public uint TryEndOffset;
            public byte* HandlerAddress;       // for Typed, Fault, Filter
            public byte* FilterAddress;        // for Filter only
            public byte* TargetTypeRaw;        // for Typed only — pointer to MT
        }

        // Initialise enumeration over the EH clauses for the method
        // containing `ip`. Resolves IP through CoffMethodLookup (handles
        // funclet -> ROOT walk), then reads UNWIND_INFO + NativeAOT
        // trailer to locate the EH info blob.
        //
        // Returns false if:
        //   - IP is outside the image
        //   - the resolved method has no UBF_FUNC_HAS_EHINFO trailer flag
        //     (no try/catch/finally in this method)
        public static bool EhEnumInit(byte* ip, out EHEnum state, out byte* methodStartAddress)
        {
            state = default;
            methodStartAddress = null;

            if (!CoffMethodLookup.TryFindMethod(ip, out CoffMethodLookup.MethodInfo info))
                return false;

            byte* imageBase = info.ImageBase;
            byte* unwindInfo = imageBase + info.RootRuntimeFunction->UnwindInfoAddress;

            // Standard UNWIND_INFO header.
            byte verFlags = unwindInfo[0];
            byte flags = (byte)((verFlags >> 3) & 0x1F);
            byte countOfCodes = unwindInfo[2];

            int unwindSize = 4 + 2 * countOfCodes;
            if ((flags & (UNW_FLAG_EHANDLER | UNW_FLAG_UHANDLER)) != 0)
            {
                unwindSize = (unwindSize + 3) & ~3;
                unwindSize += 4;
            }

            byte* p = unwindInfo + unwindSize;
            byte unwindBlockFlags = *p;
            p++;

            if ((unwindBlockFlags & CoffMethodLookup.UBF_FUNC_HAS_ASSOCIATED_DATA) != 0)
                p += 4;

            if ((unwindBlockFlags & CoffMethodLookup.UBF_FUNC_HAS_EHINFO) == 0)
                return false;

            // ehInfoRVA — 4-byte little-endian RVA into image.
            int ehInfoRva = *(int*)p;

            byte* methodStart = imageBase + info.RootRuntimeFunction->BeginAddress;
            byte* ehInfo = imageBase + ehInfoRva;

            state.MethodStartAddress = methodStart;
            state.EHInfo = ehInfo;
            state.ImageBase = imageBase;
            state.CurrentClauseIndex = 0;
            state.TotalClauses = VarInt.ReadUnsigned(ref state.EHInfo);

            methodStartAddress = methodStart;
            return true;
        }

        // Phase 1 step 11+ — funclet-aware codeOffset resolution.
        //
        // When iter's ControlPC lives inside a funclet body (catch/finally/
        // filter handler funclet), the literal `ControlPC - methodStart`
        // codeOffset points to the funclet body region, which is past all
        // parent's TRY ranges. Clause matching fails — outer catches don't
        // see the throw.
        //
        // Stock NativeAOT solves this с funclet-aware StackFrameIterator:
        // when iter walks к funclet, it transforms ControlPC к "logical
        // position" inside parent's protected TRY region. We don't have
        // funclet-aware SFI, but we can do the equivalent transformation
        // в clause matching: for each EH info enum, find the clause whose
        // handler starts at the funclet's begin address; use that clause's
        // TryStartOffset as synthetic codeOffset.
        //
        // Returns false если methodInfo represents a ROOT (no transformation
        // needed) или associated clause не найден (fall back to literal).
        public static bool TryFindFuncletProtectedOffset(
            byte* ip,
            out uint synthOffset,
            out byte* methodStart,
            out uint funcletClauseIdx)
        {
            synthOffset = 0;
            methodStart = null;
            funcletClauseIdx = 0xFFFFFFFFu;

            if (!CoffMethodLookup.TryFindMethod(ip, out CoffMethodLookup.MethodInfo info))
                return false;

            int kind = info.CurrentBlockFlags & CoffMethodLookup.UBF_FUNC_KIND_MASK;
            if (kind == CoffMethodLookup.UBF_FUNC_KIND_ROOT)
                return false;   // not a funclet

            byte* imageBase = info.ImageBase;
            byte* root = imageBase + info.RootRuntimeFunction->BeginAddress;
            methodStart = root;

            // Walk EH info, find clause whose HandlerAddress's RVA matches funclet.
            if (!EhEnumInit(root, out EHEnum state, out _))
                return false;

            byte* funcletAddr = imageBase + info.CurrentRuntimeFunction->BeginAddress;

            uint idx = 0;
            while (EhEnumNext(ref state, out RhEHClause clause))
            {
                if (clause.HandlerAddress == funcletAddr)
                {
                    synthOffset = clause.TryStartOffset;
                    funcletClauseIdx = idx;
                    return true;
                }
                idx++;
            }
            return false;
        }

        // Pulls the next clause out of the cursor. Returns false when no
        // more clauses remain.
        public static bool EhEnumNext(ref EHEnum state, out RhEHClause clause)
        {
            clause = default;

            if (state.CurrentClauseIndex >= state.TotalClauses)
                return false;
            state.CurrentClauseIndex++;

            byte* p = state.EHInfo;

            uint tryStart = VarInt.ReadUnsigned(ref p);
            uint tryEndDeltaAndKind = VarInt.ReadUnsigned(ref p);

            ClauseKind kind = (ClauseKind)(tryEndDeltaAndKind & 0x3);
            uint tryEnd = tryStart + (tryEndDeltaAndKind >> 2);

            clause.Kind = kind;
            clause.TryStartOffset = tryStart;
            clause.TryEndOffset = tryEnd;

            switch (kind)
            {
                case ClauseKind.Typed:
                    clause.HandlerAddress = state.MethodStartAddress
                                          + VarInt.ReadUnsigned(ref p);
                    // typeRVA — unaligned 4-byte LE int directly from blob.
                    uint typeRva = *(uint*)p;
                    p += 4;
                    clause.TargetTypeRaw = state.ImageBase + typeRva;
                    break;

                case ClauseKind.Fault:
                    clause.HandlerAddress = state.MethodStartAddress
                                          + VarInt.ReadUnsigned(ref p);
                    break;

                case ClauseKind.Filter:
                    clause.HandlerAddress = state.MethodStartAddress
                                          + VarInt.ReadUnsigned(ref p);
                    clause.FilterAddress = state.MethodStartAddress
                                         + VarInt.ReadUnsigned(ref p);
                    break;

                default:
                    // ClauseKind.Unused — should not appear in real EH info.
                    state.EHInfo = p;
                    return false;
            }

            state.EHInfo = p;
            return true;
        }
    }
}
