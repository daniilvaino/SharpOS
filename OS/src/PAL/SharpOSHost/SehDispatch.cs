using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot;
using OS.Boot.EH;
using OS.Hal;
using OS.Kernel;
using SharpOS.Std.NoRuntime;

namespace OS.PAL.SharpOSHost
{
    // Phase 5 of SEH unwind: exception dispatch + raise driver.
    //
    //   _CxxThrowException(pObj, pThrowInfo)
    //   → builds EXCEPTION_RECORD с code = EH_EXCEPTION_NUMBER
    //   → calls captureContext()
    //   → walks frames calling personality routines (search pass)
    //   → on match: second-pass unwind, run destructors, jump к catch
    //
    // We implement only the path needed for CoreCLR's native C++ EH:
    // raised via _CxxThrowException, caught by C++ try/catch, possibly
    // crossing many native frames. No SEH from kernel-side faults.
    //
    // Resume-to-catch is done by:
    //   1. Run destructor funclets along the way (already handled by
    //      __CxxFrameHandler3's unwind-pass path)
    //   2. Restore CONTEXT to the catching frame's state (Rsp = catcher's
    //      SP, callee-saved regs from where unwinder spilled)
    //   3. Set CONTEXT.Rip = catch handler entry
    //   4. RtlRestoreContext (custom asm helper) — load registers from
    //      CONTEXT и jump.
    internal static unsafe class SehDispatch
    {
        // Permanent mainline hygiene (Phase A clean-freeze). Managed-EH
        // pillar CLOSED (steps 70/71); this per-frame SEH raise/search +
        // throw-side dump was debug-only bring-up scaffolding. Regression
        // oracle = boot Probes EH-gates (L8..L17) + hosted 21/21 battery,
        // NOT this trace — default-off loses no coverage, keeps mainline
        // clean. HALT / alloc-failed / invalid-Rip / walked-out stay ON
        // (signal). Flip to true only when re-debugging the unwinder.
        private const bool Trace = false;

        // RaiseException / RtlRaiseException — top of the dispatch chain.
        // Builds an ExceptionRecord with the supplied code, captures
        // current context, drives the unwind loop.
        [RuntimeExport("RaiseException")]
        [UnmanagedCallersOnly(EntryPoint = "RaiseException")]
        public static void RaiseException(uint code, uint flags, uint nParams, ulong* args)
            => RaiseExceptionImpl(code, flags, nParams, args);

        private static void RaiseExceptionImpl(uint code, uint flags, uint nParams, ulong* args)
        {
            if (Trace) {
            Console.Write("[seh] RaiseException code=0x"); Console.WriteHex(code);
            Console.Write(" nParams="); Console.WriteInt((int)nParams);
            Console.WriteLine("");
            }

            // For C++ throws (code = EH_EXCEPTION_NUMBER), the throwInfo arg
            // carries enough RTTI to extract the thrown type's mangled name.
            // Format on the wire: args[0]=magic, args[1]=pObject,
            //                     args[2]=pThrowInfo, args[3]=imageBase.
            // ThrowInfo+0x0C = pCatchableTypeArray (RVA from imageBase).
            // CatchableTypeArray = {count, ctype[count]} where ctype is RVA.
            // CatchableType+0x04 = pType (RVA → TypeDescriptor).
            // TypeDescriptor+0x10 = mangled name (null-terminated bytes).
            if (Trace && code == ExceptionRecord.EH_EXCEPTION_NUMBER && nParams >= 4 && args != null)
            {
                ulong throwInfoVa = args[2];
                ulong imgBase     = args[3];
                if (throwInfoVa != 0 && imgBase != 0)
                {
                    byte* image = (byte*)imgBase;
                    uint catchableArrRva = *(uint*)(throwInfoVa + 0x0C);
                    if (catchableArrRva != 0)
                    {
                        uint* arr = (uint*)(image + catchableArrRva);
                        uint nTypes = arr[0];
                        Console.Write("[seh] throw type chain ("); Console.WriteInt((int)nTypes);
                        Console.WriteLine("):");
                        for (uint i = 0; i < nTypes && i < 6; i++)
                        {
                            uint ctRva = arr[1 + i];
                            uint pTypeRva = *(uint*)(image + ctRva + 4);
                            byte* mangledName = image + pTypeRva + 16;
                            Console.Write("  - ");
                            while (*mangledName != 0)
                            {
                                Console.WriteChar((char)*mangledName);
                                mangledName++;
                            }
                            Console.WriteLine("");
                        }
                    }
                    // HRException::m_hr — empirical offset is 0x14 (not 0x10
                    // as initial guess). Layout: vtable@0, ?@8 (padding or
                    // extra field), m_innerException@10, m_hr@0x18? Let me
                    // dump 24 bytes and extract HR from observed location.
                    ulong objVa = args[1];
                    if (objVa != 0)
                    {
                        uint hr = *(uint*)(objVa + 0x14);
                        Console.Write("[seh] m_hr = 0x"); Console.WriteHex(hr);
                        Console.WriteLine("");
                        // EEMessageException has m_kind/m_hr/m_resID/m_arg1..6.
                        // Dump raw object qwords so we can read resID без
                        // угадывания offset — corerror lookup даёт точную
                        // причину throw'а (vs бесполезный generic m_hr).
                        Console.Write("[seh] exc obj dump:");
                        for (int q = 0; q < 10; q++)
                        {
                            Console.Write(" +0x"); Console.WriteHex((ulong)(q * 8), 2);
                            Console.Write("="); Console.WriteHex(*(ulong*)(objVa + (ulong)(q * 8)));
                        }
                        Console.WriteLine("");
                    }
                }
            }

            // Allocate Context + ExceptionRecord на GC heap, not stack.
            // Together они = ~1.4 KB; combined с the dispatch loop's deeper
            // call chain, putting them on stack overflows the kernel stack
            // (no guard page) and corrupts adjacent heap. The crash dump
            // showed RSP migrated to a GcHeap region filled with 0xAF
            // pattern — classic stack-bottom-falls-into-heap pattern.
            ExceptionRecord* rec = (ExceptionRecord*)GcHeap.AllocateRaw((uint)sizeof(ExceptionRecord));
            Context* ctx = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
            if (rec == null || ctx == null)
            {
                Console.WriteLine("[seh] GcHeap alloc failed");
                Panic.Fail("seh alloc");
                return;
            }
            // GcHeap returns zeroed memory.
            rec->ExceptionCode = code;
            rec->ExceptionFlags = flags;
            rec->NumberParameters = nParams;
            if (args != null)
            {
                int n = (int)nParams;
                if (n > 15) n = 15;
                for (int i = 0; i < n; i++) rec->ExceptionInformation[i] = args[i];
            }

            CaptureCurrentContext(ctx);
            // Adjust to caller's frame: RtlCaptureContext returned to here,
            // so its caller (us) is one frame up. Our caller wants context
            // as-of THEIR call to RaiseException. Skip our prologue by
            // running RtlVirtualUnwind one step before dispatch.
            UnwindOneFrame(ctx);

            DispatchException(rec, ctx);
            // Should not return — if we get here, no handler matched.
            Console.WriteLine("[SehDispatch] no handler matched — HALT");
            Panic.Fail("unhandled exception");
        }

        // _CxxThrowException — MSVC-emitted entry for `throw obj;`.
        [RuntimeExport("_CxxThrowException")]
        [UnmanagedCallersOnly(EntryPoint = "_CxxThrowException")]
        public static void CxxThrow(void* pObject, void* pThrowInfo)
        {
            ulong* args = stackalloc ulong[4];
            args[0] = ExceptionRecord.EH_MAGIC_NUMBER1;
            args[1] = (ulong)pObject;
            args[2] = (ulong)pThrowInfo;
            args[3] = (ulong)CoffRuntimeFunctionTable.ImageBase;

            RaiseExceptionImpl(ExceptionRecord.EH_EXCEPTION_NUMBER,
                               ExceptionRecord.EXCEPTION_NONCONTINUABLE,
                               4, args);
            // RaiseException doesn't return on success — if we're here,
            // unwind failed.
            Console.WriteLine("[_CxxThrowException] RaiseException returned");
            Panic.Fail("_CxxThrowException fallthrough");
        }

        // Check whether the C++ throw described by `rec` carries an
        // HRException (or derived type). MSVC ThrowInfo's pCatchableTypeArray
        // lists all types к which the thrown object can be caught (the type
        // itself + base classes). We look for `HRException` substring в any
        // of those mangled names.
        //
        // Throw record params (set by _CxxThrowException / inlined throw):
        //   ExceptionInformation[0] = MAGIC (0x19930520..22)
        //   ExceptionInformation[1] = pObject
        //   ExceptionInformation[2] = pThrowInfo
        //   ExceptionInformation[3] = imageBase
        private static bool IsHRExceptionThrow(ExceptionRecord* rec)
        {
            if (rec->NumberParameters < 4) return false;
            ulong throwInfoVa = rec->ExceptionInformation[2];
            ulong imgBase = rec->ExceptionInformation[3];
            if (throwInfoVa == 0 || imgBase == 0) return false;

            byte* image = (byte*)imgBase;
            uint catchableArrRva = *(uint*)(throwInfoVa + 0x0C);
            if (catchableArrRva == 0) return false;

            uint* arr = (uint*)(image + catchableArrRva);
            uint nTypes = arr[0];
            for (uint i = 0; i < nTypes && i < 8; i++)
            {
                uint ctRva = arr[1 + i];
                uint pTypeRva = *(uint*)(image + ctRva + 4);
                byte* mangledName = image + pTypeRva + 16;
                // Match "HRException" substring inside the mangled name.
                // Names look like ".PEAVHRException@@" or ".?AVHRException@@".
                if (ContainsAscii(mangledName, "HRException")) return true;
            }
            return false;
        }

        // Walk the throw type chain and synthesize an HRESULT для уровней-
        // host translation. Returns true if a known EEException family member
        // is found; *outHr receives the appropriate HRESULT.
        //
        // CoreCLR EEException carries m_kind enum, not m_hr directly — actual
        // HR comes from a virtual GetHR() we can't easily call. Empirically
        // mapping by exception type name к canonical CLR HRESULT is enough
        // for host-boundary translation:
        //   EETypeLoadException     → COR_E_TYPELOAD       (0x80131522)
        //   EEFileLoadException     → COR_E_FILELOAD       (0x80131621)
        //   EEMessageException etc. → E_FAIL (generic)     (0x80004005)
        private static bool TryGetEEExceptionHResult(ExceptionRecord* rec, uint* outHr)
        {
            *outHr = 0;
            if (rec->NumberParameters < 4) return false;
            ulong throwInfoVa = rec->ExceptionInformation[2];
            ulong imgBase = rec->ExceptionInformation[3];
            if (throwInfoVa == 0 || imgBase == 0) return false;

            byte* image = (byte*)imgBase;
            uint catchableArrRva = *(uint*)(throwInfoVa + 0x0C);
            if (catchableArrRva == 0) return false;

            uint* arr = (uint*)(image + catchableArrRva);
            uint nTypes = arr[0];
            bool seenEEFamily = false;
            for (uint i = 0; i < nTypes && i < 8; i++)
            {
                uint ctRva = arr[1 + i];
                uint pTypeRva = *(uint*)(image + ctRva + 4);
                byte* mangledName = image + pTypeRva + 16;
                if (ContainsAscii(mangledName, "EETypeLoadException"))
                { *outHr = 0x80131522u; return true; }
                if (ContainsAscii(mangledName, "EEFileLoadException"))
                { *outHr = 0x80131621u; return true; }
                if (ContainsAscii(mangledName, "EEException") ||
                    ContainsAscii(mangledName, "CLRException"))
                    seenEEFamily = true;
            }
            if (seenEEFamily)
            {
                *outHr = 0x80004005u; // E_FAIL — generic fallback
                return true;
            }
            return false;
        }

        private static bool ContainsAscii(byte* hay, string needle)
        {
            int nlen = needle.Length;
            for (int i = 0; ; i++)
            {
                int j = 0;
                while (j < nlen && hay[i + j] == (byte)needle[j]) j++;
                if (j == nlen) return true;
                if (hay[i] == 0) return false;
            }
        }

        // Sanity check: a candidate Rip must be canonical and в our image
        // range. Walker stops when this fails.
        private static bool IsValidIp(ulong rip)
        {
            // Canonical x64: bits 47..63 must all match bit 47 (sign-extended).
            // 0xAFAF... pattern obviously not canonical.
            if (rip == 0) return false;
            if ((rip >> 48) != 0 && (rip >> 48) != 0xFFFF) return false;
            // Kernel image (static .pdata): Rip in [base, base+16 MiB).
            if (CoffRuntimeFunctionTable.IsInitialized)
            {
                ulong baseAddr = (ulong)CoffRuntimeFunctionTable.ImageBase;
                if (rip >= baseAddr && rip - baseAddr <= 0x10000000UL) return true;
            }
            // JIT code in a registered dynamic function-table region — unwind
            // info is resolvable via DynamicLookup (Step 1). Must NOT reject
            // it here, else the walker stops before consulting the registry.
            if (SehUnwind.InDynamicRange(rip)) return true;
            return false;
        }

        // Core dispatch loop: walk frames upward, call each frame's
        // personality routine, react to its return value.
        //
        // All large state lives in GcHeap (not stack): Context structs are
        // 1232 bytes each, three of them on stack would push us past the
        // kernel-stack budget into adjacent heap (see RaiseExceptionImpl
        // comment).
        private static void DispatchException(ExceptionRecord* rec, Context* ctx)
        {
            DispatcherContext* dc = (DispatcherContext*)GcHeap.AllocateRaw((uint)sizeof(DispatcherContext));
            Context* startCtx  = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
            Context* searchCtx = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
            Context* unwindCtx = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
            if (dc == null || startCtx == null || searchCtx == null || unwindCtx == null)
            {
                Console.WriteLine("[seh] dispatch GcHeap alloc failed");
                return;
            }
            // Copy starting context into startCtx (for rewind).
            byte* src = (byte*)ctx;
            byte* dst = (byte*)startCtx;
            for (int i = 0; i < sizeof(Context); i++) dst[i] = src[i];

            byte* image = CoffRuntimeFunctionTable.ImageBase;
            ulong imageBase = (ulong)image;

            // --- FIRST PASS: search for handler ---
            //
            // For each frame, ask its personality routine. If routine
            // returns ExecuteHandlerMarker, that's our catch — break out.
            byte* sdst = (byte*)searchCtx;
            for (int i = 0; i < sizeof(Context); i++) sdst[i] = src[i];
            ulong establisherFrame = 0;
            void* matchedHandler = null;
            HandlerType* matchedClause = null;
            ulong matchedTargetIp = 0;
            ulong matchedFrame = 0;

            int frameLimit = 64;
            bool isThrowSite = true;
            while (frameLimit-- > 0)
            {
                ulong rawRip = searchCtx->Rip;
                // For caller frames Rip = return address (instruction AFTER
                // the call). EH IP-to-state maps index by call instruction
                // itself, so use Rip-1 для lookup и handler invocation. The
                // throw-site frame (first iteration) IS the throwing
                // instruction, no adjustment.
                ulong controlPc = isThrowSite ? rawRip : rawRip - 1;
                isThrowSite = false;

                if (!IsValidIp(controlPc))
                {
                    Console.Write("[seh] invalid Rip=0x"); Console.WriteHex(controlPc);
                    Console.WriteLine(" — stop walk");
                    break;
                }
                ulong ib;
                RuntimeFunction* rf = SehUnwind.LookupFunctionEntry(controlPc, &ib);
                if (rf == null)
                {
                    Console.Write("[seh] walked out of image at Rip=0x");
                    Console.WriteHex(controlPc);
                    Console.WriteLine("");
                    break;
                }

                if (Trace) {
                Console.Write("[seh] search frame Rip=0x"); Console.WriteHex(controlPc);
                Console.Write(" Rsp=0x"); Console.WriteHex(searchCtx->Rsp);
                Console.WriteLine("");
                }

                void* handlerData = null;
                ulong newFrame = 0;
                void* personality = SehUnwind.VirtualUnwind(
                    UnwindFlags.UNW_FLAG_EHANDLER,
                    ib, rawRip, rf, searchCtx,
                    &handlerData, &newFrame);

                if (personality != null)
                {
                    if (Trace) { Console.Write("[seh]   personality=0x"); Console.WriteHex((ulong)personality); Console.WriteLine(""); }
                    dc->ControlPc = controlPc;
                    dc->ImageBase = ib;
                    dc->FunctionEntry = rf;
                    dc->EstablisherFrame = newFrame;
                    dc->TargetIp = 0;
                    dc->ContextRecord = searchCtx;
                    dc->LanguageHandler = personality;
                    dc->HandlerData = handlerData;

                    delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int> fn =
                        (delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int>)personality;
                    int disp = fn(rec, (void*)newFrame, searchCtx, dc);

                    if (disp == ExceptionDispositionExt.ExceptionExecuteHandlerMarker)
                    {
                        matchedHandler = personality;
                        matchedClause = (HandlerType*)dc->HandlerData;
                        matchedTargetIp = dc->TargetIp;
                        matchedFrame = newFrame;
                        if (Trace) {
                        Console.Write("[seh] match at frame 0x");
                        Console.WriteHex(newFrame);
                        Console.Write(" target=0x");
                        Console.WriteHex(matchedTargetIp);
                        Console.WriteLine("");
                        }
                        break;
                    }
                    if (Trace) { Console.Write("[seh]   disp="); Console.WriteInt(disp); Console.WriteLine(""); }
                }

                if (newFrame == 0 || newFrame == establisherFrame)
                {
                    // No progress — bail.
                    break;
                }
                establisherFrame = newFrame;
                if (searchCtx->Rip == 0) break;
            }

            if (matchedHandler == null)
            {
                // No C++/SEH handler caught the throw. For C++ HRException
                // throws CoreCLR expects the host to translate to HRESULT
                // return. Our caller is C# (CoreClrProbe.Run). Walk again
                // from startCtx and stop at the FIRST managed-section
                // frame (= our C# code right after coreclr_initialize
                // returned). Resume there with RAX = m_hr.
                //
                // ONLY for HRException — non-HRException C++ throws
                // (EETypeLoadException, std::bad_alloc, BadImageFormat, etc.)
                // don't have m_hr at offset 0x14 и read garbage. For those
                // we panic — instrumentation at the throw site (e.g.
                // ThrowTypeLoadException) should surface the payload before
                // it reaches us.
                if (rec->ExceptionCode == ExceptionRecord.EH_EXCEPTION_NUMBER &&
                    rec->NumberParameters >= 4 &&
                    IsHRExceptionThrow(rec))
                {
                    ulong objVa = rec->ExceptionInformation[1];
                    if (objVa != 0)
                    {
                        uint hr = *(uint*)(objVa + 0x14);

                        Context* resumeCtx = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
                        if (resumeCtx == null) return;
                        byte* rdst = (byte*)resumeCtx;
                        byte* rsrc = (byte*)startCtx;
                        for (int i = 0; i < sizeof(Context); i++) rdst[i] = rsrc[i];

                        // .managed section RVA range (NativeAOT-emitted C#
                        // methods). When walker enters this range we're in
                        // the host caller's frame — resume here.
                        const uint MANAGED_RVA_MIN = 0xCB5000;
                        const uint MANAGED_RVA_MAX = 0xCDE000;
                        bool firstWalk = true;
                        int limit = 64;
                        while (limit-- > 0)
                        {
                            ulong rip = resumeCtx->Rip;
                            ulong rva = rip - (ulong)CoffRuntimeFunctionTable.ImageBase;
                            if (!firstWalk && rva >= MANAGED_RVA_MIN && rva < MANAGED_RVA_MAX)
                                break;   // first managed frame — stop here
                            firstWalk = false;
                            ulong adjPc = rip - 1;
                            if (!IsValidIp(adjPc)) break;
                            ulong ib2;
                            RuntimeFunction* rf2 = SehUnwind.LookupFunctionEntry(adjPc, &ib2);
                            if (rf2 == null) break;
                            void* hd2 = null;
                            ulong ef2 = 0;
                            SehUnwind.VirtualUnwind(0, ib2, rip, rf2, resumeCtx, &hd2, &ef2);
                        }

                        if (Trace) {
                        Console.Write("[seh] uncaught HRException → resume managed Rip=0x");
                        Console.WriteHex(resumeCtx->Rip);
                        Console.Write(" Rsp=0x"); Console.WriteHex(resumeCtx->Rsp);
                        Console.Write(" hr=0x"); Console.WriteHex(hr);
                        Console.WriteLine("");
                        Console.Write("  Rbp=0x"); Console.WriteHex(resumeCtx->Rbp);
                        Console.Write(" Rbx=0x"); Console.WriteHex(resumeCtx->Rbx);
                        Console.Write(" Rsi=0x"); Console.WriteHex(resumeCtx->Rsi);
                        Console.Write(" Rdi=0x"); Console.WriteHex(resumeCtx->Rdi);
                        Console.WriteLine("");
                        Console.Write("  R12=0x"); Console.WriteHex(resumeCtx->R12);
                        Console.Write(" R13=0x"); Console.WriteHex(resumeCtx->R13);
                        Console.Write(" R14=0x"); Console.WriteHex(resumeCtx->R14);
                        Console.Write(" R15=0x"); Console.WriteHex(resumeCtx->R15);
                        Console.WriteLine("");
                        }
                        resumeCtx->Rax = hr;
                        RestoreContextAsm(resumeCtx);
                        return;  // unreachable
                    }
                }
                return;
            }

            // --- SECOND PASS: unwind to handler frame, running destructors. ---
            byte* udst = (byte*)unwindCtx;
            byte* ssrc = (byte*)startCtx;
            for (int i = 0; i < sizeof(Context); i++) udst[i] = ssrc[i];
            establisherFrame = 0;
            int unwindLimit = 64;
            bool isUnwindThrowSite = true;
            while (unwindLimit-- > 0)
            {
                ulong rawRipU = unwindCtx->Rip;
                ulong controlPc = isUnwindThrowSite ? rawRipU : rawRipU - 1;
                isUnwindThrowSite = false;
                if (!IsValidIp(controlPc)) break;
                ulong ib;
                RuntimeFunction* rf = SehUnwind.LookupFunctionEntry(controlPc, &ib);
                if (rf == null) break;

                void* handlerData = null;
                ulong newFrame = 0;
                void* personality = SehUnwind.VirtualUnwind(
                    UnwindFlags.UNW_FLAG_UHANDLER,
                    ib, rawRipU, rf, unwindCtx,
                    &handlerData, &newFrame);

                if (personality != null)
                {
                    rec->ExceptionFlags |= ExceptionRecord.EXCEPTION_UNWINDING;
                    if (newFrame == matchedFrame)
                        rec->ExceptionFlags |= ExceptionRecord.EXCEPTION_TARGET_UNWIND;

                    dc->ControlPc = controlPc;
                    dc->ImageBase = ib;
                    dc->FunctionEntry = rf;
                    dc->EstablisherFrame = newFrame;
                    dc->TargetIp = matchedTargetIp;
                    dc->ContextRecord = unwindCtx;
                    dc->LanguageHandler = personality;
                    dc->HandlerData = handlerData;

                    delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int> fn =
                        (delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int>)personality;
                    fn(rec, (void*)newFrame, unwindCtx, dc);
                }

                if (newFrame == matchedFrame)
                {
                    // Reached catching frame — set RIP к handler entry и resume.
                    unwindCtx->Rip = matchedTargetIp;
                    // Restore via asm helper. Never returns.
                    RestoreContextAsm(unwindCtx);
                    return;  // unreachable
                }

                if (newFrame == 0 || newFrame == establisherFrame) break;
                establisherFrame = newFrame;
                if (unwindCtx->Rip == 0) break;
            }
        }

        // Advance a freshly-captured context one frame so its Rip/Rsp
        // reflect the caller of RaiseException rather than RaiseException
        // itself.
        private static void UnwindOneFrame(Context* ctx)
        {
            ulong ib;
            RuntimeFunction* rf = SehUnwind.LookupFunctionEntry(ctx->Rip, &ib);
            if (rf == null) return;
            void* hd = null;
            ulong ef = 0;
            SehUnwind.VirtualUnwind(0, ib, ctx->Rip, rf, ctx, &hd, &ef);
        }

        // RtlUnwind — Windows second-pass unwinder. CoreCLR's ClrUnwindEx
        // (exceptionhandling.cpp, SHARPOS path) calls this once its
        // personality (managed funclet / __CxxFrameHandler3 /
        // ProcessCLRException) chose the target frame+IP. Unwinds from our
        // caller down to targetFrame, running each frame's
        // EXCEPTION_UNWINDING handlers (finally / fault / funclets), then
        // transfers control to targetIp with Rsp=targetFrame, Rax=
        // returnValue. Does not return. C#-side per CLAUDE.md invariant 1;
        // the fork CRT_STUB(RtlUnwind) is only the __imp_ alias (same
        // arrangement as RtlVirtualUnwind / RtlLookupFunctionEntry).
        [RuntimeExport("RtlUnwind")]
        [UnmanagedCallersOnly(EntryPoint = "RtlUnwind")]
        public static void RtlUnwind(void* targetFrame, void* targetIp,
                                     ExceptionRecord* excRec, void* returnValue)
        {
            DispatcherContext* dc = (DispatcherContext*)GcHeap.AllocateRaw((uint)sizeof(DispatcherContext));
            Context* uc = (Context*)GcHeap.AllocateRaw((uint)sizeof(Context));
            ExceptionRecord* rec = excRec;
            if (dc == null || uc == null) { Panic.Fail("RtlUnwind alloc"); return; }
            if (rec == null)
            {
                rec = (ExceptionRecord*)GcHeap.AllocateRaw((uint)sizeof(ExceptionRecord));
                if (rec == null) { Panic.Fail("RtlUnwind rec alloc"); return; }
                rec->ExceptionCode = 0xC0000027;   // STATUS_UNWIND
            }
            rec->ExceptionFlags |= ExceptionRecord.EXCEPTION_UNWINDING;

            CaptureCurrentContext(uc);
            UnwindOneFrame(uc);   // step out of RtlUnwind → caller (ClrUnwindEx)

            ulong target = (ulong)targetFrame;
            ulong establisher = 0;
            int limit = 64;
            while (limit-- > 0)
            {
                ulong rawRip = uc->Rip;
                ulong controlPc = rawRip - 1;
                if (!IsValidIp(controlPc)) break;
                ulong ib;
                RuntimeFunction* rf = SehUnwind.LookupFunctionEntry(controlPc, &ib);
                if (rf == null) break;

                void* handlerData = null;
                ulong newFrame = 0;
                void* personality = SehUnwind.VirtualUnwind(
                    UnwindFlags.UNW_FLAG_UHANDLER, ib, rawRip, rf, uc,
                    &handlerData, &newFrame);

                if (personality != null)
                {
                    if (newFrame == target)
                        rec->ExceptionFlags |= ExceptionRecord.EXCEPTION_TARGET_UNWIND;
                    dc->ControlPc = controlPc;
                    dc->ImageBase = ib;
                    dc->FunctionEntry = rf;
                    dc->EstablisherFrame = newFrame;
                    dc->TargetIp = (ulong)targetIp;
                    dc->ContextRecord = uc;
                    dc->LanguageHandler = personality;
                    dc->HandlerData = handlerData;
                    delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int> fn =
                        (delegate* unmanaged<ExceptionRecord*, void*, Context*, DispatcherContext*, int>)personality;
                    fn(rec, (void*)newFrame, uc, dc);
                }

                if (newFrame == target)
                {
                    uc->Rip = (ulong)targetIp;
                    uc->Rsp = target;
                    uc->Rax = (ulong)returnValue;
                    RestoreContextAsm(uc);
                    return;   // unreachable
                }
                if (newFrame == 0 || newFrame == establisher) break;
                establisher = newFrame;
                if (uc->Rip == 0) break;
            }

            Console.WriteLine("[RtlUnwind] target frame not reached — HALT");
            Panic.Fail("RtlUnwind: target not found");
        }

        // Helpers backed by AsmExecBuffer shellcode (emitted on first use):
        //   CaptureCurrentContext — like RtlCaptureContext, фиксирует CONTEXT
        //                            у точки вызова
        //   RestoreContextAsm — load all regs из CONTEXT и jmp Rip (no return)

        private static void CaptureCurrentContext(Context* ctx)
        {
            EnsureShellcode();
            s_capture(ctx);
        }

        private static void RestoreContextAsm(Context* ctx)
        {
            EnsureShellcode();
            s_restore(ctx);
            // unreachable
        }

        private static delegate* unmanaged<Context*, void> s_capture;
        private static delegate* unmanaged<Context*, void> s_restore;
        private static bool s_shellcodeReady;

        // Emits "capture context" and "restore context" shellcode into
        // BootInfo.AsmExecBuffer. Layout: capture at offset 0x80, restore
        // at offset 0x100 (past X64Asm STI/CLI/HLT 0..63 and GS-base
        // wrmsr stub from CoreClrProbe at 64..82).
        private static void EnsureShellcode()
        {
            if (s_shellcodeReady) return;
            BootInfo bi = Platform.GetBootInfo();
            byte* buf = (byte*)bi.AsmExecBuffer;
            if (buf == null || bi.AsmExecBufferSize < 0x200) return;

            // Layout: STI/CLI/HLT 0..63, wrmsr GS_BASE 64..82,
            // capture shellcode at 0x80 (~140 bytes → 0x10C),
            // restore shellcode at 0x200 (~140 bytes → 0x28C).
            // Buffer is 1024 — comfortably fits with no overlap.
            byte* capCode = buf + 0x80;
            EmitCapture(capCode);
            byte* resCode = buf + 0x200;
            EmitRestore(resCode);

            s_capture = (delegate* unmanaged<Context*, void>)capCode;
            s_restore = (delegate* unmanaged<Context*, void>)resCode;
            s_shellcodeReady = true;
        }

        // Capture context shellcode: same idea как RtlCaptureContext в fork's
        // crt_imp_stubs.cpp. RCX = ctx ptr (Win64 arg1).
        //   mov [rcx+0x30], 0x100003  ; ContextFlags
        //   mov [rcx+0x78], rax
        //   ... все GP regs ...
        //   mov rax, [rsp]            ; return addr → Rip
        //   mov [rcx+0xF8], rax
        //   lea rax, [rsp+8]
        //   mov [rcx+0x98], rax       ; caller's Rsp
        //   pushfq; pop rax; mov [rcx+0x44], eax
        //   ret
        private static void EmitCapture(byte* p)
        {
            int o = 0;
            // movl $0x100003, 0x30(%rcx)
            p[o++] = 0xC7; p[o++] = 0x41; p[o++] = 0x30;
            p[o++] = 0x03; p[o++] = 0x00; p[o++] = 0x10; p[o++] = 0x00;
            // mov %rax, 0x78(%rcx)
            EmitMovRegToCtx(p, ref o, 0x00 /*rax*/, 0x78);
            // mov %rcx, 0x80(%rcx) — note caller's RCX is gone (arg)
            EmitMovRegToCtx(p, ref o, 0x01 /*rcx*/, 0x80);
            EmitMovRegToCtx(p, ref o, 0x02 /*rdx*/, 0x88);
            EmitMovRegToCtx(p, ref o, 0x03 /*rbx*/, 0x90);
            EmitMovRegToCtx(p, ref o, 0x05 /*rbp*/, 0xA0);
            EmitMovRegToCtx(p, ref o, 0x06 /*rsi*/, 0xA8);
            EmitMovRegToCtx(p, ref o, 0x07 /*rdi*/, 0xB0);
            EmitMovRegToCtx(p, ref o, 0x08 /*r8*/,  0xB8);
            EmitMovRegToCtx(p, ref o, 0x09 /*r9*/,  0xC0);
            EmitMovRegToCtx(p, ref o, 0x0A /*r10*/, 0xC8);
            EmitMovRegToCtx(p, ref o, 0x0B /*r11*/, 0xD0);
            EmitMovRegToCtx(p, ref o, 0x0C /*r12*/, 0xD8);
            EmitMovRegToCtx(p, ref o, 0x0D /*r13*/, 0xE0);
            EmitMovRegToCtx(p, ref o, 0x0E /*r14*/, 0xE8);
            EmitMovRegToCtx(p, ref o, 0x0F /*r15*/, 0xF0);
            // mov rax, [rsp]
            p[o++] = 0x48; p[o++] = 0x8B; p[o++] = 0x04; p[o++] = 0x24;
            // mov [rcx+0xF8], rax
            EmitMovRegToCtx(p, ref o, 0x00 /*rax*/, 0xF8);
            // lea rax, [rsp+8]
            p[o++] = 0x48; p[o++] = 0x8D; p[o++] = 0x44; p[o++] = 0x24; p[o++] = 0x08;
            // mov [rcx+0x98], rax
            EmitMovRegToCtx(p, ref o, 0x00 /*rax*/, 0x98);
            // pushfq; pop rax
            p[o++] = 0x9C;
            p[o++] = 0x58;
            // mov [rcx+0x44], eax
            p[o++] = 0x89; p[o++] = 0x41; p[o++] = 0x44;
            // ret
            p[o++] = 0xC3;
        }

        // mov [rcx + disp8 or disp32], r64.
        // REX.W (0x48 / 0x4C) + 0x89 + ModR/M.
        // For reg in [rax..rdi] (0..7): REX = 0x48, opcode = 0x89, modrm = 0b01_rrr_001 (mode 01, rm=rcx).
        // For reg in [r8..r15] (8..15): REX = 0x4C, same.
        private static void EmitMovRegToCtx(byte* p, ref int o, int reg, int disp)
        {
            byte rex = (byte)((reg >= 8) ? 0x4C : 0x48);
            int regLow = reg & 0x7;
            p[o++] = rex;
            p[o++] = 0x89;
            if (disp <= 0x7F)
            {
                p[o++] = (byte)(0x40 | (regLow << 3) | 0x01); // mod=01 disp8, rm=001 (rcx)
                p[o++] = (byte)disp;
            }
            else
            {
                p[o++] = (byte)(0x80 | (regLow << 3) | 0x01); // mod=10 disp32, rm=001
                p[o++] = (byte)(disp);
                p[o++] = (byte)(disp >> 8);
                p[o++] = (byte)(disp >> 16);
                p[o++] = (byte)(disp >> 24);
            }
        }

        // Restore context shellcode. RCX = ctx ptr.
        //   mov rax, [rcx+0xF8]    ; target Rip
        //   mov rdx, [rcx+0x98]    ; target Rsp
        //   mov [rdx-8], rax       ; pre-place Rip just below new SP
        //   restore all GP regs except Rcx (used as ptr), Rsp (handled)
        //   restore Rcx from ctx, last
        //   mov rsp, [rcx+0x98]
        //   sub rsp, 8
        //   ret                    ; jumps к pre-placed Rip
        private static void EmitRestore(byte* p)
        {
            int o = 0;
            // Pre-place return target so we can use rsp's `ret` to transfer.
            // mov rax, [rcx+0xF8]   ; Rip
            EmitMovCtxToReg(p, ref o, 0x00 /*rax*/, 0xF8);
            // mov rdx, [rcx+0x98]   ; new Rsp
            EmitMovCtxToReg(p, ref o, 0x02 /*rdx*/, 0x98);
            // mov [rdx-8], rax       ; store target Rip там, куда `ret` его прочтёт после rsp-8
            p[o++] = 0x48; p[o++] = 0x89; p[o++] = 0x42; p[o++] = 0xF8;
            // Restore EFlags through pushfq trick.
            // mov eax, [rcx+0x44] ; new EFlags
            p[o++] = 0x8B; p[o++] = 0x41; p[o++] = 0x44;
            // push rax; popfq
            p[o++] = 0x50;
            p[o++] = 0x9D;
            // Restore GP regs (NOT rcx — it's our ctx ptr, NOT rax — used; NOT rsp/rip — handled).
            EmitMovCtxToReg(p, ref o, 0x03 /*rbx*/, 0x90);
            EmitMovCtxToReg(p, ref o, 0x05 /*rbp*/, 0xA0);
            EmitMovCtxToReg(p, ref o, 0x06 /*rsi*/, 0xA8);
            EmitMovCtxToReg(p, ref o, 0x07 /*rdi*/, 0xB0);
            EmitMovCtxToReg(p, ref o, 0x08 /*r8*/,  0xB8);
            EmitMovCtxToReg(p, ref o, 0x09 /*r9*/,  0xC0);
            EmitMovCtxToReg(p, ref o, 0x0A /*r10*/, 0xC8);
            EmitMovCtxToReg(p, ref o, 0x0B /*r11*/, 0xD0);
            EmitMovCtxToReg(p, ref o, 0x0C /*r12*/, 0xD8);
            EmitMovCtxToReg(p, ref o, 0x0D /*r13*/, 0xE0);
            EmitMovCtxToReg(p, ref o, 0x0E /*r14*/, 0xE8);
            EmitMovCtxToReg(p, ref o, 0x0F /*r15*/, 0xF0);
            // mov rdx, [rcx+0x88]
            EmitMovCtxToReg(p, ref o, 0x02 /*rdx*/, 0x88);
            // mov rax, [rcx+0x78]
            EmitMovCtxToReg(p, ref o, 0x00 /*rax*/, 0x78);
            // Switch RSP last (so the new stack now holds the pre-placed Rip).
            // Use the helper so disp 0x98 gets encoded as disp32 — hand-rolled
            // disp8 byte 0x98 would sign-extend to -0x68 and load garbage.
            EmitMovCtxToReg(p, ref o, 0x04 /*rsp*/, 0x98);
            // sub rsp, 8 (back into the slot where we stored Rip)
            p[o++] = 0x48; p[o++] = 0x83; p[o++] = 0xEC; p[o++] = 0x08;
            // mov rcx, [rcx+0x80]   ; finally restore Rcx
            EmitMovCtxToReg(p, ref o, 0x01 /*rcx*/, 0x80);
            // ret    — pops [rsp] (= our pre-placed target Rip) into Rip
            p[o++] = 0xC3;
        }

        // mov reg, [rcx + disp].
        // REX.W (0x48 / 0x4C) + 0x8B + ModR/M.
        private static void EmitMovCtxToReg(byte* p, ref int o, int reg, int disp)
        {
            byte rex = (byte)((reg >= 8) ? 0x4C : 0x48);
            int regLow = reg & 0x7;
            p[o++] = rex;
            p[o++] = 0x8B;
            if (disp <= 0x7F)
            {
                p[o++] = (byte)(0x40 | (regLow << 3) | 0x01);
                p[o++] = (byte)disp;
            }
            else
            {
                p[o++] = (byte)(0x80 | (regLow << 3) | 0x01);
                p[o++] = (byte)(disp);
                p[o++] = (byte)(disp >> 8);
                p[o++] = (byte)(disp >> 16);
                p[o++] = (byte)(disp >> 24);
            }
        }
    }
}
