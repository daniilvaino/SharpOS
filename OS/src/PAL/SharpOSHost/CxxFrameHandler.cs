using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot.EH;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // Phase 4 of SEH unwind: MSVC C++ personality routine `__CxxFrameHandler3`.
    //
    // Called by the OS unwind/exception dispatcher (here: SehDispatch) once
    // per stack frame whose UNWIND_INFO carries `__CxxFrameHandler3` as its
    // language handler. Decides whether the function CAN catch (first pass)
    // and runs destructors + transfers control on second pass.
    //
    // ABI (RegDisplay = DISPATCHER_CONTEXT, x64):
    //   EXCEPTION_DISPOSITION __CxxFrameHandler3(
    //       EXCEPTION_RECORD*    pExceptionRecord,
    //       void*                pEstablisherFrame,
    //       CONTEXT*             pContextRecord,
    //       DISPATCHER_CONTEXT*  pDispatcherContext);
    //
    // The `HandlerData` field of DISPATCHER_CONTEXT points at an RVA of
    // FuncInfo (MSVC-emitted "function info" struct per try-rich function).
    //
    // FuncInfo layout (v3 = 0x19930520, v4 has different magic):
    //   +0x00 magicNumber          DWORD (0x19930520..0x19930522)
    //   +0x04 maxState              int
    //   +0x08 pUnwindMap            RVA  → UnwindMapEntry[maxState]
    //   +0x0C nTryBlocks            uint
    //   +0x10 pTryBlockMap          RVA  → TryBlockMapEntry[nTryBlocks]
    //   +0x14 nIPMap                uint
    //   +0x18 pIPtoStateMap         RVA  → IpToStateMapEntry[nIPMap]
    //   +0x1C pESTypeList           RVA  (exception specs, usually 0)
    //   +0x20 EHFlags                int  (bit 0 = strict, bit 1 = is-tryblockfunc)
    //
    // UnwindMapEntry (8 bytes):
    //   +0x00 toState   int  (state to transition to)
    //   +0x04 actionRva RVA  (unwind funclet — destructor)
    //
    // TryBlockMapEntry (20 bytes):
    //   +0x00 tryLow      int     (state range start)
    //   +0x04 tryHigh     int     (state range end)
    //   +0x08 catchHigh   int
    //   +0x0C nCatches    int
    //   +0x10 pHandlerArray RVA   → HandlerType[nCatches]
    //
    // HandlerType (20 bytes):
    //   +0x00 adjectives  uint    (qualifiers: const/volatile/reference/...)
    //   +0x04 pType       RVA     → TypeDescriptor (RTTI name)
    //   +0x08 dispCatchObj int    (frame offset where caught object is copied)
    //   +0x0C dispOfHandler RVA   (catch funclet body entry)
    //   +0x10 dispFrame   int     (frame-relative frame pointer for funclet)
    //
    // IpToStateMapEntry (8 bytes):
    //   +0x00 ip       RVA  (start of code range)
    //   +0x04 state    int  (active try state)
    //
    // CatchableTypeArray (from throwInfo):
    //   +0x00 nCatchableTypes int
    //   +0x04 arrayOfCatchableTypes  CatchableType[nCatchableTypes] (RVAs each)
    //
    // CatchableType (28 bytes):
    //   +0x00 properties      uint
    //   +0x04 pType          RVA → TypeDescriptor
    //   +0x08 ptd            PMD (sub-object adjustment, 12 bytes)
    //   +0x14 sizeOrOffset    int
    //   +0x18 copyFunction   RVA (copy ctor)
    //
    // TypeDescriptor begins с a vtable pointer (`type_info` vtable) — 8 bytes,
    // then a "spare" 8 bytes, then mangled name as null-terminated string.
    // Match is by string comparison of mangled names.
    internal static unsafe class CxxFrameHandler
    {
        // Permanent mainline hygiene (Phase A clean-freeze). The managed-EH
        // pillar is CLOSED (steps 70/71); this per-frame C++ EH search/
        // unwind dump was debug-only scaffolding from that bring-up. The EH
        // regression oracle is the boot Probes EH-gates (L8..L17) + the
        // hosted 21/21 battery — NOT this per-frame trace — so gating it
        // default-off loses no regression coverage and keeps mainline
        // output clean. HALT / alloc-failed / "bad magic" lines stay ON
        // (rare, signal). ILC dead-codes the const-false blocks.
        // Flip to true only when re-debugging the unwinder itself.
        private const bool Trace = false;

        private const uint MAGIC_V1 = 0x19930520;
        private const uint MAGIC_V2 = 0x19930521;
        private const uint MAGIC_V3 = 0x19930522;

        // Test whether a thrown object's CatchableType is assignable to a
        // catch handler's declared type. Match is by mangled name string
        // equality. C++ supports inheritance — a "Derived" throw matches a
        // "Base" catch — but only if the THROW side emitted Derived's full
        // CatchableTypeArray including Base. So we just walk thrown
        // CatchableTypeArray and compare each name against handler's type.
        private static bool MatchHandler(byte* image,
                                         HandlerType* handler,
                                         uint catchableArrayRva,
                                         out uint matchedCatchableIdx)
        {
            matchedCatchableIdx = 0xFFFFFFFFu;

            // catch (...): matches everything, and has no type to read.
            if (handler->pType == 0)
            {
                matchedCatchableIdx = 0;
                return true;
            }

            if (catchableArrayRva == 0) return false;

            byte* handlerTypeDesc = image + handler->pType;
            // type_info: skip 16 bytes of vtable+spare, then mangled name.
            byte* handlerName = handlerTypeDesc + 16;

            // CatchableTypeArray
            uint* arr = (uint*)(image + catchableArrayRva);
            int n = (int)arr[0];
            for (int i = 0; i < n; i++)
            {
                uint ctRva = arr[1 + i];
                CatchableType* ct = (CatchableType*)(image + ctRva);
                byte* throwTypeDesc = image + ct->pType;
                byte* throwName = throwTypeDesc + 16;
                if (StrEq(handlerName, throwName))
                {
                    matchedCatchableIdx = (uint)i;
                    return true;
                }
            }
            return false;
        }

        private const uint HT_IsReference = 0x08;
        private const uint CT_IsSimpleType = 0x01;

        /// <summary>
        /// Writes the thrown object into the catch clause's variable at
        /// <paramref name="slot"/>, as the MSVC runtime's BuildCatchObject
        /// does: a reference gets the object's address, a simple type (a
        /// pointer included) is copied — a pointer adjusted to the caught
        /// base — and a class is copy-constructed. CoreCLR's EX_CATCH is
        /// <c>catch (Exception*)</c>: without this its handler reads its
        /// exception pointer from an unwritten slot.
        /// </summary>
        internal static void BuildCatchObject(ulong thrown, ulong imageBase, ulong slot,
                                              uint adjectives, uint catchableTypeRva)
        {
            if (slot == 0 || catchableTypeRva == 0 || thrown == 0)
                return;

            CatchableType* ct = (CatchableType*)(imageBase + catchableTypeRva);

            if ((adjectives & HT_IsReference) != 0)
            {
                *(ulong*)slot = AdjustPointer(thrown, ct);
                return;
            }

            if ((ct->properties & CT_IsSimpleType) != 0)
            {
                byte* dst = (byte*)slot;
                byte* src = (byte*)thrown;
                for (int i = 0; i < ct->sizeOrOffset; i++) dst[i] = src[i];
                if (ct->sizeOrOffset == sizeof(ulong) && *(ulong*)slot != 0)
                    *(ulong*)slot = AdjustPointer(*(ulong*)slot, ct);
                return;
            }

            if (ct->copyFunction != 0)
            {
                var copy = (delegate* unmanaged<void*, void*, void>)(imageBase + ct->copyFunction);
                copy((void*)slot, (void*)AdjustPointer(thrown, ct));
                return;
            }

            byte* d = (byte*)slot;
            byte* s = (byte*)AdjustPointer(thrown, ct);
            for (int i = 0; i < ct->sizeOrOffset; i++) d[i] = s[i];
        }

        /// <summary>
        /// Copies the thrown object into <paramref name="copy"/> and returns
        /// the copy's address, or the original's when it does not fit. The
        /// original lives in the throwing frame, below the catching one: dead
        /// once the catch funclet's calls grow over it, while a reference to
        /// it, or a rethrow, still reads it. Size is the most-derived type's,
        /// the first entry of the throw's catchable types.
        /// </summary>
        internal static ulong KeepThrownObject(ulong thrown, ulong throwInfo, ulong imageBase,
                                               byte* copy, int capacity)
        {
            if (thrown == 0 || throwInfo == 0 || imageBase == 0)
                return thrown;

            uint catchableArrayRva = *(uint*)(throwInfo + 0x0C);
            if (catchableArrayRva == 0)
                return thrown;
            uint* catchables = (uint*)(imageBase + catchableArrayRva);
            if (catchables[0] == 0)
                return thrown;

            CatchableType* mostDerived = (CatchableType*)(imageBase + catchables[1]);
            int size = mostDerived->sizeOrOffset;
            if (size <= 0 || size > capacity)
                return thrown;

            byte* src = (byte*)thrown;
            for (int i = 0; i < size; i++) copy[i] = src[i];
            return (ulong)copy;
        }

        // From a derived object to the base the catch names (the catchable
        // type's pointer-to-member displacement, virtual base included).
        private static ulong AdjustPointer(ulong p, CatchableType* ct)
        {
            ulong adjusted = p + (ulong)(long)ct->pmdMemberDisp;
            if (ct->pmdVBaseDisp >= 0)
            {
                byte* vbTable = *(byte**)(p + (ulong)ct->pmdVBaseDisp);
                adjusted += (ulong)(long)(*(int*)(vbTable + ct->pmdVDispOff)) + (ulong)ct->pmdVBaseDisp;
            }
            return adjusted;
        }

        // The active state at an IP, from the IP-to-state map: the entry with
        // the largest ip <= the IP. On x64 the entries (like dispOfHandler and
        // the unwind actions) are IMAGE-relative RVAs — the first entry is the
        // function start itself. Compared against a function-relative offset,
        // as this did until step172, every entry was "past" the IP, the state
        // was always -1, and no C++ catch in the image ever matched: CoreCLR's
        // own EX_CATCH never ran, and its exceptions escaped instead.
        private static int FindCurrentState(byte* image, FuncInfo* fi, uint controlRva)
        {
            uint mapRva = fi->pIPtoStateMap;
            if (mapRva == 0) return -1;

            IpToStateMapEntry* map = (IpToStateMapEntry*)(image + mapRva);
            int state = -1;
            for (uint i = 0; i < fi->nIPMap; i++)
            {
                if (map[i].ip > controlRva) break;
                state = map[i].state;
            }
            return state;
        }

        // The try block that catches in this frame, for the unwind that stops
        // here: the state goes back to its tryLow — objects built inside the
        // try are destroyed, the ones built before it stay alive for the code
        // after the catch. Same search as the first pass, same answer.
        private static int CatchingTryLow(byte* image, FuncInfo* fi, int curState, uint catchableArrayRva)
        {
            TryBlockMapEntry* tbm = (TryBlockMapEntry*)(image + fi->pTryBlockMap);
            for (uint t = 0; t < fi->nTryBlocks; t++)
            {
                TryBlockMapEntry* tb = &tbm[t];
                if (curState < tb->tryLow || curState > tb->tryHigh) continue;
                HandlerType* handlers = (HandlerType*)(image + tb->pHandlerArray);
                for (int h = 0; h < tb->nCatches; h++)
                {
                    if (MatchHandler(image, &handlers[h], catchableArrayRva, out _))
                        return tb->tryLow;
                }
            }
            return -1;
        }

        // Personality routine entry point.
        [RuntimeExport("__CxxFrameHandler3")]
        public static int FrameHandler3(
            ExceptionRecord* pExceptionRecord,
            void* pEstablisherFrame,
            Context* pContextRecord,
            DispatcherContext* pDispatcherContext)
        {
            if (Trace) {
            Console.Write("[__CxxFrameHandler3] ENTER controlPc=0x");
            Console.WriteHex(pDispatcherContext->ControlPc);
            Console.Write(" flags=0x"); Console.WriteHex(pExceptionRecord->ExceptionFlags);
            Console.WriteLine("");
            }

            // Unwind pass first (we're called both for search AND unwind).
            bool unwinding = (pExceptionRecord->ExceptionFlags
                & (ExceptionRecord.EXCEPTION_UNWINDING | ExceptionRecord.EXCEPTION_EXIT_UNWIND)) != 0;

            // Only handle MSVC C++ throws on the search pass. Other codes
            // (HW faults, SEH, etc.) flow through unchanged.
            if (!unwinding &&
                pExceptionRecord->ExceptionCode != ExceptionRecord.EH_EXCEPTION_NUMBER)
            {
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            byte* image = (byte*)pDispatcherContext->ImageBase;
            uint funcInfoRva = *(uint*)pDispatcherContext->HandlerData;
            if (Trace) { Console.Write("  funcInfoRva=0x"); Console.WriteHex(funcInfoRva); Console.WriteLine(""); }
            if (funcInfoRva == 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            FuncInfo* fi = (FuncInfo*)(image + funcInfoRva);
            if (Trace) {
            Console.Write("  magic=0x"); Console.WriteHex(fi->magicNumber);
            Console.Write(" maxState="); Console.WriteInt(fi->maxState);
            Console.Write(" nTry="); Console.WriteInt((int)fi->nTryBlocks);
            Console.Write(" nIP="); Console.WriteInt((int)fi->nIPMap);
            Console.WriteLine("");
            }
            if (fi->magicNumber != MAGIC_V1 && fi->magicNumber != MAGIC_V2 && fi->magicNumber != MAGIC_V3)
            {
                Console.WriteLine("  bad magic");
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            uint funcStartRva = pDispatcherContext->FunctionEntry->BeginAddress;
            uint controlRva = (uint)(pDispatcherContext->ControlPc - pDispatcherContext->ImageBase);
            uint relIp = controlRva - funcStartRva;
            int curState = FindCurrentState(image, fi, controlRva);
            if (Trace) {
            Console.Write("  funcStart=0x"); Console.WriteHex(funcStartRva);
            Console.Write(" relIp=0x"); Console.WriteHex(relIp);
            Console.Write(" state="); Console.WriteInt(curState);
            Console.WriteLine("");
            }

            // Dump IP-to-state map raw entries (sage 2 says: see if entries
            // are func-relative or image-relative).
            if (Trace && fi->pIPtoStateMap != 0)
            {
                IpToStateMapEntry* ipMap = (IpToStateMapEntry*)(image + fi->pIPtoStateMap);
                Console.Write("  IPMap:");
                for (uint ii = 0; ii < fi->nIPMap && ii < 6; ii++)
                {
                    Console.Write(" [ip=0x"); Console.WriteHex(ipMap[ii].ip);
                    Console.Write(",s="); Console.WriteInt(ipMap[ii].state); Console.Write("]");
                }
                Console.WriteLine("");
            }

            if (curState < 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            bool cxxThrow = pExceptionRecord->ExceptionCode == ExceptionRecord.EH_EXCEPTION_NUMBER
                            && pExceptionRecord->NumberParameters >= 3;
            ulong throwInfoVa = cxxThrow ? pExceptionRecord->ExceptionInformation[2] : 0;
            ulong objectVa    = cxxThrow ? pExceptionRecord->ExceptionInformation[1] : 0;
            uint catchableArrayRva = throwInfoVa != 0 ? ((ThrowInfo*)throwInfoVa)->pCatchableTypeArray : 0;

            if (unwinding)
            {
                // Unwind pass, for any exception — a C++ throw, a managed one
                // coming through RtlUnwind, a hardware fault: run the
                // destructor funclets from the current state down. To -1 in a
                // frame the exception passes through; to the catching try's
                // tryLow in the frame that catches a C++ throw; not at all in
                // a target frame that is resumed for anything else, whose
                // objects stay alive. Until step172 this sat behind the "not
                // a C++ throw" check below and ran for C++ throws only.
                int targetState = -1;
                if ((pExceptionRecord->ExceptionFlags & ExceptionRecord.EXCEPTION_TARGET_UNWIND) != 0)
                    targetState = catchableArrayRva != 0
                        ? CatchingTryLow(image, fi, curState, catchableArrayRva)
                        : curState;
                UnwindToState(image, fi, curState, targetState, pEstablisherFrame);
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            if (throwInfoVa == 0 || objectVa == 0)
            {
                Console.WriteLine("  no throwInfo/obj");
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            // Search pass: walk all TryBlockMap entries whose state range
            // covers curState. For each, walk handlers, match types. First
            // match wins.
            TryBlockMapEntry* tbm = (TryBlockMapEntry*)(image + fi->pTryBlockMap);
            if (Trace && fi->nTryBlocks > 0)
            {
                Console.Write("  TryBlocks:");
                for (uint tt = 0; tt < fi->nTryBlocks && tt < 4; tt++)
                {
                    Console.Write(" ["); Console.WriteInt(tbm[tt].tryLow);
                    Console.Write(".."); Console.WriteInt(tbm[tt].tryHigh);
                    Console.Write(",n="); Console.WriteInt(tbm[tt].nCatches);
                    Console.Write("]");
                }
                Console.WriteLine("");
            }
            for (uint t = 0; t < fi->nTryBlocks; t++)
            {
                TryBlockMapEntry* tb = &tbm[t];
                if (curState < tb->tryLow || curState > tb->tryHigh) continue;

                HandlerType* handlers = (HandlerType*)(image + tb->pHandlerArray);
                for (int h = 0; h < tb->nCatches; h++)
                {
                    HandlerType* hd = &handlers[h];
                    if (Trace) {
                    Console.Write("    catch["); Console.WriteInt(h);
                    Console.Write("] pType=0x"); Console.WriteHex(hd->pType);
                    Console.WriteLine("");
                    }
                    if (!MatchHandler(image, hd, catchableArrayRva, out uint ctIdx))
                        continue;

                    // Found match. dispOfHandler is the RVA of the catch
                    // funclet (image-relative on x64, like the IP map). The
                    // dispatcher unwinds to this frame, builds the catch
                    // object and enters the funclet; the funclet returns the
                    // continuation in RAX.
                    pDispatcherContext->TargetIp = (ulong)(image + hd->dispOfHandler);
                    CxxFrameHandler4.CatchTransfer* transfer =
                        (CxxFrameHandler4.CatchTransfer*)pDispatcherContext->HistoryTable;
                    if (transfer != null)
                    {
                        uint* catchables = (uint*)(image + catchableArrayRva);
                        *transfer = new CxxFrameHandler4.CatchTransfer
                        {
                            Fh3 = true,
                            Adjectives = hd->adjectives,
                            DispType = (int)hd->pType,
                            DispCatchObj = (uint)hd->dispCatchObj,
                            DispOfHandler = (int)hd->dispOfHandler,
                            MatchedCatchableIdx = ctIdx,
                            CatchableTypeRva = hd->pType != 0 && catchableArrayRva != 0
                                ? catchables[1 + ctIdx] : 0,
                        };
                    }
                    pDispatcherContext->HandlerData = hd;
                    if (Trace) {
                    Console.Write("[__CxxFrameHandler3] caught at func+0x");
                    Console.WriteHex(hd->dispOfHandler);
                    Console.WriteLine("");
                    }
                    return ExceptionDispositionExt.ExceptionExecuteHandlerMarker;
                }
            }

            return (int)ExceptionDisposition.ExceptionContinueSearch;
        }

        // Walk unwindMap from currentState down to targetState, calling each
        // destructor funclet. funclets are emitted with parent's frame.
        private static void UnwindToState(byte* image, FuncInfo* fi,
                                          int currentState, int targetState,
                                          void* establisherFrame)
        {
            UnwindMapEntry* umap = (UnwindMapEntry*)(image + fi->pUnwindMap);
            int s = currentState;
            while (s > targetState)
            {
                if (s < 0 || s >= fi->maxState) break;
                UnwindMapEntry* u = &umap[s];
                if (u->actionRva != 0)
                {
                    // Invoke the destructor funclet the way the MSVC runtime
                    // does (_CallSettingFrame): the parent's establisher frame
                    // in RDX, which the funclet prologue takes as its frame
                    // pointer. RCX gets it too.
                    delegate* unmanaged<void*, void*, void> action =
                        (delegate* unmanaged<void*, void*, void>)(image + u->actionRva);
                    if (Trace) {
                    Console.Write("[__CxxFrameHandler3] unwind state ");
                    Console.WriteInt(s);
                    Console.Write(" → ");
                    Console.WriteInt(u->toState);
                    Console.Write(" action=0x");
                    Console.WriteHex(u->actionRva);
                    Console.WriteLine("");
                    }
                    action(establisherFrame, establisherFrame);
                }
                s = u->toState;
                if (s == currentState) break;     // safety: never make progress
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

    // MSVC EH structures.

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct FuncInfo
    {
        public uint magicNumber;
        public int  maxState;
        public uint pUnwindMap;
        public uint nTryBlocks;
        public uint pTryBlockMap;
        public uint nIPMap;
        public uint pIPtoStateMap;
        public uint pESTypeList;
        public int  EHFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnwindMapEntry
    {
        public int  toState;
        public uint actionRva;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TryBlockMapEntry
    {
        public int  tryLow;
        public int  tryHigh;
        public int  catchHigh;
        public int  nCatches;
        public uint pHandlerArray;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HandlerType
    {
        public uint adjectives;
        public uint pType;          // RVA → TypeDescriptor (or 0 = catch(...))
        public int  dispCatchObj;
        public uint dispOfHandler;
        public int  dispFrame;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IpToStateMapEntry
    {
        public uint ip;
        public int  state;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ThrowInfo
    {
        public uint attributes;
        public uint pmfnUnwind;          // RVA — destructor for exception object
        public uint pForwardCompat;
        public uint pCatchableTypeArray; // RVA → uint[nTypes+1]
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CatchableType
    {
        public uint properties;
        public uint pType;          // RVA → TypeDescriptor
        public int  pmdMemberDisp;
        public int  pmdVBaseDisp;
        public int  pmdVDispOff;
        public int  sizeOrOffset;
        public uint copyFunction;   // RVA → copy ctor
    }

    // Extension к ExceptionDisposition for "found handler, transfer control"
    // — MSVC uses a special return value, but our dispatcher recognizes by
    // pDispatcherContext->TargetIp being set. Use a sentinel int outside
    // the enum range that won't conflict with real values.
    internal static class ExceptionDispositionExt
    {
        // ExceptionContinueExecution = 0
        // ExceptionContinueSearch    = 1
        // ExceptionNestedException   = 2
        // ExceptionCollidedUnwind    = 3
        // 0x100 = our "execute handler" signal (translated by dispatcher).
        public const int ExceptionExecuteHandlerMarker = 0x100;
    }
}
