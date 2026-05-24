using System;
using OS.PAL.SharpOSHost;

namespace OS.Boot.EH
{
    // Step 110 part 3 — precise GC info decoder.
    //
    // Decodes the varint-and-bit-packed GcInfo blob produced by
    // NativeAOT for each compiled method. Algorithm mirrors
    //   dotnet-runtime-sharpos/src/coreclr/vm/gcinfodecoder.cpp
    //     (GcInfoDecoder::GcInfoDecoder)
    //   dotnet-runtime-sharpos/src/coreclr/tools/aot/
    //     ILCompiler.Reflection.ReadyToRun/Amd64/GcInfo.cs
    //     (read-only C# decoder used by R2RDump)
    // Clean-room port to BitReader over byte*; AMD64-only.
    //
    // This first landing parses the header up through NumSafePoints
    // and NumInterruptibleRanges — enough to verify our locator,
    // reader, and ENCBASE constants are correct (CodeLength must
    // equal methodEnd - methodStart). Safepoint table, interruptible
    // ranges, slot table, and per-safepoint live-slot enumeration are
    // separate landings (Part 4+).
    //
    // Version is plumbed in from RTR header (NativeAotModuleInit reads
    // Major/Minor and maps via ReadyToRunVersionToGcInfoVersion).
    internal unsafe struct CoffGcInfoHeader
    {
        public int  Version;
        public bool SlimHeader;
        public uint ReturnKind;
        public int  CodeLength;

        public bool HasSecurityObject;
        public bool HasGsCookie;
        public bool HasPspSym;
        public bool HasGenericsInstContext;
        public bool HasStackBaseRegister;
        public bool HasSizeOfEditAndContinuePreservedArea;
        public bool HasReversePInvokeFrame;
        public bool WantsReportOnlyLeaf;

        // -1 if absent.
        public int SecurityObjectStackSlot;
        public int GsCookieStackSlot;
        public int PspSymStackSlot;
        public int GenericsInstContextStackSlot;
        public int ReversePInvokeFrameStackSlot;

        public uint ValidRangeStart;
        public uint ValidRangeEnd;
        public uint StackBaseRegister;                     // 0xFFFFFFFF if absent
        public uint SizeOfEditAndContinuePreservedArea;    // 0xFFFFFFFF if absent
        public uint SizeOfStackOutgoingAndScratchArea;
        public uint NumSafePoints;
        public uint NumInterruptibleRanges;

        // Cursor immediately after the header — where SafePointOffsets start.
        public int BitOffsetAfterHeader;
    }

    // Per-slot detail decoded from the slot table — enough info to
    // resolve the slot's address given a frame's saved CPU context.
    // For register slots: RegOrOffset = AMD64 reg index 0..15 (rax..r15).
    // For stack slots:    SpBase = 0..2 (caller-SP, current-SP, FP-base),
    //                     RegOrOffset = signed byte offset, already denormalized.
    internal struct CoffGcSlot
    {
        public byte Kind;          // 0 = register, 1 = stack
        public byte SpBase;        // for Kind==1: 0=caller-SP, 1=current-SP, 2=FP-rel
        public byte Flags;         // raw 2-bit flags (mostly UNTRACKED bit)
        public int  RegOrOffset;   // reg index, or signed byte offset
    }

    // GcSlotFlags from the stock encoder. Only UNTRACKED bit matters for
    // our enumerator — it marks "always-live for the entire function".
    internal static class CoffGcSlotFlags
    {
        public const byte Untracked = 0x4;   // GC_SLOT_UNTRACKED
        // 0x1 = GC_SLOT_BASE, 0x2 = GC_SLOT_INTERIOR — pointer kind hints
        // (interior pointer = points inside an object, not at MT header).
        // We can ignore for mark phase (kernel mark walker handles both
        // via its own range check), but caller can choose to honor them.
    }

    // GcStackSlotBase — the 2-bit per-stack-slot enum:
    //   0 = Caller-SP (above current SP by SizeOfStackOutgoingAndScratchArea)
    //   1 = Current-SP
    //   2 = FP-base (decoded via header's StackBaseRegister field)
    internal static class CoffGcStackSlotBase
    {
        public const byte Caller    = 0;
        public const byte CurrentSp = 1;
        public const byte FpBase    = 2;
    }

    // One interruptible range within a method — chunk of code where the
    // method is "fully interruptible" (GC can happen at any instruction).
    // Methods we decode have at least one such range covering most/all of
    // the method body for fully-interruptible code (NumSafePoints == 0).
    internal struct CoffInterruptibleRange
    {
        public uint StartOffset;
        public uint StopOffset;
    }

    // Result of decoding the slot table — how many GC-tracked slots
    // this method has, and how they're encoded. Slot details (register
    // numbers, stack offsets) live in slot-table memory we don't unpack
    // here yet; we just count them to know whether GetTransitions /
    // GetLiveSlotsAtSafepoints needs to run.
    internal struct CoffGcSlotTable
    {
        public uint NumRegisters;
        public uint NumStackSlots;
        public uint NumUntracked;
        public uint NumSlots;          // = NumRegisters + NumStackSlots + NumUntracked
        public uint NumTracked;        // = NumRegisters + NumStackSlots
        public int  BitOffsetAfterTable;
    }

    internal static unsafe class CoffGcInfoDecoder
    {
        private const int MinVersionWithReturnKind = 2;
        private const int MaxVersionWithReturnKind = 3;
        private const int MinVersionWithRevPInvokeFrame = 2;
        private const int MinVersionWithNormalizedCodeOffsets = 3;

        public static void DecodeHeader(byte* gcInfo, int version, out CoffGcInfoHeader hdr)
        {
            hdr = default;
            hdr.Version = version;
            hdr.SecurityObjectStackSlot = -1;
            hdr.GsCookieStackSlot = -1;
            hdr.PspSymStackSlot = -1;
            hdr.GenericsInstContextStackSlot = -1;
            hdr.ReversePInvokeFrameStackSlot = -1;
            hdr.StackBaseRegister = 0xFFFFFFFFu;
            hdr.SizeOfEditAndContinuePreservedArea = 0xFFFFFFFFu;

            BitReader r = new BitReader(gcInfo);

            // ---- Header flags ----
            hdr.SlimHeader = r.ReadBits(1) == 0;
            uint flagsRaw;
            if (hdr.SlimHeader)
            {
                // Slim: only the HasStackBaseRegister bit follows.
                flagsRaw = r.ReadBits(1) != 0 ? (uint)GcInfoHeaderFlags.HasStackBaseRegister : 0;
            }
            else
            {
                int numFlagBits = (version == 1)
                    ? GcInfoFlagsBitSize.V1
                    : GcInfoFlagsBitSize.CurrentVersion;
                flagsRaw = r.ReadBits(numFlagBits);
            }
            GcInfoHeaderFlags hf = (GcInfoHeaderFlags)flagsRaw;

            hdr.HasSecurityObject  = (hf & GcInfoHeaderFlags.HasSecurityObject) != 0;
            hdr.HasGsCookie        = (hf & GcInfoHeaderFlags.HasGsCookie) != 0;
            hdr.HasPspSym          = (hf & GcInfoHeaderFlags.HasPspSym) != 0;
            hdr.HasGenericsInstContext =
                (hf & GcInfoHeaderFlags.HasGenericsInstContextMask) !=
                GcInfoHeaderFlags.HasGenericsInstContextNone;
            hdr.HasStackBaseRegister = (hf & GcInfoHeaderFlags.HasStackBaseRegister) != 0;
            hdr.HasSizeOfEditAndContinuePreservedArea =
                (hf & GcInfoHeaderFlags.HasEditAndContinuePreservedSlots) != 0;
            if (version >= MinVersionWithRevPInvokeFrame)
                hdr.HasReversePInvokeFrame = (hf & GcInfoHeaderFlags.ReversePInvokeFrame) != 0;
            hdr.WantsReportOnlyLeaf = (hf & GcInfoHeaderFlags.WantsReportOnlyLeaf) != 0;

            // ---- ReturnKind ----
            if (version >= MinVersionWithReturnKind && version <= MaxVersionWithReturnKind)
            {
                int returnKindBits = hdr.SlimHeader
                    ? CoffGcInfoTypes.SizeOfReturnKindSlim
                    : CoffGcInfoTypes.SizeOfReturnKindFat;
                hdr.ReturnKind = r.ReadBits(returnKindBits);
            }

            // ---- CodeLength ----
            hdr.CodeLength = CoffGcInfoTypes.DenormalizeCodeLength(
                (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.CodeLengthEncBase));

            // ---- Valid range (GS cookie / security object / generics context) ----
            if (hdr.HasGsCookie)
            {
                uint normPrologSize = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NormPrologSizeEncBase) + 1;
                uint normEpilogSize = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NormEpilogSizeEncBase);
                hdr.ValidRangeStart = CoffGcInfoTypes.DenormalizeCodeOffset(normPrologSize);
                hdr.ValidRangeEnd   = (uint)hdr.CodeLength - CoffGcInfoTypes.DenormalizeCodeOffset(normEpilogSize);
            }
            else if (hdr.HasSecurityObject || hdr.HasGenericsInstContext)
            {
                uint normValidRangeStart = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NormPrologSizeEncBase) + 1;
                hdr.ValidRangeStart = CoffGcInfoTypes.DenormalizeCodeOffset(normValidRangeStart);
                hdr.ValidRangeEnd   = hdr.ValidRangeStart + 1;
            }

            // ---- Optional stack slot fields ----
            if (hdr.HasSecurityObject)
                hdr.SecurityObjectStackSlot = r.DecodeVarLengthSigned(CoffGcInfoTypes.SecurityObjectStackSlotEncBase);

            if (hdr.HasGsCookie)
                hdr.GsCookieStackSlot = r.DecodeVarLengthSigned(CoffGcInfoTypes.GsCookieStackSlotEncBase);

            if (hdr.HasPspSym)
                hdr.PspSymStackSlot = r.DecodeVarLengthSigned(CoffGcInfoTypes.PspSymStackSlotEncBase);

            if (hdr.HasGenericsInstContext)
                hdr.GenericsInstContextStackSlot = r.DecodeVarLengthSigned(CoffGcInfoTypes.GenericsInstContextStackSlotEncBase);

            // ---- Stack base register (FP), only in fat header ----
            // Stored as denormalized AMD64 reg index (RBP=5 for encoded 0,
            // RSP=4 for encoded 1). Resolver uses it directly with
            // ReadGpReg without further translation.
            if (hdr.HasStackBaseRegister && !hdr.SlimHeader)
            {
                uint raw = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.StackBaseRegisterEncBase);
                hdr.StackBaseRegister = CoffGcInfoTypes.DenormalizeStackBaseRegister(raw);
            }

            // ---- EnC preserved area size ----
            if (hdr.HasSizeOfEditAndContinuePreservedArea)
                hdr.SizeOfEditAndContinuePreservedArea =
                    r.DecodeVarLengthUnsigned(CoffGcInfoTypes.SizeOfEditAndContinuePreservedAreaEncBase);

            // ---- Reverse P/Invoke frame slot ----
            if (hdr.HasReversePInvokeFrame)
                hdr.ReversePInvokeFrameStackSlot =
                    r.DecodeVarLengthSigned(CoffGcInfoTypes.ReversePInvokeFrameEncBase);

            // ---- Outgoing/scratch stack area size (fat header only) ----
            if (!hdr.SlimHeader)
                hdr.SizeOfStackOutgoingAndScratchArea =
                    r.DecodeVarLengthUnsigned(CoffGcInfoTypes.SizeOfStackAreaEncBase);

            // ---- Counts ----
            hdr.NumSafePoints = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumSafePointsEncBase);

            if (!hdr.SlimHeader)
                hdr.NumInterruptibleRanges = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumInterruptibleRangesEncBase);

            hdr.BitOffsetAfterHeader = r.BitOffset;
        }

        // Map RTR header (Major, Minor) to GcInfo version. Mirrors
        // ILCompiler.Reflection.ReadyToRun.Amd64.GcInfo
        //   .ReadyToRunVersionToGcInfoVersion.
        public static int ReadyToRunVersionToGcInfoVersion(int major, int minor)
        {
            if (major == 1) return 1;
            if (major < 9 || (major == 9 && minor < 2)) return 2;
            if (major < 11) return 3;
            return 4;
        }

        // After the header, decode the safepoint offsets array
        // (NumSafePoints * ceilLog2(codeLength) raw bits) — skip in place,
        // we don't store details yet. Returns new bit offset.
        public static int SkipSafePointOffsets(byte* gcInfo, in CoffGcInfoHeader hdr, int bitOffset)
        {
            if (hdr.NumSafePoints == 0) return bitOffset;
            int numBitsPerOffset = CoffGcInfoTypes.CeilOfLog2(
                (int)CoffGcInfoTypes.NormalizeCodeOffset((uint)hdr.CodeLength));
            return bitOffset + numBitsPerOffset * (int)hdr.NumSafePoints;
        }

        // After safepoint offsets, decode interruptible-range deltas.
        // Each range is (delta1 + delta2) varints; we skip in place.
        public static int SkipInterruptibleRanges(byte* gcInfo, in CoffGcInfoHeader hdr, int bitOffset)
        {
            if (hdr.NumInterruptibleRanges == 0) return bitOffset;
            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(bitOffset);
            for (uint i = 0; i < hdr.NumInterruptibleRanges; i++)
            {
                r.DecodeVarLengthUnsigned(CoffGcInfoTypes.InterruptibleRangeDelta1EncBase);
                r.DecodeVarLengthUnsigned(CoffGcInfoTypes.InterruptibleRangeDelta2EncBase);
            }
            return r.BitOffset;
        }

        // Decode interruptible ranges into the caller-provided buffer.
        // Returns count written + new bit offset via out. Halts if
        // ranges.Length is too small (caller should size for expected
        // max — typically 1, very rarely > 4).
        public static int DecodeInterruptibleRanges(
            byte* gcInfo,
            in CoffGcInfoHeader hdr,
            int bitOffset,
            Span<CoffInterruptibleRange> rangesOut,
            out int newBitOffset)
        {
            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(bitOffset);
            uint normLastStop = 0;
            int count = (int)hdr.NumInterruptibleRanges;
            if (count > rangesOut.Length) Halt();
            for (int i = 0; i < count; i++)
            {
                uint normStartDelta = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.InterruptibleRangeDelta1EncBase);
                uint normStopDelta  = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.InterruptibleRangeDelta2EncBase) + 1;
                uint normStart = normLastStop + normStartDelta;
                uint normStop  = normStart + normStopDelta;
                rangesOut[i].StartOffset = CoffGcInfoTypes.DenormalizeCodeOffset(normStart);
                rangesOut[i].StopOffset  = CoffGcInfoTypes.DenormalizeCodeOffset(normStop);
                normLastStop = normStop;
            }
            newBitOffset = r.BitOffset;
            return count;
        }

        private static void Halt() { while (true) ; }

        // ---- Part 5: Transitions decoder ----
        //
        // For fully-interruptible methods (NumSafePoints == 0), GcInfo
        // encodes per-PC liveness of tracked slots via chunked transition
        // tables: interruptible code is virtually concatenated and chopped
        // into 64-instruction chunks; for each non-empty chunk, the encoder
        // emits couldBeLive bitmap + finalState bitmap + per-slot transition
        // offset lists.
        //
        // Algorithm to compute live state at a given PC:
        //   1. Find which range PC lives in, normalize PC into
        //      "interruptible coordinate" (sum of preceding range lengths +
        //      pc - currentRange.start).
        //   2. targetChunk = normalizedPc / 64; pcInChunk = normalizedPc % 64.
        //   3. For chunks 0..targetChunk-1: walk transitions, set
        //      liveState[slot] := finalState[slot in chunk].
        //   4. For targetChunk: walk transitions chronologically per slot,
        //      apply only those with offsetInChunk <= pcInChunk (parity
        //      flips).
        //
        // Returns true if PC is inside an interruptible range and liveOut
        // has been populated; false if PC is outside (caller must use a
        // different code path — e.g. safepoint table for partially-
        // interruptible methods).
        //
        // Untracked slots are NOT in liveOut — they're always live for the
        // whole function frame and need separate handling by the caller.
        //
        // liveOut must be at least slots.NumTracked in length. It gets
        // overwritten (no merging with previous content).
        public static bool EnumerateLiveSlotsAtPc(
            byte* gcInfo,
            int gcInfoVersion,
            uint pcCodeOffset,
            Span<bool> liveOut)
        {
            DecodeHeader(gcInfo, gcInfoVersion, out CoffGcInfoHeader hdr);

            int bitOffset = SkipSafePointOffsets(gcInfo, in hdr, hdr.BitOffsetAfterHeader);

            // Decode interruptible ranges (stackalloc up to 16 — methods
            // with > 16 ranges are extremely rare; can bump if needed).
            Span<CoffInterruptibleRange> ranges = stackalloc CoffInterruptibleRange[16];
            int numRanges = DecodeInterruptibleRanges(gcInfo, in hdr, bitOffset, ranges, out bitOffset);

            // Slot table is right after ranges.
            DecodeSlotTable(gcInfo, bitOffset, out CoffGcSlotTable slots);
            bitOffset = slots.BitOffsetAfterTable;

            // Clear caller output.
            for (int i = 0; i < (int)slots.NumTracked && i < liveOut.Length; i++)
                liveOut[i] = false;

            if (slots.NumTracked == 0)
                return true;   // no tracked slots, nothing to enumerate

            // Find which range PC lives in, and normalize.
            int targetRange = -1;
            uint normalizedPc = 0;
            uint cumLen = 0;
            for (int i = 0; i < numRanges; i++)
            {
                uint rangeLen = ranges[i].StopOffset - ranges[i].StartOffset;
                if (pcCodeOffset >= ranges[i].StartOffset && pcCodeOffset < ranges[i].StopOffset)
                {
                    targetRange = i;
                    normalizedPc = cumLen + (pcCodeOffset - ranges[i].StartOffset);
                    break;
                }
                cumLen += rangeLen;
            }
            if (targetRange < 0)
                return false;   // PC not in interruptible code

            uint totalInterruptibleLength = cumLen;
            for (int i = targetRange; i < numRanges; i++)
                totalInterruptibleLength += ranges[i].StopOffset - ranges[i].StartOffset;

            const int ChunkBits = 6;                             // log2(64)
            const int ChunkSize = 1 << ChunkBits;                // 64
            int normTotal = (int)CoffGcInfoTypes.NormalizeCodeOffset(totalInterruptibleLength);
            int numChunks = (normTotal + ChunkSize - 1) / ChunkSize;
            int targetChunk = (int)(normalizedPc >> ChunkBits);
            uint pcInChunk = normalizedPc & (ChunkSize - 1);

            // POINTER_SIZE varint, then `numChunks * numBitsPerPointer` raw
            // bits of chunk pointers, then byte-align → info2Offset.
            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(bitOffset);
            int numBitsPerPointer = (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.PointerSizeEncBase);
            if (numBitsPerPointer == 0)
                return true;   // no transitions encoded → liveOut stays all-false

            // Read chunk pointers into stackalloc (up to 128 chunks
            // = 8192 bytes of code — covers any realistic kernel method).
            if (numChunks > 128) Halt();
            Span<int> chunkPointers = stackalloc int[numChunks];
            for (int i = 0; i < numChunks; i++)
                chunkPointers[i] = (int)r.ReadBits(numBitsPerPointer);

            int info2Offset = (r.BitOffset + 7) & ~7;   // byte-align

            // Walk chunks 0..targetChunk, updating liveOut.
            for (int chunkIdx = 0; chunkIdx <= targetChunk; chunkIdx++)
            {
                int chunkPtr = chunkPointers[chunkIdx];
                if (chunkPtr == 0) continue;   // no state changes in this chunk

                int chunkBitOffset = info2Offset + chunkPtr - 1;
                ApplyChunkTransitions(
                    gcInfo, chunkBitOffset,
                    (int)slots.NumTracked,
                    /*isTargetChunk:*/ chunkIdx == targetChunk,
                    pcInChunk,
                    liveOut);
            }

            return true;
        }

        // Process one chunk: read couldBeLive bitmap + finalState bitmap +
        // per-slot transition lists. Updates liveOut[slotId] either to
        // finalState (if !isTargetChunk) or by toggling with parity of
        // transitions where offsetInChunk <= pcInChunk (if isTargetChunk).
        private static void ApplyChunkTransitions(
            byte* gcInfo,
            int chunkBitOffset,
            int numTracked,
            bool isTargetChunk,
            uint pcInChunk,
            Span<bool> liveOut)
        {
            // couldBeLive cursor + slotId iterator state — these read from
            // the bitmap region (couldBeLiveOffset in the source).
            int couldBeLiveOffset = chunkBitOffset;

            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(chunkBitOffset);

            bool fSimple = r.ReadBits(1) == 0;
            bool fSkipFirst = false;
            int slotId = 0;
            int couldBeLiveCnt = 0;
            if (!fSimple)
            {
                fSkipFirst = r.ReadBits(1) == 0;
                slotId = -1;
            }
            // Advance our couldBeLiveOffset cursor past the fSimple/skipFirst
            // bits (they live before the bitmap).
            couldBeLiveOffset = r.BitOffset;

            // r.BitOffset advances past the couldBeLive area to numCouldBeLive count.
            uint numCouldBeLive = GetNumCouldBeLiveSlots(ref r, numTracked, fSimple);

            // finalState bits live at current r position; bitOffset for
            // transitions is right after.
            int finalStateOffset = r.BitOffset;
            r.SetBitOffset(finalStateOffset + (int)numCouldBeLive);
            int transitionsOffset = r.BitOffset;

            BitReader finalReader = new BitReader(gcInfo);
            finalReader.SetBitOffset(finalStateOffset);

            BitReader transitionReader = new BitReader(gcInfo);
            transitionReader.SetBitOffset(transitionsOffset);

            BitReader couldBeLiveReader = new BitReader(gcInfo);
            couldBeLiveReader.SetBitOffset(couldBeLiveOffset);

            for (uint i = 0; i < numCouldBeLive; i++)
            {
                slotId = GetNextSlotId(ref couldBeLiveReader, fSimple, fSkipFirst, slotId, ref couldBeLiveCnt);

                bool finalState = finalReader.ReadBits(1) != 0;

                // Walk per-slot transition list, count flips before pcInChunk.
                int flipsTotal = 0;
                int flipsBeforePc = 0;
                while (transitionReader.ReadBits(1) != 0)
                {
                    uint offsetInChunk = transitionReader.ReadBits(6);   // log2(64)
                    flipsTotal++;
                    if (!isTargetChunk || offsetInChunk <= pcInChunk)
                        flipsBeforePc++;
                }

                if (slotId < liveOut.Length)
                {
                    if (isTargetChunk)
                    {
                        if ((flipsBeforePc & 1) == 1)
                            liveOut[slotId] = !liveOut[slotId];
                    }
                    else
                    {
                        liveOut[slotId] = finalState;
                    }
                }

                slotId++;
            }
        }

        // Count "could be live" slots for this chunk. fSimple branch reads
        // numTracked raw bits (one per tracked slot, 1 = in couldBeLive set).
        // RLE branch alternates skip/run runs. Advances the BitReader cursor
        // to the start of finalState bits.
        private static uint GetNumCouldBeLiveSlots(ref BitReader r, int numTracked, bool fSimple)
        {
            // r is already past the fSimple/skipFirst bits.
            uint count = 0;
            if (fSimple)
            {
                for (int i = 0; i < numTracked; i++)
                    if (r.ReadBits(1) != 0)
                        count++;
            }
            else
            {
                bool fSkip = r.ReadBits(1) == 0;
                bool fReport = true;
                uint readSlots = r.DecodeVarLengthUnsigned(
                    fSkip ? CoffGcInfoTypes.LivestateRleSkipEncBase
                          : CoffGcInfoTypes.LivestateRleRunEncBase);
                fSkip = !fSkip;
                while (readSlots < (uint)numTracked)
                {
                    uint cnt = r.DecodeVarLengthUnsigned(
                        fSkip ? CoffGcInfoTypes.LivestateRleSkipEncBase
                              : CoffGcInfoTypes.LivestateRleRunEncBase) + 1;
                    if (fReport) count += cnt;
                    readSlots += cnt;
                    fSkip = !fSkip;
                    fReport = !fReport;
                }
            }
            return count;
        }

        // Yield the next slot id from the couldBeLive iterator. State
        // (slotId / couldBeLiveCnt) is carried across calls. For fSimple
        // we scan bits one at a time; for RLE we maintain a (skip, run)
        // state machine.
        private static int GetNextSlotId(
            ref BitReader r,
            bool fSimple,
            bool fSkipFirst,
            int slotId,
            ref int couldBeLiveCnt)
        {
            if (fSimple)
            {
                while (r.ReadBits(1) == 0)
                    slotId++;
            }
            else if (couldBeLiveCnt > 0)
            {
                couldBeLiveCnt--;
            }
            else if (fSkipFirst)
            {
                int tmp = (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.LivestateRleSkipEncBase) + 1;
                slotId += tmp;
                couldBeLiveCnt = (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.LivestateRleRunEncBase);
            }
            else
            {
                int tmp = (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.LivestateRleRunEncBase) + 1;
                slotId += tmp;
                couldBeLiveCnt = (int)r.DecodeVarLengthUnsigned(CoffGcInfoTypes.LivestateRleSkipEncBase);
            }
            return slotId;
        }

        // Decode the slot table counts + skip per-slot details. Mirrors
        // GcSlotTable ctor in ILCompiler.Reflection.ReadyToRun. We don't
        // structure-store the per-slot data yet — that lands in the next
        // chunk together with the live-state enumerator. For now we just
        // need to advance the bit cursor accurately past the table.
        public static void DecodeSlotTable(byte* gcInfo, int bitOffset, out CoffGcSlotTable slots)
        {
            slots = default;
            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(bitOffset);

            if (r.ReadBit())
                slots.NumRegisters = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumRegistersEncBase);

            if (r.ReadBit())
            {
                slots.NumStackSlots = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumStackSlotsEncBase);
                slots.NumUntracked  = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumUntrackedSlotsEncBase);
            }

            slots.NumSlots = slots.NumRegisters + slots.NumStackSlots + slots.NumUntracked;
            slots.NumTracked = slots.NumRegisters + slots.NumStackSlots;

            if (slots.NumRegisters > 0)
                SkipRegisterList(ref r, (int)slots.NumRegisters);

            if (slots.NumStackSlots > 0)
                SkipStackSlotList(ref r, (int)slots.NumStackSlots);

            if (slots.NumUntracked > 0)
                SkipStackSlotList(ref r, (int)slots.NumUntracked);

            slots.BitOffsetAfterTable = r.BitOffset;
        }

        // Variant of DecodeSlotTable that fills caller's CoffGcSlot buffer
        // with per-slot detail (register number / stack base+offset / flags).
        // Used by mark-phase to resolve addresses; the count-only variant
        // is fine for sanity probes.
        //
        // slotsOut.Length must be >= total slot count (NumRegisters +
        // NumStackSlots + NumUntracked). Halts on undersize.
        public static void DecodeFullSlotTable(
            byte* gcInfo,
            int bitOffset,
            Span<CoffGcSlot> slotsOut,
            out CoffGcSlotTable counts)
        {
            counts = default;
            BitReader r = new BitReader(gcInfo);
            r.SetBitOffset(bitOffset);

            if (r.ReadBit())
                counts.NumRegisters = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumRegistersEncBase);

            if (r.ReadBit())
            {
                counts.NumStackSlots = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumStackSlotsEncBase);
                counts.NumUntracked  = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.NumUntrackedSlotsEncBase);
            }

            counts.NumSlots = counts.NumRegisters + counts.NumStackSlots + counts.NumUntracked;
            counts.NumTracked = counts.NumRegisters + counts.NumStackSlots;

            if ((int)counts.NumSlots > slotsOut.Length) Halt();

            int outIdx = 0;
            if (counts.NumRegisters > 0)
                DecodeRegisterList(ref r, (int)counts.NumRegisters, slotsOut, ref outIdx);
            if (counts.NumStackSlots > 0)
                DecodeStackSlotList(ref r, (int)counts.NumStackSlots, isUntracked: false, slotsOut, ref outIdx);
            if (counts.NumUntracked > 0)
                DecodeStackSlotList(ref r, (int)counts.NumUntracked, isUntracked: true, slotsOut, ref outIdx);

            counts.BitOffsetAfterTable = r.BitOffset;
        }

        private static void DecodeRegisterList(
            ref BitReader r, int n, Span<CoffGcSlot> slotsOut, ref int outIdx)
        {
            uint regNum = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterEncBase);
            byte flags = (byte)r.ReadBits(2);
            slotsOut[outIdx++] = new CoffGcSlot
            {
                Kind = 0, RegOrOffset = (int)regNum, Flags = flags,
            };
            for (int i = 1; i < n; i++)
            {
                if (flags != 0)
                {
                    regNum = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterEncBase);
                    flags = (byte)r.ReadBits(2);
                }
                else
                {
                    uint regDelta = r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterDeltaEncBase) + 1;
                    regNum += regDelta;
                }
                slotsOut[outIdx++] = new CoffGcSlot
                {
                    Kind = 0, RegOrOffset = (int)regNum, Flags = flags,
                };
            }
        }

        private static void DecodeStackSlotList(
            ref BitReader r, int n, bool isUntracked, Span<CoffGcSlot> slotsOut, ref int outIdx)
        {
            byte spBase = (byte)r.ReadBits(2);
            int normSpOffset = r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotEncBase);
            int spOffset = CoffGcInfoTypes.DenormalizeStackSlot(normSpOffset);
            byte flags = (byte)r.ReadBits(2);
            if (isUntracked) flags |= CoffGcSlotFlags.Untracked;
            slotsOut[outIdx++] = new CoffGcSlot
            {
                Kind = 1, SpBase = spBase, RegOrOffset = spOffset, Flags = flags,
            };
            for (int i = 1; i < n; i++)
            {
                spBase = (byte)r.ReadBits(2);
                if (flags != 0 && !isUntracked)
                {
                    normSpOffset = r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotEncBase);
                    spOffset = CoffGcInfoTypes.DenormalizeStackSlot(normSpOffset);
                    flags = (byte)r.ReadBits(2);
                    if (isUntracked) flags |= CoffGcSlotFlags.Untracked;
                }
                else if (flags != 0 && isUntracked)
                {
                    // Untracked slots always read full offset + flags (no
                    // delta-chain — matches stock decoder's pattern of
                    // separating tracked/untracked passes).
                    normSpOffset = r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotEncBase);
                    spOffset = CoffGcInfoTypes.DenormalizeStackSlot(normSpOffset);
                    flags = (byte)r.ReadBits(2);
                    flags |= CoffGcSlotFlags.Untracked;
                }
                else
                {
                    int normSpOffsetDelta = r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotDeltaEncBase);
                    normSpOffset += normSpOffsetDelta;
                    spOffset = CoffGcInfoTypes.DenormalizeStackSlot(normSpOffset);
                }
                slotsOut[outIdx++] = new CoffGcSlot
                {
                    Kind = 1, SpBase = spBase, RegOrOffset = spOffset, Flags = flags,
                };
            }
        }

        // Register list — first reg full-decode, subsequent either
        // full re-decode (when flags != 0) or delta-decode (when flags == 0).
        // The "flags carry over" pattern matches gcInfoEncoder spec.
        private static void SkipRegisterList(ref BitReader r, int n)
        {
            r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterEncBase);
            uint flags = r.ReadBits(2);
            for (int i = 1; i < n; i++)
            {
                if (flags != 0)
                {
                    r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterEncBase);
                    flags = r.ReadBits(2);
                }
                else
                {
                    r.DecodeVarLengthUnsigned(CoffGcInfoTypes.RegisterDeltaEncBase);
                }
            }
        }

        // Stack slot list — 2 bits spBase + signed varint sp offset + 2 bits flags
        // for the first entry; subsequent either full (flags!=0) or delta-only.
        private static void SkipStackSlotList(ref BitReader r, int n)
        {
            r.ReadBits(2);                                                          // spBase
            r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotEncBase);              // normSpOffset
            uint flags = r.ReadBits(2);
            for (int i = 1; i < n; i++)
            {
                r.ReadBits(2);                                                      // spBase
                if (flags != 0)
                {
                    r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotEncBase);
                    flags = r.ReadBits(2);
                }
                else
                {
                    r.DecodeVarLengthSigned(CoffGcInfoTypes.StackSlotDeltaEncBase);
                }
            }
        }
    }
}
