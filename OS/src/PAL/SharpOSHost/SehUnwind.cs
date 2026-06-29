using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot.EH;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // Phase 2 + 3 of SEH unwind: RtlLookupFunctionEntry + RtlVirtualUnwind.
    //
    // RtlLookupFunctionEntry — given an instruction pointer, find the
    // matching .pdata entry. Already implemented inside CoffMethodLookup
    // (binary search в RUNTIME_FUNCTION array sorted by BeginAddress);
    // this wraps it под Windows ABI.
    //
    // RtlVirtualUnwind — given a function entry + current CONTEXT, apply
    // the function's UNWIND_INFO opcodes to walk one frame upward. After
    // the call: ContextRecord->Rip = caller's return address, Rsp = caller
    // SP, callee-saved regs restored from where they were spilled in
    // prolog. Returns pointer к personality routine if the function has
    // EHANDLER/UHANDLER flag set.
    //
    // Layout per AMD64 ABI (winnt.h UNWIND_INFO + UNWIND_CODE):
    //
    //   UNWIND_INFO:
    //     +0x00 [byte] Version (low 3 bits) | Flags (high 5 bits)
    //     +0x01 [byte] SizeOfProlog
    //     +0x02 [byte] CountOfCodes
    //     +0x03 [byte] FrameRegister (low 4 bits) | FrameOffset (high 4 bits)
    //     +0x04 [array of UNWIND_CODE: 2 bytes each, count = CountOfCodes,
    //            but each code may span multiple slots]
    //     [aligned to DWORD]
    //     if EHANDLER|UHANDLER: ExceptionHandler RVA (4 bytes)
    //                           ExceptionData[...] (language-specific)
    //     if CHAININFO: chained RUNTIME_FUNCTION (12 bytes)
    //
    //   UNWIND_CODE:
    //     +0x00 [byte] OffsetInProlog  — codes only apply if prolog has
    //                                    progressed past this offset.
    //     +0x01 [byte] UnwindOp (low 4 bits) | OpInfo (high 4 bits)
    //
    // For "virtual unwind" (i.e. from inside the body, not mid-prolog),
    // OffsetInProlog is ignored — apply all codes in forward order.
    internal static unsafe class SehUnwind
    {
        // step 89 sec11 cheap detector -- per-opcode trace inside
        // ApplyUnwindInfo (header / each code / finalize) plus first 48
        // bytes of function body for identification by prolog signature.
        // ILC dead-codes when false. Pair with SehDispatch.Trace=true
        // for the per-frame bracket lines. Used in step-89 to identify
        // CallDescrWorkerInternal as the sec11 kill frame; root is now
        // understood (FrameChain integration), tier stays as scaffolding
        // for the actual Phase D fix.
        private const bool TraceUnwind = false;

        // step124: targeted unwind tracing — fires only for specified PCs to
        // avoid flooding. Set TraceUnwindPc1/Pc2 to RIPs of interest, then
        // s_traceThisCall is computed at VirtualUnwind entry.
        // For step124 we focus on Null object.ToString catchability path:
        //   Pc1 = 0x500008628C91 — original throw IP (in JIT'd probe lambda)
        //   Pc2 = 0x5000085D1E70 — resume PC after catch (continuation)
        private const ulong TraceUnwindPc1 = 0x500008628C91UL;
        private const ulong TraceUnwindPc2 = 0x5000085D1E70UL;
        // ±range bytes around each — covers funclet calls and resume-frame
        // walks where controlPc may be slightly shifted from above.
        private const ulong TraceUnwindRange = 0x40UL;
        private static bool s_traceThisCall;

        // Windows-API-shaped lookup. Returns pointer to RUNTIME_FUNCTION
        // (in our .pdata array) or null if IP isn't в our image.
        //
        // pImageBase out-param: caller uses for unwind-info RVA math.
        // pHistoryTable: unused (optional optimization slot).
        [RuntimeExport("RtlLookupFunctionEntry")]
        [UnmanagedCallersOnly(EntryPoint = "RtlLookupFunctionEntry")]
        public static RuntimeFunction* RtlLookupFunctionEntry(
            ulong controlPc, ulong* pImageBase, void* pHistoryTable)
            => LookupFunctionEntry(controlPc, pImageBase);

        public static RuntimeFunction* LookupFunctionEntry(
            ulong controlPc, ulong* pImageBase)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized)
                return null;

            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            if (pImageBase != null) *pImageBase = (ulong)imageBase;

            // Use existing binary search infra. Convert IP → byte* anchor.
            if (!CoffMethodLookup.TryFindMethod((byte*)controlPc, out var info))
            {
                // 1) JIT code-heap callback range (RtlInstallFunctionTableCallback)
                RuntimeFunction* dyn = DynamicLookup(controlPc, pImageBase);
                if (dyn != null) return dyn;
                // 2) R2R image static .pdata (RtlAddFunctionTable) — e.g.
                //    System.Private.CoreLib precompiled code mapped into the
                //    VM window. peimagelayout.cpp registers it under SHARPOS.
                RuntimeFunction* stat = StaticTableLookup(controlPc, pImageBase);
                if (stat != null) return stat;
                // 3) Stub heap (Phase E10 Path B): LoaderAllocator's precode /
                //    call-counting / VSD / dynamic-helper heaps. CoreCLR
                //    doesn't emit .pdata for these — they're leaf-style
                //    thunks (call helper; ret). Return a synthetic leaf
                //    RUNTIME_FUNCTION so the unwinder pops [rsp] and steps
                //    past the thunk to the caller (JIT method).
                RuntimeFunction* stub = StubRangeLookup(controlPc, pImageBase);
                if (stub != null) return stub;
                // 4) Image-text leaf thunk: linker emits frameless trampolines
                //    (typically `mov rcx, [rcx]; jmp [slot]` import/delay-load
                //    helpers, 10 bytes) between real functions WITHOUT .pdata
                //    entries. They're leaves — no prologue, no frame. Windows
                //    SEH walker treats no-pdata RIP inside an image text range
                //    as a frameless leaf (pop RA, advance RSP). We mirror that:
                //    synthesize the same leaf RUNTIME_FUNCTION as StubRange.
                //    Surfaced by `[ivip]` diagnostic in step 123 census on
                //    Release fork (Debug had different code shape due to ICF).
                return ImageTextGapLookup(controlPc, pImageBase);
            }

            // CoffMethodLookup returns CurrentRuntimeFunction (which may be
            // a funclet inside the method). For Windows ABI, that IS what
            // RtlLookupFunctionEntry returns — each .pdata entry is a
            // distinct "function" в RUNTIME_FUNCTION sense. Funclet/parent
            // distinction matters только to personality routines.
            return (RuntimeFunction*)info.CurrentRuntimeFunction;
        }

        // ─── Dynamic JIT function tables (Step 1: JIT-frame SEH unwind) ──
        // CoreCLR's code-heap manager calls RtlInstallFunctionTableCallback
        // for each JIT code region — those methods have NO static .pdata.
        // The fork stub (crt_imp_stubs.cpp) forwards here. On an SEH walk,
        // LookupFunctionEntry first tries the kernel image .pdata
        // (CoffMethodLookup); if controlPc is in a registered JIT region we
        // invoke CoreCLR's GET_RUNTIME_FUNCTION_CALLBACK to synthesize the
        // RUNTIME_FUNCTION (RVAs relative to that region base) so
        // RtlVirtualUnwind can step the JIT frame like any native one.
        //
        // Storage: value-type static + fixed buffer — NO managed alloc, NO
        // static-ref initializer (ClassConstructorRunner trap). 64 regions
        // max (JIT code heaps are few); 4 ulongs/entry: base,len,cb,ctx.
        private const int DynMax = 64;
        private struct DynTab { public fixed ulong S[DynMax * 4]; }
        private static DynTab s_dyn;
        private static int s_dynCount;

        [RuntimeExport("SharpOSHost_RegisterFunctionTableCallback")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegisterFunctionTableCallback")]
        public static void RegisterFunctionTableCallback(
            ulong baseAddr, uint length, void* callback, void* context)
        {
            if (callback == null || length == 0) return;
            int i = s_dynCount;
            if (i >= DynMax) return;                  // registry full — unexpected
            fixed (ulong* p = s_dyn.S)
            {
                ulong* e = p + (i * 4);
                e[0] = baseAddr;
                e[1] = length;
                e[2] = (ulong)callback;
                e[3] = (ulong)context;
            }
            s_dynCount = i + 1;
        }

        // True if pc falls in any registered JIT code region. Used by the
        // SEH walker's IsValidIp so it doesn't reject JIT frames before
        // LookupFunctionEntry/DynamicLookup gets a chance.
        public static bool InDynamicRange(ulong pc)
        {
            int n = s_dynCount;
            fixed (ulong* p = s_dyn.S)
            {
                for (int i = 0; i < n; i++)
                {
                    ulong* e = p + (i * 4);
                    ulong b = e[0];
                    if (pc >= b && pc < b + e[1]) return true;
                }
            }
            // Static R2R .pdata regions OR stub heaps (leaf-unwind synthetic).
            return InStaticRange(pc) || InStubRange(pc);
        }

        // controlPc in a registered JIT region → invoke CoreCLR's callback
        // (GET_RUNTIME_FUNCTION_CALLBACK: PRUNTIME_FUNCTION(DWORD64,PVOID)).
        // *pImageBase ← region base (returned entry's RVAs are relative
        // to it, so RtlVirtualUnwind's RVA math stays correct).
        private static RuntimeFunction* DynamicLookup(ulong controlPc, ulong* pImageBase)
        {
            int n = s_dynCount;
            fixed (ulong* p = s_dyn.S)
            {
                for (int i = 0; i < n; i++)
                {
                    ulong* e = p + (i * 4);
                    ulong b = e[0];
                    ulong len = e[1];
                    if (controlPc < b || controlPc >= b + len) continue;
                    var cb = (delegate* unmanaged<ulong, void*, RuntimeFunction*>)e[2];
                    RuntimeFunction* rf = cb(controlPc, (void*)e[3]);
                    if (rf == null) return null;
                    if (pImageBase != null) *pImageBase = b;
                    return rf;
                }
            }
            return null;
        }

        // ─── Static R2R function tables (RtlAddFunctionTable) ───────────
        // peimagelayout.cpp (re-enabled under TARGET_SHARPOS) registers a
        // loaded R2R image's static .pdata: a sorted RUNTIME_FUNCTION[]
        // whose RVAs are relative to the image base. The fork
        // RtlAddFunctionTable forwards here. Without this, unwinding R2R
        // CoreLib code (mapped into the VM window, no kernel-image .pdata,
        // no JIT callback) fails → "invalid Rip" → unhandled C++ exception.
        // Layout: 3 ulongs/entry: base, funcTablePtr, count. Value-type
        // static + fixed buffer — no managed alloc, no cctor (CCR trap).
        private const int StatMax = 64;
        private struct StatTab { public fixed ulong S[StatMax * 3]; }
        private static StatTab s_stat;
        private static int s_statCount;

        [RuntimeExport("SharpOSHost_RegisterStaticFunctionTable")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegisterStaticFunctionTable")]
        public static void RegisterStaticFunctionTable(
            ulong baseAddr, void* funcTable, uint count)
        {
            if (funcTable == null || count == 0) return;
            int i = s_statCount;
            if (i >= StatMax) return;
            fixed (ulong* p = s_stat.S)
            {
                ulong* e = p + (i * 3);
                e[0] = baseAddr;
                e[1] = (ulong)funcTable;
                e[2] = count;
            }
            s_statCount = i + 1;
        }

        // True if pc is inside any registered static R2R table's span
        // [base + first.BeginAddress, base + last.EndAddress). Folded into
        // the IsValidIp path via InDynamicRange so the walker doesn't
        // reject R2R frames before StaticTableLookup runs.
        private static bool InStaticRange(ulong pc)
        {
            int n = s_statCount;
            fixed (ulong* p = s_stat.S)
            {
                for (int i = 0; i < n; i++)
                {
                    ulong* e = p + (i * 3);
                    ulong b = e[0];
                    var f = (RuntimeFunction*)e[1];
                    uint c = (uint)e[2];
                    if (c == 0 || pc < b) continue;
                    ulong rva = pc - b;
                    if (rva >= f[0].BeginAddress && rva < f[c - 1].EndAddress)
                        return true;
                }
            }
            return false;
        }

        // ─── Stub heaps (Phase E10 Path B) ──────────────────────────────────
        // LoaderAllocator stub heaps (m_pStubHeap, m_pNewStubPrecodeHeap,
        // m_pDynamicHelpersHeap, VSD stubs, m_pExecutableHeap) hold tiny
        // thunks that CoreCLR emits at runtime but never registers with
        // RtlInstallFunctionTableCallback. The thunks are leaf-style
        // (typically `call helper; ret` — see CallCountingHelperFrame
        // path through HELPER_METHOD_FRAME). We register these heaps as
        // "leaf-unwind ranges": any RIP in such a range gets a synthetic
        // RUNTIME_FUNCTION + UNWIND_INFO with countOfCodes=0, so
        // VirtualUnwind just pops [rsp] into Rip and steps past the
        // thunk to the caller (JIT method or another stub).
        //
        // This mirrors the Windows-CoreCLR architecture where each code
        // heap is covered by GetRuntimeFunctionCallback which synthesizes
        // RUNTIME_FUNCTIONs on demand, paired with personality routines
        // that consult Thread::m_pFrame. Our walker already activates
        // the Frame chain (TryActivateFrameChain in SehDispatch); the
        // missing piece was unwinding through the thunk that comes
        // immediately after the Frame is popped — that's what this
        // synthetic leaf entry provides.
        // 1024 entries x 2 ulongs (base, len) = 16 KiB. Each VM-level reserve
        // adds one entry; budget covers many JIT/stub heaps + GC arena +
        // duplicate registrations from fork-side hooks (defense in depth).
        private const int StubMax = 1024;
        private struct StubTab { public fixed ulong S[StubMax * 2]; }
        private static StubTab s_stubs;
        private static int s_stubCount;

        // Synthetic leaf metadata. UNWIND_INFO is 4 zero bytes
        // (Version=0/Flags=0/SizeOfProlog=0/CountOfCodes=0/FrameRegister=0);
        // ApplyUnwindInfo reads countOfCodes=0 → no codes → pops [rsp] →
        // Rip = next return address, Rsp += 8. RUNTIME_FUNCTION has
        // BeginAddress=0, EndAddress=0x7FFFFFFF (covers any RVA), and
        // UnwindInfoAddress=0 — combined with our pImageBase trick
        // (set to address of s_leafUw) this makes
        // unwindInfo = imageBase + UnwindInfoAddress = &s_leafUw.
        private struct LeafUw
        {
            public byte B0, B1, B2, B3;
        }
        private static LeafUw s_leafUw;
        private static RuntimeFunction s_leafRf;
        private static bool s_leafReady;

        // Managed entry point — callable from C# (e.g. VirtualMemory.Reserve
        // registers every JIT-VA reserve here to ensure 100% coverage even
        // for paths that bypass the fork's ExecutableAllocator::Reserve).
        public static void RegisterStubRange(ulong baseAddr, ulong length)
        {
            if (baseAddr == 0 || length == 0) return;
            // Phase E10 path-B diagnostic: NOT gated by Verbose — proves
            // registration is reached. Remove after acceptance verified.
            Console.Write("[stub-reg] #"); Console.WriteInt(s_stubCount);
            Console.Write(" base=0x");   Console.WriteHex(baseAddr);
            Console.Write(" len=0x");    Console.WriteHex(length);
            Console.WriteLine("");
            int i = s_stubCount;
            if (i >= StubMax) return;
            fixed (ulong* p = s_stubs.S)
            {
                ulong* e = p + (i * 2);
                e[0] = baseAddr;
                e[1] = length;
            }
            s_stubCount = i + 1;
        }

        // Unmanaged entry point — called from fork-side
        // SharpOSRegisterStubHeap wrapper (crt_imp_stubs.cpp) which hooks
        // ExecutableAllocator::Reserve + AllocateThunksFromTemplate +
        // ReserveWithinRange + ReserveAt. Redundant against the VM-level
        // catch but kept for defense-in-depth.
        [RuntimeExport("SharpOSHost_RegisterStubRange")]
        [UnmanagedCallersOnly(EntryPoint = "SharpOSHost_RegisterStubRange")]
        public static void RegisterStubRangeExport(ulong baseAddr, ulong length)
            => RegisterStubRange(baseAddr, length);

        private static bool InStubRange(ulong pc)
        {
            int n = s_stubCount;
            fixed (ulong* p = s_stubs.S)
            {
                for (int i = 0; i < n; i++)
                {
                    ulong* e = p + (i * 2);
                    ulong b = e[0];
                    if (pc >= b && pc < b + e[1]) return true;
                }
            }
            return false;
        }

        // Public predicate for IsValidIp — same range check as ImageTextGap-
        // Lookup but without synthesizing the leaf RF. Returns true when
        // controlPc sits inside the kernel image .pdata span (first .Begin ..
        // last .End) AND isn't covered by any explicit RUNTIME_FUNCTION (caller
        // should have already checked CoffMethodLookup.TryFindMethod).
        public static bool IsImageTextGap(ulong controlPc)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return false;
            int count = CoffRuntimeFunctionTable.Count;
            if (count <= 0) return false;
            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            nint diff = (nint)controlPc - (nint)imageBase;
            if (diff < 0 || (ulong)diff > 0xFFFFFFFFUL) return false;
            uint rva = (uint)(ulong)diff;
            global::OS.Boot.EH.RuntimeFunction* first = CoffRuntimeFunctionTable.GetRecord(0);
            global::OS.Boot.EH.RuntimeFunction* last  = CoffRuntimeFunctionTable.GetRecord(count - 1);
            if (first == null || last == null) return false;
            return rva >= first->BeginAddress && rva < last->EndAddress;
        }

        private static bool s_didImageGapProbe;

        // controlPc fell into a gap between two kernel-image .pdata entries
        // (linker-emitted frameless thunk, e.g. import/delay-load `mov rcx,[rcx];
        //  jmp [slot]`). No prologue, no frame → synthesize a leaf RF, identical
        // shape to StubRangeLookup. Guard: must be strictly inside the first..
        // last .pdata RVA span so we don't accept arbitrary heap/data addresses.
        private static RuntimeFunction* ImageTextGapLookup(ulong controlPc, ulong* pImageBase)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized) return null;
            int count = CoffRuntimeFunctionTable.Count;
            if (count <= 0) return null;
            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            nint diff = (nint)controlPc - (nint)imageBase;
            if (diff < 0 || (ulong)diff > 0xFFFFFFFFUL) return null;
            uint rva = (uint)(ulong)diff;
            // Span check: first entry's Begin .. last entry's End. Anything
            // outside is NOT a code thunk, treat as bad IP.
            global::OS.Boot.EH.RuntimeFunction* first = CoffRuntimeFunctionTable.GetRecord(0);
            global::OS.Boot.EH.RuntimeFunction* last  = CoffRuntimeFunctionTable.GetRecord(count - 1);
            if (first == null || last == null) return null;
            if (rva < first->BeginAddress || rva >= last->EndAddress) return null;

            // One-shot trace: matches mean we ACCEPTED a gap thunk; the surrounding
            // .pdata entries tell us whether it's a real tight thunk gap (~18 bytes,
            // adjustor / delay-load) or an oversize match suggesting our span check
            // is too permissive (catching arbitrary data).
            if (!s_didImageGapProbe)
            {
                s_didImageGapProbe = true;
                int idx = -1;
                uint bestBeg = 0;
                for (int i = 0; i < count; i++)
                {
                    var r = CoffRuntimeFunctionTable.GetRecord(i);
                    if (r == null) continue;
                    if (r->BeginAddress <= rva && r->BeginAddress > bestBeg)
                    { bestBeg = r->BeginAddress; idx = i; }
                }
                Console.Write("[gaplkup] controlPc=0x"); Console.WriteHex(controlPc);
                Console.Write(" rva=0x"); Console.WriteHex(rva);
                if (idx >= 0)
                {
                    var pr = CoffRuntimeFunctionTable.GetRecord(idx);
                    Console.Write(" prev[0x"); Console.WriteHex((ulong)idx);
                    Console.Write("]=0x"); Console.WriteHex(pr->BeginAddress);
                    Console.Write("..0x"); Console.WriteHex(pr->EndAddress);
                    Console.Write(" (gap="); Console.WriteHex(rva - pr->EndAddress); Console.Write(")");
                }
                if (idx + 1 < count)
                {
                    var nr = CoffRuntimeFunctionTable.GetRecord(idx + 1);
                    Console.Write(" next=0x"); Console.WriteHex(nr->BeginAddress);
                    Console.Write("..0x"); Console.WriteHex(nr->EndAddress);
                    Console.Write(" (gap="); Console.WriteHex(nr->BeginAddress - rva); Console.Write(")");
                }
                Console.WriteLine("");
            }
            // Reuse same synthetic leaf as StubRangeLookup.
            if (!s_leafReady)
            {
                s_leafRf.BeginAddress = 0;
                s_leafRf.EndAddress = 0x7FFFFFFF;
                s_leafRf.UnwindInfoAddress = 0;
                s_leafReady = true;
            }
            if (pImageBase != null)
            {
                *pImageBase = (ulong)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_leafUw);
            }
            return (RuntimeFunction*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_leafRf);
        }

        private static RuntimeFunction* StubRangeLookup(ulong controlPc, ulong* pImageBase)
        {
            if (!InStubRange(controlPc)) return null;
            // Lazy-init leaf metadata. UNWIND_INFO bytes stay zero; only
            // RUNTIME_FUNCTION needs EndAddress=large (zero would fail any
            // mid-prolog check and confuse callers that compute address
            // ranges from it).
            if (!s_leafReady)
            {
                s_leafRf.BeginAddress = 0;
                s_leafRf.EndAddress = 0x7FFFFFFF;
                s_leafRf.UnwindInfoAddress = 0;
                s_leafReady = true;
            }
            // Static value-type field addresses are stable (image .data,
            // never moves); Unsafe.AsPointer hands out a raw pointer
            // without the GC-pinning that `fixed` is for.
            if (pImageBase != null)
            {
                *pImageBase = (ulong)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_leafUw);
            }
            return (RuntimeFunction*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_leafRf);
        }

        // controlPc in a registered R2R image → binary-search its sorted
        // RUNTIME_FUNCTION[] (RVAs relative to base) and return the entry;
        // *pImageBase ← image base for RtlVirtualUnwind's RVA math.
        private static RuntimeFunction* StaticTableLookup(ulong controlPc, ulong* pImageBase)
        {
            int n = s_statCount;
            fixed (ulong* p = s_stat.S)
            {
                for (int i = 0; i < n; i++)
                {
                    ulong* e = p + (i * 3);
                    ulong b = e[0];
                    var f = (RuntimeFunction*)e[1];
                    int c = (int)e[2];
                    if (c == 0 || controlPc < b) continue;
                    ulong rvaU = controlPc - b;
                    if (rvaU >= f[c - 1].EndAddress || rvaU < f[0].BeginAddress) continue;
                    uint rva = (uint)rvaU;
                    int lo = 0, hi = c - 1;
                    while (lo <= hi)
                    {
                        int mid = lo + ((hi - lo) >> 1);
                        if (rva < f[mid].BeginAddress) hi = mid - 1;
                        else if (rva >= f[mid].EndAddress) lo = mid + 1;
                        else
                        {
                            if (pImageBase != null) *pImageBase = b;
                            return &f[mid];
                        }
                    }
                }
            }
            return null;
        }

        // RtlVirtualUnwind — applies one function's unwind codes to a
        // CONTEXT. After return:
        //   Context->Rip = caller's return address
        //   Context->Rsp = caller's SP (= our SP + frame size + 8 for ret)
        //   Context->R*  = restored from where prolog saved them
        //
        // Args:
        //   handlerType  — UNW_FLAG_EHANDLER if walking exception search,
        //                  UNW_FLAG_UHANDLER if walking unwind dispatch,
        //                  0 если просто frame-walk (no handler call needed)
        //   imageBase    — base of PE image (for RVA → VA math)
        //   controlPc    — current IP (for funclet/prolog awareness)
        //   functionEntry — .pdata entry returned by RtlLookupFunctionEntry
        //   context      — input/output: register state
        //   handlerData  — out: language-specific data ptr after personality RVA
        //   establisherFrame — out: caller SP (after frame teardown)
        //
        // Returns: personality routine pointer if function has E/U handler
        //          and handlerType matches; null otherwise.
        [RuntimeExport("RtlVirtualUnwind")]
        [UnmanagedCallersOnly(EntryPoint = "RtlVirtualUnwind")]
        public static void* RtlVirtualUnwind(
            uint handlerType,
            ulong imageBase,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            Context* context,
            void** handlerData,
            ulong* establisherFrame,
            void* contextPointers)
            => VirtualUnwind(handlerType, imageBase, controlPc, functionEntry,
                             context, handlerData, establisherFrame, contextPointers);

        public static void* VirtualUnwind(
            uint handlerType,
            ulong imageBase,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            Context* context,
            void** handlerData,
            ulong* establisherFrame,
            void* contextPointers = null)
        {
            if (functionEntry == null || context == null) return null;

            byte* image = (byte*)imageBase;
            byte* unwindInfo = image + functionEntry->UnwindInfoAddress;

            // step124: enable per-call trace for two specific PCs of interest.
            ulong d1 = controlPc > TraceUnwindPc1 ? controlPc - TraceUnwindPc1 : TraceUnwindPc1 - controlPc;
            ulong d2 = controlPc > TraceUnwindPc2 ? controlPc - TraceUnwindPc2 : TraceUnwindPc2 - controlPc;
            s_traceThisCall = (d1 < TraceUnwindRange) || (d2 < TraceUnwindRange);
            if (s_traceThisCall)
            {
                Console.Write("[TU-entry] controlPc=0x"); Console.WriteHex(controlPc);
                Console.Write(" funcEntry beg=0x"); Console.WriteHex((ulong)functionEntry->BeginAddress);
                Console.Write(" end=0x"); Console.WriteHex((ulong)functionEntry->EndAddress);
                Console.Write(" uw=0x"); Console.WriteHex((ulong)functionEntry->UnwindInfoAddress);
                Console.Write(" preRsp=0x"); Console.WriteHex(context->Rsp);
                Console.Write(" preRbp=0x"); Console.WriteHex(context->Rbp);
                Console.WriteLine("");
            }

            var ret = ApplyUnwindInfo(handlerType, image, controlPc, functionEntry,
                                     unwindInfo, context, handlerData, establisherFrame,
                                     contextPointers);
            if (s_traceThisCall)
            {
                Console.Write("[TU-exit] postRsp=0x"); Console.WriteHex(context->Rsp);
                Console.Write(" postRbp=0x"); Console.WriteHex(context->Rbp);
                Console.Write(" postRip=0x"); Console.WriteHex(context->Rip);
                Console.WriteLine("");
            }
            return ret;
        }

        // step112: record the address of a spilled non-volatile register
        // into the caller-supplied KNONVOLATILE_CONTEXT_POINTERS struct.
        // GcInfoDecoder reads OBJECTREF roots from these addresses (see
        // gcinfodecoder.cpp:1486 `&pRD->pCurrentContextPointers->Rax`);
        // if the struct is left unfilled the decoder reads from stale /
        // uninitialized memory and synthesizes phantom OBJECTREFs that
        // point into the managed heap range but at zeroed bytes, which
        // GCHeap::Relocate then attempts to walk -- causing the
        // Object::ValidateInner assert and (after our containment guard)
        // a stack-buffer-overrun fail-fast when relocate writes garbage
        // back into a stack slot it shouldn't touch.
        //
        // Layout (winnt.h KNONVOLATILE_CONTEXT_POINTERS, AMD64): first
        // 0x80 bytes are 16 × M128A* (Xmm0..Xmm15); GP register pointers
        // start at offset 0x80 in processor-encoding order:
        //   +0x80=Rax  +0x88=Rcx  +0x90=Rdx  +0x98=Rbx
        //   +0xA0=Rsp  +0xA8=Rbp  +0xB0=Rsi  +0xB8=Rdi
        //   +0xC0=R8 .. +0xF8=R15
        private static void RecordSpill(void* ctxPtrs, int regId, ulong* slotAddr)
        {
            if (ctxPtrs == null) return;
            ulong** gp = (ulong**)((byte*)ctxPtrs + 0x80);
            gp[regId] = slotAddr;
        }

        // Recursive worker — chases UNW_FLAG_CHAININFO to parent fragments.
        private static void* ApplyUnwindInfo(
            uint handlerType,
            byte* image,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            byte* unwindInfo,
            Context* context,
            void** handlerData,
            ulong* establisherFrame,
            void* contextPointers = null)
        {
            byte verFlags = unwindInfo[0];
            byte version  = (byte)(verFlags & 0x07);
            byte flags    = (byte)((verFlags >> 3) & 0x1F);
            byte prologSize  = unwindInfo[1];
            byte countOfCodes = unwindInfo[2];
            byte frameRegInfo = unwindInfo[3];
            int  frameReg    = frameRegInfo & 0x0F;
            int  frameOffset = (frameRegInfo >> 4) & 0x0F;   // × 16 = bytes

            // Determine how far through prolog we are. If RIP - BeginAddress
            // < prologSize, we're mid-prolog and must SKIP codes whose
            // OffsetInProlog > current offset (those slots haven't been
            // executed yet). Otherwise we're in body/epilog — apply all.
            ulong rva = controlPc - (ulong)image;
            int progress = (int)((long)rva - (long)functionEntry->BeginAddress);
            bool midProlog = (progress >= 0) && (progress < prologSize);

            if (TraceUnwind) TuHeader(controlPc, image, functionEntry,
                                      countOfCodes, flags, frameReg, frameOffset, progress, midProlog);
            if (s_traceThisCall)
            {
                Console.Write("[TU-hdr] ver="); Console.WriteInt(version);
                Console.Write(" prologSize="); Console.WriteHex(prologSize);
                Console.Write(" nCodes="); Console.WriteInt(countOfCodes);
                Console.Write(" fpReg="); Console.WriteInt(frameReg);
                Console.Write(" fpOff="); Console.WriteHex((ulong)frameOffset * 16);
                Console.Write(" progress="); Console.WriteHex((ulong)progress);
                Console.Write(" midProlog="); Console.WriteInt(midProlog ? 1 : 0);
                Console.WriteLine("");
            }

            // Capture both Rsp and FrameRegister BEFORE applying unwind codes.
            // Per Windows RtlVirtualUnwind ABI (mirrored in fork's unwinder.cpp:1129):
            //   EstablisherFrame = (FrameRegister != 0 && SET_FPREG already executed)
            //                        ? FrameReg_orig - FrameOffset*16
            //                        : Rsp_orig
            // FrameReg_orig and Rsp_orig come from the exception-time context
            // (BEFORE any unwind code is reversed). FPO functions and mid-prolog
            // (before UWOP_SET_FPREG fired) both use the original Rsp.
            // MSVC funclets receive this in RDX and access parent locals as
            // [RDX + compiler_offset]; wrong value → deref into garbage → #GP/#PF.
            ulong originalRsp = context->Rsp;
            ulong originalFrameRegValue = (frameReg != 0) ? ReadReg(context, frameReg) : 0;

            // Pre-scan unwind codes for UWOP_SET_FPREG (op==3) to discover its
            // codeOffsetInProlog. If progress >= that offset, the prolog had
            // executed SET_FPREG by exception time → FrameReg holds the
            // established frame value. Otherwise FrameReg still holds caller's
            // value and is unusable as EstablisherFrame source.
            bool fpregEstablished = (frameReg == 0) ? false : !midProlog;
            if (frameReg != 0 && midProlog)
            {
                ushort* scanCodes = (ushort*)(unwindInfo + 4);
                int si = 0;
                while (si < countOfCodes)
                {
                    ushort sraw = scanCodes[si];
                    int scodeOff = sraw & 0xFF;
                    int sop = (sraw >> 8) & 0x0F;
                    int sinfo = (sraw >> 12) & 0x0F;
                    if (sop == 3 /*UWOP_SET_FPREG*/ && scodeOff <= progress)
                    {
                        fpregEstablished = true;
                        break;
                    }
                    si += SlotCount(sop, sinfo);
                }
            }

            ushort* codes = (ushort*)(unwindInfo + 4);
            int i = 0;
            while (i < countOfCodes)
            {
                ushort raw = codes[i];
                int codeOffsetInProlog = raw & 0xFF;
                int op   = (raw >> 8) & 0x0F;
                int info = (raw >> 12) & 0x0F;

                int slotsConsumed;
                bool skipped = midProlog && codeOffsetInProlog > progress;
                if (skipped)
                {
                    // This prolog step hadn't been executed yet — skip it.
                    slotsConsumed = SlotCount(op, info);
                }
                else
                {
                    slotsConsumed = ApplyCode(op, info, codes + i, context, frameReg, frameOffset, contextPointers);
                }
                if (TraceUnwind) TuCode(op, info, codeOffsetInProlog, skipped, slotsConsumed, context);
                if (s_traceThisCall)
                {
                    Console.Write("[TU-op] off=0x"); Console.WriteHex((ulong)codeOffsetInProlog);
                    Console.Write(" op="); Console.WriteInt(op);
                    Console.Write(" info="); Console.WriteInt(info);
                    Console.Write(" skipped="); Console.WriteInt(skipped ? 1 : 0);
                    Console.Write(" slots="); Console.WriteInt(slotsConsumed);
                    Console.Write(" -> Rsp=0x"); Console.WriteHex(context->Rsp);
                    Console.Write(" Rbp=0x"); Console.WriteHex(context->Rbp);
                    Console.WriteLine("");
                }
                if (slotsConsumed < 0)
                {
                    Console.Write("[seh-unwind] unknown UNWIND_CODE op=");
                    Console.WriteInt(op);
                    Console.Write(" info=");
                    Console.WriteInt(info);
                    Console.WriteLine("");
                    return null;
                }
                i += slotsConsumed;
            }

            // Handle chained unwind info.
            if ((flags & UnwindFlags.UNW_FLAG_CHAININFO) != 0)
            {
                // After the codes (rounded up to DWORD), a 12-byte
                // RUNTIME_FUNCTION points at the parent fragment.
                int unwindSize = 4 + 2 * countOfCodes;
                unwindSize = (unwindSize + 3) & ~3;
                RuntimeFunction* parent = (RuntimeFunction*)(unwindInfo + unwindSize);
                byte* parentUnwindInfo = image + parent->UnwindInfoAddress;
                // Compute EstablisherFrame from THIS fragment's original
                // context (before parent's unwind codes mutate it further),
                // then pass null down so parent doesn't overwrite.
                if (establisherFrame != null)
                {
                    *establisherFrame = (frameReg != 0 && fpregEstablished)
                        ? originalFrameRegValue - (ulong)frameOffset * 16
                        : originalRsp;
                }
                return ApplyUnwindInfo(handlerType, image, controlPc, parent,
                                       parentUnwindInfo, context, handlerData, null,
                                       contextPointers);
            }

            // SP now points at saved return address. Pop it into RIP, then
            // advance SP by 8.
            ulong* sp = (ulong*)context->Rsp;
            if (TraceUnwind) TuFinalize(context, sp);

            // EstablisherFrame computed per Windows ABI:
            //   FP function (UNWIND_INFO.FrameRegister != 0): use the original
            //     FrameRegister value at exception time (typically Rbp).
            //   FPO function: use the saved-RA address (Rsp BEFORE popping it),
            //     which equals the function's entry-Rsp.
            // Both shapes are what MSVC funclets expect in RDX.
            if (establisherFrame != null)
            {
                // Mirrors fork unwinder.cpp:1129. Compute from EXCEPTION-TIME
                // context (originalRsp / originalFrameRegValue), never from
                // post-unwind state — unwind codes have already been reversed
                // and the relevant registers may now hold caller values.
                *establisherFrame = (frameReg != 0 && fpregEstablished)
                    ? originalFrameRegValue - (ulong)frameOffset * 16
                    : originalRsp;
            }

            context->Rip = *sp;
            context->Rsp = (ulong)(sp + 1);

            // If function has matching handler, expose it.
            bool wantHandler = (handlerType & UnwindFlags.UNW_FLAG_EHANDLER) != 0
                            || (handlerType & UnwindFlags.UNW_FLAG_UHANDLER) != 0;
            if (wantHandler && (flags & (UnwindFlags.UNW_FLAG_EHANDLER | UnwindFlags.UNW_FLAG_UHANDLER)) != 0)
            {
                int unwindSize = 4 + 2 * countOfCodes;
                unwindSize = (unwindSize + 3) & ~3;
                uint handlerRva = *(uint*)(unwindInfo + unwindSize);
                if (handlerData != null) *handlerData = unwindInfo + unwindSize + 4;
                return image + handlerRva;
            }

            return null;
        }

        // Apply one unwind code. Returns number of slots consumed (1 / 2 / 3),
        // or -1 if opcode unknown.
        //
        // Per-opcode behavior reverses the prolog action:
        //   PUSH_NONVOL r:    register r ← [Rsp]; Rsp += 8
        //   ALLOC_LARGE size: Rsp += size
        //   ALLOC_SMALL n:    Rsp += (n+1)*8
        //   SET_FPREG:        Rsp = framereg - frameoffset*16
        //   SAVE_NONVOL r,o:  register r ← [Rsp + o*8]
        //   SAVE_XMM128:      (skipped — vol across calls for our use; just consume slots)
        //   PUSH_MACHFRAME:   pops machine frame (interrupt-style)
        //   EPILOG:           Win8+ marker — no register effect, consumed for slot accounting
        private static int ApplyCode(int op, int info, ushort* code, Context* ctx,
                                     int frameReg, int frameOffset, void* ctxPtrs = null)
        {
            switch (op)
            {
                case UnwindOp.UWOP_PUSH_NONVOL:
                {
                    ulong* slot = (ulong*)ctx->Rsp;
                    RecordSpill(ctxPtrs, info, slot);
                    ulong val = *slot;
                    WriteReg(ctx, info, val);
                    ctx->Rsp += 8;
                    return 1;
                }
                case UnwindOp.UWOP_ALLOC_LARGE:
                {
                    if (info == 0)
                    {
                        ushort sizeSlots = code[1];
                        ctx->Rsp += (ulong)sizeSlots * 8u;
                        return 2;
                    }
                    else if (info == 1)
                    {
                        uint sizeLo = code[1];
                        uint sizeHi = code[2];
                        uint size = sizeLo | (sizeHi << 16);
                        ctx->Rsp += size;
                        return 3;
                    }
                    return -1;
                }
                case UnwindOp.UWOP_ALLOC_SMALL:
                    ctx->Rsp += (ulong)((info + 1) * 8);
                    return 1;
                case UnwindOp.UWOP_SET_FPREG:
                {
                    ulong fpVal = ReadReg(ctx, frameReg);
                    ctx->Rsp = fpVal - (ulong)(frameOffset * 16);
                    return 1;
                }
                case UnwindOp.UWOP_SAVE_NONVOL:
                {
                    uint slotOffset = code[1];   // *8 = bytes
                    ulong* slot = (ulong*)(ctx->Rsp + slotOffset * 8u);
                    RecordSpill(ctxPtrs, info, slot);
                    WriteReg(ctx, info, *slot);
                    return 2;
                }
                case UnwindOp.UWOP_SAVE_NONVOL_FAR:
                {
                    uint lo = code[1];
                    uint hi = code[2];
                    uint offset = lo | (hi << 16);
                    ulong* slot = (ulong*)(ctx->Rsp + offset);
                    RecordSpill(ctxPtrs, info, slot);
                    WriteReg(ctx, info, *slot);
                    return 3;
                }
                case UnwindOp.UWOP_EPILOG:
                    // Win8+ v2 marker: signals epilog location. No register
                    // effect. Code spans 1 or 2 slots depending on first one
                    // (low bit of info = "first epilog code").
                    return 2;   // conservative: most epilog markers are 2 slots
                case UnwindOp.UWOP_SAVE_XMM128:
                    // We don't restore XMM regs (volatile around call). Consume.
                    return 2;
                case UnwindOp.UWOP_SAVE_XMM128_FAR:
                    return 3;
                case UnwindOp.UWOP_PUSH_MACHFRAME:
                    // Pops an interrupt/exception machine frame: 5 or 6 qwords
                    // (with or without error code). info=0 → no error code (5
                    // qwords: RIP, CS, EFlags, RSP, SS); info=1 → +error code.
                    // We don't currently need this for CoreCLR's standard
                    // call frames. Skip with diagnostic.
                    return 1;
                default:
                    return -1;
            }
        }

        // Slot count for skipping mid-prolog codes that haven't been reached.
        private static int SlotCount(int op, int info)
        {
            switch (op)
            {
                case UnwindOp.UWOP_PUSH_NONVOL:     return 1;
                case UnwindOp.UWOP_ALLOC_LARGE:     return info == 0 ? 2 : 3;
                case UnwindOp.UWOP_ALLOC_SMALL:     return 1;
                case UnwindOp.UWOP_SET_FPREG:       return 1;
                case UnwindOp.UWOP_SAVE_NONVOL:     return 2;
                case UnwindOp.UWOP_SAVE_NONVOL_FAR: return 3;
                case UnwindOp.UWOP_EPILOG:          return 2;
                case UnwindOp.UWOP_SAVE_XMM128:     return 2;
                case UnwindOp.UWOP_SAVE_XMM128_FAR: return 3;
                case UnwindOp.UWOP_PUSH_MACHFRAME:  return 1;
                default: return 1;
            }
        }

        private static void WriteReg(Context* ctx, int regId, ulong val)
        {
            switch (regId)
            {
                case 0:  ctx->Rax = val; break;
                case 1:  ctx->Rcx = val; break;
                case 2:  ctx->Rdx = val; break;
                case 3:  ctx->Rbx = val; break;
                case 4:  ctx->Rsp = val; break;
                case 5:  ctx->Rbp = val; break;
                case 6:  ctx->Rsi = val; break;
                case 7:  ctx->Rdi = val; break;
                case 8:  ctx->R8  = val; break;
                case 9:  ctx->R9  = val; break;
                case 10: ctx->R10 = val; break;
                case 11: ctx->R11 = val; break;
                case 12: ctx->R12 = val; break;
                case 13: ctx->R13 = val; break;
                case 14: ctx->R14 = val; break;
                case 15: ctx->R15 = val; break;
            }
        }

        private static ulong ReadReg(Context* ctx, int regId)
        {
            switch (regId)
            {
                case 0:  return ctx->Rax;
                case 1:  return ctx->Rcx;
                case 2:  return ctx->Rdx;
                case 3:  return ctx->Rbx;
                case 4:  return ctx->Rsp;
                case 5:  return ctx->Rbp;
                case 6:  return ctx->Rsi;
                case 7:  return ctx->Rdi;
                case 8:  return ctx->R8;
                case 9:  return ctx->R9;
                case 10: return ctx->R10;
                case 11: return ctx->R11;
                case 12: return ctx->R12;
                case 13: return ctx->R13;
                case 14: return ctx->R14;
                case 15: return ctx->R15;
                default: return 0;
            }
        }

        // ─── step 89 §11 diag helpers (compile-out via TraceUnwind=false) ─
        // Concise per-frame / per-code dump: enough to locate which
        // UNWIND_CODE applied to the last good frame before walker
        // reads a stack address as RIP.

        private static void TuHeader(ulong controlPc, byte* image, RuntimeFunction* fn,
                                     int nCodes, int flags, int fpReg, int fpOff,
                                     int progress, bool midProlog)
        {
            Console.Write("[uw] f pc=0x");        Console.WriteHex(controlPc);
            Console.Write(" rva=0x");             Console.WriteHex((ulong)(controlPc - (ulong)image));
            Console.Write(" beg=0x");             Console.WriteHex((ulong)fn->BeginAddress);
            Console.Write(" end=0x");             Console.WriteHex((ulong)fn->EndAddress);
            Console.Write(" n=");                 Console.WriteInt(nCodes);
            Console.Write(" flg=0x");             Console.WriteHex((ulong)flags);
            if (fpReg != 0) { Console.Write(" fpReg="); Console.WriteInt(fpReg);
                              Console.Write(" fpOff="); Console.WriteInt(fpOff * 16); }
            Console.Write(" prog=");              Console.WriteInt(progress);
            if (midProlog) Console.Write(" MID");
            Console.WriteLine("");
            // First 48 bytes of function body — lets us identify the function
            // by its prolog+early-body signature when no symbols available.
            // 48 bytes typically covers prolog (~16) + enough body to catch
            // `mov rbp, [...]` and the first `call` displacement.
            byte* fnBytes = image + fn->BeginAddress;
            Console.Write("[uw]   bytes:");
            for (int b = 0; b < 48; b++)
            {
                if ((b & 15) == 0 && b > 0) { Console.WriteLine(""); Console.Write("[uw]         "); }
                Console.Write(" ");
                byte v = fnBytes[b];
                if (v < 0x10) Console.Write("0");
                Console.WriteHex((ulong)v);
            }
            Console.WriteLine("");
        }

        private static void TuCode(int op, int info, int pOff, bool skipped,
                                   int slots, Context* ctx)
        {
            Console.Write("[uw]  op=");           Console.WriteInt(op);
            Console.Write(" info=");              Console.WriteInt(info);
            Console.Write(" pOff=");              Console.WriteInt(pOff);
            if (skipped) Console.Write(" SKIP");
            Console.Write(" slots=");             Console.WriteInt(slots);
            Console.Write("  rsp=0x");            Console.WriteHex(ctx->Rsp);
            Console.WriteLine("");
        }

        private static void TuFinalize(Context* ctx, ulong* sp)
        {
            Console.Write("[uw] fin rsp=0x");     Console.WriteHex(ctx->Rsp);
            Console.Write(" *rsp=0x");            Console.WriteHex(*sp);
            Console.WriteLine("");
        }
    }
}
