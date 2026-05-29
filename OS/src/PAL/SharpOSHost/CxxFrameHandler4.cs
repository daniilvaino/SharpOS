
using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // MSVC C++ personality routine `__CxxFrameHandler4` (FH4, compact format).
    //
    // clang-cl / cl /Ox (Release) emits the FH4 compact exception tables
    // instead of the fixed-layout FH3 ones. This is the FH4 sibling of
    // CxxFrameHandler.cs (__CxxFrameHandler3). The search/unwind/catch
    // *logic* is identical to FH3 — only the on-disk table decode differs:
    // FH4 packs everything as back-readable varints with no magic number.
    //
    // Ported from MSVC ehdata4_export.h / ehdata4.h (14.44.35207):
    //   - ReadUnsigned / ReadInt / s_negLengthTab / s_shiftTab  (verbatim)
    //   - DecompFuncInfo                                          (FuncInfo4)
    //   - UWMap4 / TryBlockMap4 / HandlerMap4 / IPtoStateMap4     (decoders)
    //
    // Cuts vs. the reference: no Compress namespace (write side), no C++
    // iterator wrappers (replaced with explicit cursor walks), no ESType /
    // noexcept-spec handling (FH4 has none), no BBT-driven separate-segment
    // catch-object building beyond what FrameUnwindToState needs.
    //
    // Thrown-side structures (ThrowInfo, CatchableType) are format-independent
    // and reused from CxxFrameHandler.cs. So are ExceptionRecord, Context,
    // DispatcherContext, RuntimeFunction and ExceptionDispositionExt
    // (SehStructs.cs). The catch-funclet / dtor-funclet invocation ABI
    // (RCX = frame pointer) mirrors FH3's UnwindToState exactly.
    internal static unsafe class CxxFrameHandler4
    {
        // Same const-false diagnostics gate as FH3 — ILC dead-codes the blocks.
        private const bool Trace = false;

        // --- FuncInfoHeader bits (1 byte) ---
        private const byte FH_isCatch     = 0x01;
        private const byte FH_isSeparated = 0x02;
        private const byte FH_BBT         = 0x04;
        private const byte FH_UnwindMap   = 0x08;
        private const byte FH_TryBlockMap = 0x10;
        // EHs = 0x20, NoExcept = 0x40, reserved = 0x80 (unused here).

        // --- HandlerTypeHeader bits (1 byte) ---
        private const byte HT_adjectives   = 0x01;
        private const byte HT_dispType     = 0x02;
        private const byte HT_dispCatchObj = 0x04;
        private const byte HT_contIsRVA    = 0x08;
        // contAddr is bits 4..5 (mask 0x30, shift 4): 0=none,1=one,2=two.

        // --- UnwindMapEntry4 type (low 2 bits of packed first field) ---
        private const uint UW_NoUW             = 0; // no action
        private const uint UW_DtorWithObj      = 1; // dtor + frame offset of object
        private const uint UW_DtorWithPtrToObj = 2; // dtor + frame offset of ptr-to-object
        private const uint UW_RVA              = 3; // direct action funclet RVA

        // Normalized FuncInfo4 (resolved RVAs + header flags).
        private struct FuncInfo4
        {
            public byte header;
            public int  dispUnwindMap;     // RVA, 0 if absent
            public int  dispTryBlockMap;   // RVA, 0 if absent
            public int  dispIPtoStateMap;  // RVA to IPtoStateMap (already segment-resolved)
            public uint dispFrame;         // catch-funclet frame displacement (isCatch only)
        }

        internal struct CatchTransfer
        {
            public byte Header;
            public uint Adjectives;
            public int  DispType;
            public uint DispCatchObj;
            public int  DispOfHandler;
            public ulong Continuation0;     // RVA, 0 if absent
            public ulong Continuation1;     // RVA, 0 if absent
            public uint MatchedCatchableIdx;
        }

        // ----------------------------------------------------------------
        // Back-reading varint primitives — ported VERBATIM from
        // ehdata4_export.h. `p` advances FORWARD by negLength but the 32-bit
        // word is read from BEHIND the cursor: *(uint*)(p - negLength - 4).
        // negLength is negative (e.g. -1), so (p - negLength - 4) = p + 1 - 4.
        // ----------------------------------------------------------------

        private static uint ReadUnsigned(ref byte* p)
        {
            // s_negLengthTab / s_shiftTab from ehdata4_export.h computed
            // directly (collection-expression table literals need C# 12; we
            // are on C# 11). Encoded length = position of the lowest CLEAR bit
            // in the low nibble (1..5); negLength is its negation;
            // shift = 32 - 7*length, with length 5 -> shift 0.
            int lengthBits = *p & 0x0F;
            int length;
            if ((lengthBits & 1) == 0)      length = 1;
            else if ((lengthBits & 2) == 0) length = 2;
            else if ((lengthBits & 4) == 0) length = 3;
            else if ((lengthBits & 8) == 0) length = 4;
            else                            length = 5;

            int negLength = -length;                 // negative, as in the table
            int shift = (length == 5) ? 0 : 32 - 7 * length;
            uint result = *(uint*)(p - negLength - 4);
            result >>= shift;
            p -= negLength;                          // advances forward
            return result;
        }

        private static int ReadInt(ref byte* p)
        {
            int value = *(int*)p;
            p += sizeof(int);
            return value;
        }

        // ----------------------------------------------------------------
        // DecompFuncInfo — port of ehdata4_export.h. Resolves the IPtoState
        // map for the active segment when the function is separated.
        // ----------------------------------------------------------------
        private static void DecompFuncInfo(byte* image, byte* buffer, int functionStart, out FuncInfo4 fi)
        {
            fi = default;
            fi.header = buffer[0];
            buffer++;

            if ((fi.header & FH_BBT) != 0)
                ReadUnsigned(ref buffer);             // bbtFlags, discarded

            if ((fi.header & FH_UnwindMap) != 0)
                fi.dispUnwindMap = ReadInt(ref buffer);

            if ((fi.header & FH_TryBlockMap) != 0)
                fi.dispTryBlockMap = ReadInt(ref buffer);

            if ((fi.header & FH_isSeparated) != 0)
            {
                // Default: no states for this segment.
                fi.dispIPtoStateMap = 0;
                int dispToSegMap = ReadInt(ref buffer);
                if (dispToSegMap != 0)
                {
                    byte* segBuffer = image + dispToSegMap;
                    uint numSegEntries = ReadUnsigned(ref segBuffer);
                    for (uint i = 0; i < numSegEntries; i++)
                    {
                        int segRVA = ReadInt(ref segBuffer);
                        int dispSegTable = ReadInt(ref segBuffer);
                        if (segRVA == functionStart)
                        {
                            fi.dispIPtoStateMap = dispSegTable;
                            break;
                        }
                    }
                }
                // dispToSegMap == 0 is "should not happen" in the reference
                // (__fastfail). We leave dispIPtoStateMap = 0 → state -1 →
                // ContinueSearch, which is the safe non-catching outcome.
            }
            else
            {
                fi.dispIPtoStateMap = ReadInt(ref buffer);
            }

            if ((fi.header & FH_isCatch) != 0)
                fi.dispFrame = ReadUnsigned(ref buffer);
        }

        // ----------------------------------------------------------------
        // StateFromIp — decode the IPtoStateMap, find the largest entry IP
        // <= relIp and return its state (mirror of FH3 FindCurrentState).
        // IPs are function-relative AND delta-encoded; states are stored +1.
        // ----------------------------------------------------------------
        private static int StateFromIp(byte* image, in FuncInfo4 fi, uint funcStartRva, uint controlRva)
        {
            if (fi.dispIPtoStateMap == 0) return -1;

            byte* buffer = image + fi.dispIPtoStateMap;
            uint numEntries = ReadUnsigned(ref buffer);

            int state = -1;
            uint prevIp = 0; // delta accumulator (function-relative)
            for (uint i = 0; i < numEntries; i++)
            {
                uint ipDelta = ReadUnsigned(ref buffer);
                uint relIp = prevIp + ipDelta;            // function-relative IP of this entry
                int entryState = (int)ReadUnsigned(ref buffer) - 1; // stored +1

                uint entryRva = funcStartRva + relIp;
                if (entryRva > controlRva) break;
                state = entryState;

                prevIp = relIp;
            }
            return state;
        }

        // ----------------------------------------------------------------
        // Personality routine entry point. Replaces the old panic stub.
        // ----------------------------------------------------------------
        [RuntimeExport("__CxxFrameHandler4")]
        [UnmanagedCallersOnly(EntryPoint = "__CxxFrameHandler4")]
        public static int FrameHandler4(
            ExceptionRecord* pExceptionRecord,
            void* pEstablisherFrame,
            Context* pContextRecord,
            DispatcherContext* pDispatcherContext)
        {
            if (Trace) {
            Console.Write("[__CxxFrameHandler4] ENTER controlPc=0x");
            Console.WriteHex(pDispatcherContext->ControlPc);
            Console.Write(" flags=0x"); Console.WriteHex(pExceptionRecord->ExceptionFlags);
            Console.WriteLine("");
            }

            bool unwinding = (pExceptionRecord->ExceptionFlags
                & (ExceptionRecord.EXCEPTION_UNWINDING | ExceptionRecord.EXCEPTION_EXIT_UNWIND)) != 0;

            if (!unwinding &&
                pExceptionRecord->ExceptionCode != ExceptionRecord.EH_EXCEPTION_NUMBER)
            {
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            byte* image = (byte*)pDispatcherContext->ImageBase;
            uint funcInfoRva = *(uint*)pDispatcherContext->HandlerData;
            if (funcInfoRva == 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            uint funcStartRva = pDispatcherContext->FunctionEntry->BeginAddress;
            uint controlRva = (uint)(pDispatcherContext->ControlPc - pDispatcherContext->ImageBase);

            // FH4 has NO magic number; presence of the personality IS the marker.
            DecompFuncInfo(image, image + funcInfoRva, (int)funcStartRva, out FuncInfo4 fi);

            int curState = StateFromIp(image, fi, funcStartRva, controlRva);
            if (Trace) {
            Console.Write("  funcStart=0x"); Console.WriteHex(funcStartRva);
            Console.Write(" controlRva=0x"); Console.WriteHex(controlRva);
            Console.Write(" state="); Console.WriteInt(curState);
            Console.WriteLine("");
            }

            if (unwinding)
            {
                // Unwind pass: run dtor funclets from curState down to -1.
                FrameUnwindToState(image, in fi, curState, -1, pEstablisherFrame);
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            if (curState < 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            ulong throwInfoVa = pExceptionRecord->ExceptionInformation[2];
            ulong objectVa    = pExceptionRecord->ExceptionInformation[1];
            if (throwInfoVa == 0 || objectVa == 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            ThrowInfo* ti = (ThrowInfo*)throwInfoVa;
            uint catchableArrayRva = ti->pCatchableTypeArray;

            if (fi.dispTryBlockMap == 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            // Walk the TryBlockMap. Entries are sequential varint records;
            // we decode them one-by-one with a forward cursor.
            byte* tbCursor = image + fi.dispTryBlockMap;
            uint numTryBlocks = ReadUnsigned(ref tbCursor);

            for (uint t = 0; t < numTryBlocks; t++)
            {
                int tryLow      = (int)ReadUnsigned(ref tbCursor);
                int tryHigh     = (int)ReadUnsigned(ref tbCursor);
                int catchHigh   = (int)ReadUnsigned(ref tbCursor);
                int dispHandler = ReadInt(ref tbCursor);

                if (curState < tryLow || curState > tryHigh) continue;
                if (dispHandler == 0) continue;

                // HandlerMap: count + sequential HandlerType4 records.
                byte* hCursor = image + dispHandler;
                uint numHandlers = ReadUnsigned(ref hCursor);

                for (uint h = 0; h < numHandlers; h++)
                {
                    DecodeHandler(image, (int)funcStartRva, ref hCursor,
                        out byte htHeader, out uint adjectives, out int dispType,
                        out uint dispCatchObj, out int dispOfHandler,
                        out ulong cont0, out ulong cont1);

                    if (!MatchHandler4(image, dispType, catchableArrayRva, out uint ctIdx))
                        continue;

                    // Match. Mirror FH3: set TargetIp to handler funclet body,
                    // hand the dispatcher the funcInfoRva location (HandlerData
                    // already points there). We do NOT rewrite HandlerData to a
                    // FH3 HandlerType* because the layout differs; instead leave
                    // the original HandlerData (funcInfo RVA cell) intact so the
                    // dispatcher's second (unwind) pass re-enters here and runs
                    // FrameUnwindToState. The dispatcher uses TargetIp to jump.
                    // FH4 HandlerType4.dispOfHandler is IMAGE-relative
                    // (ehdata4_export.h: "Image relative offset of 'catch'
                    // code"; maps resolve via __RVAtoRealOffset = imageBase +
                    // RVA). Do NOT add funcStartRva (that's the FH3 convention,
                    // where dispOfHandler is function-relative).
                    pDispatcherContext->TargetIp =
                        (ulong)(image + (uint)dispOfHandler);
                    CatchTransfer transfer = new CatchTransfer
                    {
                        Header = htHeader,
                        Adjectives = adjectives,
                        DispType = dispType,
                        DispCatchObj = dispCatchObj,
                        DispOfHandler = dispOfHandler,
                        Continuation0 = cont0,
                        Continuation1 = cont1,
                        MatchedCatchableIdx = ctIdx
                    };
                    CatchTransfer* outTransfer = (CatchTransfer*)pDispatcherContext->HistoryTable;
                    if (outTransfer != null)
                        *outTransfer = transfer;
                    if (Trace) {
                    Console.Write("[__CxxFrameHandler4] caught, TargetIp=image+0x");
                    Console.WriteHex((uint)dispOfHandler);
                    Console.Write(" cont0=0x");
                    Console.WriteHex(cont0);
                    Console.Write(" catchObj=0x");
                    Console.WriteHex(dispCatchObj);
                    Console.WriteLine("");
                    }
                    return ExceptionDispositionExt.ExceptionExecuteHandlerMarker;
                }
            }

            return (int)ExceptionDisposition.ExceptionContinueSearch;
        }

        // Decode a single HandlerType4 record at the cursor (advances it).
        private static void DecodeHandler(
            byte* image, int functionStart, ref byte* buffer,
            out byte header, out uint adjectives, out int dispType,
            out uint dispCatchObj, out int dispOfHandler,
            out ulong cont0, out ulong cont1)
        {
            adjectives = 0; dispType = 0; dispCatchObj = 0;
            cont0 = 0; cont1 = 0;

            header = buffer[0];
            buffer++;

            if ((header & HT_adjectives) != 0)
                adjectives = ReadUnsigned(ref buffer);

            if ((header & HT_dispType) != 0)
                dispType = ReadInt(ref buffer);

            if ((header & HT_dispCatchObj) != 0)
                dispCatchObj = ReadUnsigned(ref buffer);

            dispOfHandler = ReadInt(ref buffer);

            int contAddr = (header >> 4) & 0x3;
            bool contIsRVA = (header & HT_contIsRVA) != 0;
            if (contIsRVA)
            {
                if (contAddr == 1) cont0 = (ulong)(uint)ReadInt(ref buffer);
                else if (contAddr == 2)
                {
                    cont0 = (ulong)(uint)ReadInt(ref buffer);
                    cont1 = (ulong)(uint)ReadInt(ref buffer);
                }
            }
            else
            {
                if (contAddr == 1)
                    cont0 = (ulong)((uint)functionStart + ReadUnsigned(ref buffer));
                else if (contAddr == 2)
                {
                    cont0 = (ulong)((uint)functionStart + ReadUnsigned(ref buffer));
                    cont1 = (ulong)((uint)functionStart + ReadUnsigned(ref buffer));
                }
            }
        }

        // Type-match against the thrown object's CatchableTypeArray. The
        // thrown side is format-independent; only the catch type descriptor
        // RVA differs (FH4 calls it dispType). Same mangled-name compare as
        // FH3's MatchHandler — replicated here on the decoded dispType.
        private static bool MatchHandler4(byte* image, int dispType,
                                          uint catchableArrayRva,
                                          out uint matchedCatchableIdx)
        {
            matchedCatchableIdx = 0xFFFFFFFFu;

            // catch(...) — dispType == 0 matches everything.
            if (dispType == 0)
            {
                matchedCatchableIdx = 0;
                return true;
            }
            if (catchableArrayRva == 0) return false;

            byte* handlerName = image + (uint)dispType + 16; // skip vtable+spare

            uint* arr = (uint*)(image + catchableArrayRva);
            int n = (int)arr[0];
            for (int i = 0; i < n; i++)
            {
                uint ctRva = arr[1 + i];
                CatchableType* ct = (CatchableType*)(image + ctRva);
                byte* throwName = image + ct->pType + 16;
                if (StrEq(handlerName, throwName))
                {
                    matchedCatchableIdx = (uint)i;
                    return true;
                }
            }
            return false;
        }

        // ----------------------------------------------------------------
        // FrameUnwindToState — run dtor funclets walking the UnwindMap from
        // currentState down to targetState. FH4's UnwindMap is a back-linked
        // chain: each entry's `nextOffset` is the byte distance BACK to the
        // entry for its predecessor state. We must therefore index the entry
        // for `currentState` first (forward scan), then walk back.
        // ----------------------------------------------------------------
        private static void FrameUnwindToState(byte* image, in FuncInfo4 fi,
                                               int currentState, int targetState,
                                               void* establisherFrame)
        {
            if (fi.dispUnwindMap == 0) return;
            if (currentState <= targetState) return;

            byte* mapStart = image + fi.dispUnwindMap;
            byte* cursor = mapStart;
            uint numEntries = ReadUnsigned(ref cursor);
            // `cursor` now points at entry index 0. Entries are stored in
            // ascending state order; index == state.
            byte* entriesBase = cursor;

            if (currentState < 0 || (uint)currentState >= numEntries) return;

            // Only full unwind (targetState == -1) is exercised, same as FH3's
            // UnwindToState(curState, -1). FH4 encodes each entry's toState as
            // a byte offset back to the target entry -- NOT necessarily
            // state-1: a try with several objects can jump (e.g. 5 -> 1), and
            // the skipped states' dtors must NOT run. We therefore FOLLOW the
            // toState chain via the back-offset, not a linear state-- decrement.
            // An entry whose back-offset lands BEFORE the entries buffer denotes
            // toState == -1 (per ehdata4_export.h) -> terminate.
            if (targetState != -1) return; // partial unwind not needed (FH3 parity)

            // Forward-scan (index == state, ascending) to currentState's entry.
            byte* entryStart = entriesBase;
            for (int s = 0; s < currentState; s++)
            {
                byte* tmp = entryStart;
                DecodeUnwindEntry(ref tmp, out _, out _, out _, out _);
                entryStart = tmp; // start of next entry
            }

            byte* curEntryStart = entryStart;
            while (true)
            {
                byte* p = curEntryStart;
                DecodeUnwindEntry(ref p, out uint nextOffset, out uint type,
                                  out int action, out uint objOffset);

                if (Trace) {
                Console.Write("[FH4 unwind] type="); Console.WriteInt((int)type);
                Console.Write(" action=0x"); Console.WriteHex((uint)action);
                Console.Write(" nextOff=0x"); Console.WriteHex(nextOffset);
                Console.Write(" objOff=0x"); Console.WriteHex(objOffset);
                Console.WriteLine("");
                }

                if (type == UW_RVA && action != 0)
                {
                    // Direct action funclet. ABI: RCX = frame pointer.
                    delegate* unmanaged<void*, void> fn =
                        (delegate* unmanaged<void*, void>)(image + (uint)action);
                    fn(establisherFrame);
                }
                else if ((type == UW_DtorWithObj || type == UW_DtorWithPtrToObj) && action != 0)
                {
                    // Dtor funclet called with the object pointer in RCX.
                    // DtorWithObj:      object lives at frame+objOffset.
                    // DtorWithPtrToObj: frame+objOffset holds a pointer to it.
                    byte* objSlot = (byte*)establisherFrame + objOffset;
                    void* objPtr = (type == UW_DtorWithObj)
                        ? (void*)objSlot
                        : *(void**)objSlot;
                    delegate* unmanaged<void*, void> dtor =
                        (delegate* unmanaged<void*, void>)(image + (uint)action);
                    dtor(objPtr);
                }
                // UW_NoUW: nothing to run.

                // Follow the toState chain: target entry starts nextOffset
                // bytes before THIS entry. Landing before the entries buffer
                // means toState == -1 -> done.
                if (nextOffset == 0) break;            // safety: no progress
                byte* nextEntry = curEntryStart - nextOffset;
                if (nextEntry < entriesBase) break;    // toState == -1
                curEntryStart = nextEntry;
            }
        }

        // Decode one UnwindMapEntry4 at the cursor (advances it past the
        // entry). Returns the back-offset to the predecessor entry, the
        // entry type, the action RVA and the object frame-offset.
        private static void DecodeUnwindEntry(ref byte* buffer,
            out uint nextOffset, out uint type, out int action, out uint objOffset)
        {
            action = 0; objOffset = 0;
            uint packed = ReadUnsigned(ref buffer);
            type = packed & 0x3;
            nextOffset = packed >> 2;

            if (type == UW_DtorWithObj || type == UW_DtorWithPtrToObj)
            {
                action = ReadInt(ref buffer);
                objOffset = ReadUnsigned(ref buffer);
            }
            else if (type == UW_RVA)
            {
                action = ReadInt(ref buffer);
            }
        }

        private static bool StrEq(byte* a, byte* b)
        {
            for (int i = 0; ; i++)
            {
                if (a[i] != b[i]) return false;
                if (a[i] == 0) return true;
            }
        }
    }
}
