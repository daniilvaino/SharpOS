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
            // Special case: catch (...) — handler->pType == 0 RVA matches everything.
            if (handler->pType == 0)
            {
                matchedCatchableIdx = 0;
                return true;
            }
            return false;
        }

        // Find the active "state" given current IP (RVA from function start)
        // by walking IpToStateMap. Map is sorted by ip ascending; we want the
        // largest ip <= currentRva → state at that entry.
        private static int FindCurrentState(byte* image, FuncInfo* fi, uint funcStartRva, uint currentRva)
        {
            uint mapRva = fi->pIPtoStateMap;
            if (mapRva == 0) return -1;

            IpToStateMapEntry* map = (IpToStateMapEntry*)(image + mapRva);
            uint relIp = currentRva - funcStartRva;
            int state = -1;
            for (uint i = 0; i < fi->nIPMap; i++)
            {
                if (map[i].ip > relIp) break;
                state = map[i].state;
            }
            return state;
        }

        // Personality routine entry point.
        [RuntimeExport("__CxxFrameHandler3")]
        [UnmanagedCallersOnly(EntryPoint = "__CxxFrameHandler3")]
        public static int FrameHandler3(
            ExceptionRecord* pExceptionRecord,
            void* pEstablisherFrame,
            Context* pContextRecord,
            DispatcherContext* pDispatcherContext)
        {
            Console.Write("[__CxxFrameHandler3] ENTER controlPc=0x");
            Console.WriteHex(pDispatcherContext->ControlPc);
            Console.Write(" flags=0x"); Console.WriteHex(pExceptionRecord->ExceptionFlags);
            Console.WriteLine("");

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
            Console.Write("  funcInfoRva=0x"); Console.WriteHex(funcInfoRva); Console.WriteLine("");
            if (funcInfoRva == 0)
                return (int)ExceptionDisposition.ExceptionContinueSearch;

            FuncInfo* fi = (FuncInfo*)(image + funcInfoRva);
            Console.Write("  magic=0x"); Console.WriteHex(fi->magicNumber);
            Console.Write(" maxState="); Console.WriteInt(fi->maxState);
            Console.Write(" nTry="); Console.WriteInt((int)fi->nTryBlocks);
            Console.Write(" nIP="); Console.WriteInt((int)fi->nIPMap);
            Console.WriteLine("");
            if (fi->magicNumber != MAGIC_V1 && fi->magicNumber != MAGIC_V2 && fi->magicNumber != MAGIC_V3)
            {
                Console.WriteLine("  bad magic");
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            uint funcStartRva = pDispatcherContext->FunctionEntry->BeginAddress;
            uint controlRva = (uint)(pDispatcherContext->ControlPc - pDispatcherContext->ImageBase);
            uint relIp = controlRva - funcStartRva;
            int curState = FindCurrentState(image, fi, funcStartRva, controlRva);
            Console.Write("  funcStart=0x"); Console.WriteHex(funcStartRva);
            Console.Write(" relIp=0x"); Console.WriteHex(relIp);
            Console.Write(" state="); Console.WriteInt(curState);
            Console.WriteLine("");

            // Dump IP-to-state map raw entries (sage 2 says: see if entries
            // are func-relative or image-relative).
            if (fi->pIPtoStateMap != 0)
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

            ulong throwInfoVa = pExceptionRecord->ExceptionInformation[2];
            ulong objectVa    = pExceptionRecord->ExceptionInformation[1];

            if (throwInfoVa == 0 || objectVa == 0)
            {
                Console.WriteLine("  no throwInfo/obj");
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            ThrowInfo* ti = (ThrowInfo*)throwInfoVa;
            uint catchableArrayRva = ti->pCatchableTypeArray;

            if (unwinding)
            {
                // Unwind pass: run destructor funclets между current state и
                // target state (-1). Each UnwindMapEntry has a toState и
                // actionRva (destructor funclet — RVA of code to call).
                UnwindToState(image, fi, curState, -1, pEstablisherFrame);
                return (int)ExceptionDisposition.ExceptionContinueSearch;
            }

            // Search pass: walk all TryBlockMap entries whose state range
            // covers curState. For each, walk handlers, match types. First
            // match wins.
            TryBlockMapEntry* tbm = (TryBlockMapEntry*)(image + fi->pTryBlockMap);
            if (fi->nTryBlocks > 0)
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
                    Console.Write("    catch["); Console.WriteInt(h);
                    Console.Write("] pType=0x"); Console.WriteHex(hd->pType);
                    Console.WriteLine("");
                    if (!MatchHandler(image, hd, catchableArrayRva, out uint ctIdx))
                        continue;

                    // Found match — record dispatcher state, return EXECUTE.
                    // Dispatcher will second-pass unwind to this frame, then
                    // jump to handler->dispOfHandler within the function.
                    pDispatcherContext->TargetIp = (ulong)(image + funcStartRva + hd->dispOfHandler);
                    pDispatcherContext->HandlerData = hd;
                    Console.Write("[__CxxFrameHandler3] caught at func+0x");
                    Console.WriteHex(hd->dispOfHandler);
                    Console.WriteLine("");
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
                    // Invoke destructor funclet. Funclet ABI: RCX = frame pointer.
                    delegate* unmanaged<void*, void> action =
                        (delegate* unmanaged<void*, void>)(image + u->actionRva);
                    Console.Write("[__CxxFrameHandler3] unwind state ");
                    Console.WriteInt(s);
                    Console.Write(" → ");
                    Console.WriteInt(u->toState);
                    Console.Write(" action=0x");
                    Console.WriteHex(u->actionRva);
                    Console.WriteLine("");
                    action(establisherFrame);
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
