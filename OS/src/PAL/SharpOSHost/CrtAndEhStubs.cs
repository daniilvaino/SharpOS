using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;
using OS.Kernel;
using OS.Kernel.Memory;

namespace OS.PAL.SharpOSHost
{
    // Phase 6.1.0b — fatal/minimal stubs that close the kernel link when
    // CoreCLR guest archive is linked into the kernel image.
    //
    // L1 mechanical CRT (real impl, must work — CoreCLR calls during
    // very early init before managed runtime ready):
    //   memset / memcpy / memmove / memcmp
    //
    // L5 EH personality stubs (fatal — Phase 6.1.0b/6.1.a/b do not
    // exercise C++ EH paths; if hit это bug):
    //   __CxxFrameHandler3   — MSVC C++ EH personality routine
    //   _CxxThrowException   — MSVC C++ throw
    //   __C_specific_handler — Windows SEH personality
    //
    // L1 misc CRT minimal (constants / fatal):
    //   _purecall            — fatal (pure virtual call should never fire)
    //   _fltused             — global int = 1 (CRT linker marker)
    //   _security_cookie     — global cookie value (CFG/security)
    //   __chkstk             — fatal for now (stack probe; trivial real impl available)
    //
    // ALL go в kernel image as C-ABI exports. CoreCLR static archive's
    // .obj files reference these symbols at link; resolver finds our
    // exports here.
    internal static unsafe class CrtAndEhStubs
    {
        // ---------------------------------------------------------------
        // L1 — mechanical CRT (real impl via SharpOS.Std.NoRuntime).
        // NOTE: memset/memcpy/memmove already provided by OS/src/Boot/
        // MinimalRuntime.cs NativeMemoryStubs (predates Phase 6).
        // ---------------------------------------------------------------

        [RuntimeExport("memcmp")]
        public static int Memcmp(void* a, void* b, ulong count)
        {
            byte* pa = (byte*)a;
            byte* pb = (byte*)b;
            for (ulong i = 0; i < count; i++)
            {
                if (pa[i] != pb[i])
                    return pa[i] < pb[i] ? -1 : 1;
            }
            return 0;
        }

        // ---------------------------------------------------------------
        // L5 — EH personality routines.
        // __CxxFrameHandler3 lives in CxxFrameHandler.cs (Phase 6.1.b — real
        // MSVC C++ EH personality). __C_specific_handler is here.
        // ---------------------------------------------------------------

        // EXCEPTION_DISPOSITION __C_specific_handler(
        //     PEXCEPTION_RECORD pExceptionRecord,
        //     void*             pEstablisherFrame,
        //     PCONTEXT          pContextRecord,
        //     void*             pDispatcherContext);
        // __C_specific_handler — Windows SEH personality для C `__try/__except`
        // и `__try/__finally`. CoreCLR EX_TRY macros expand to __try/__except.
        //
        // DispatcherContext.HandlerData → ScopeTable:
        //   [count: 4 bytes]
        //   then count × ScopeRecord (16 bytes):
        //     BeginAddress   — try-region start RVA (from image base)
        //     EndAddress     — try-region end RVA
        //     HandlerAddress — filter RVA, or const 1 (always-catch __except)
        //     JumpTarget     — __except body RVA, or 0 если scope is __finally
        //
        // First pass (search): walk scopes covering current RIP. For each
        // __except (JumpTarget != 0), invoke filter:
        //   - 1  EXECUTE_HANDLER → match, set TargetIp, return marker
        //   - 0  CONTINUE_SEARCH → skip scope, try next
        //   - -1 CONTINUE_EXECUTION → resume from current RIP
        // Filter ABI (x64): rcx = EXCEPTION_POINTERS*, rdx = establisher frame.
        //
        // Unwind pass: walk scopes whose JumpTarget == 0 (__finally), call
        // handler funclet. ABI: rcx = abnormal flag (1), rdx = establisher.
        [RuntimeExport("__C_specific_handler")]
        public static int CSpecificHandler(ExceptionRecord* rec,
                                            void* establisherFrame,
                                            Context* ctx,
                                            DispatcherContext* dc)
        {
            byte* image = (byte*)dc->ImageBase;
            uint* scopeTable = (uint*)dc->HandlerData;
            if (scopeTable == null) return 1;   // ContinueSearch
            uint count = scopeTable[0];
            ScopeRecord* records = (ScopeRecord*)(scopeTable + 1);

            uint ripRva = (uint)(dc->ControlPc - dc->ImageBase);
            Console.Write("[__C_specific_handler] controlPc=0x"); Console.WriteHex(dc->ControlPc);
            Console.Write(" ripRva=0x"); Console.WriteHex(ripRva);
            Console.Write(" nScopes="); Console.WriteInt((int)count);
            Console.WriteLine("");
            for (uint si = 0; si < count && si < 8; si++)
            {
                Console.Write("  scope["); Console.WriteInt((int)si);
                Console.Write("] beg=0x"); Console.WriteHex(records[si].BeginAddress);
                Console.Write(" end=0x"); Console.WriteHex(records[si].EndAddress);
                Console.Write(" h=0x"); Console.WriteHex(records[si].HandlerAddress);
                Console.Write(" jt=0x"); Console.WriteHex(records[si].JumpTarget);
                Console.WriteLine("");
            }
            bool unwinding = (rec->ExceptionFlags &
                (ExceptionRecord.EXCEPTION_UNWINDING |
                 ExceptionRecord.EXCEPTION_EXIT_UNWIND)) != 0;
            bool targetUnwind = (rec->ExceptionFlags &
                ExceptionRecord.EXCEPTION_TARGET_UNWIND) != 0;

            for (uint i = 0; i < count; i++)
            {
                ScopeRecord r = records[i];
                if (ripRva < r.BeginAddress || ripRva >= r.EndAddress) continue;

                Console.Write("  → scope["); Console.WriteInt((int)i); Console.Write("] matches ripRva");
                Console.WriteLine("");

                if (r.JumpTarget == 0)
                {
                    Console.WriteLine("    __finally");
                    if (unwinding && !targetUnwind)
                    {
                        delegate* unmanaged<int, void*, void> finallyFn =
                            (delegate* unmanaged<int, void*, void>)(image + r.HandlerAddress);
                        finallyFn(1, establisherFrame);
                    }
                    continue;
                }

                Console.Write("    __except, unwinding="); Console.WriteInt(unwinding ? 1 : 0);
                Console.WriteLine("");
                if (unwinding) continue;

                int filterResult;
                if (r.HandlerAddress == 1)
                {
                    filterResult = 1;
                    Console.WriteLine("    handler==1 (always-catch)");
                }
                else
                {
                    ExceptionPointers ep;
                    ep.ExceptionRecord = rec;
                    ep.ContextRecord = ctx;
                    void* filterAbs = image + r.HandlerAddress;
                    Console.Write("    calling filter @0x"); Console.WriteHex((ulong)filterAbs);
                    Console.WriteLine("");
                    delegate* unmanaged<ExceptionPointers*, void*, int> filter =
                        (delegate* unmanaged<ExceptionPointers*, void*, int>)filterAbs;
                    filterResult = filter(&ep, establisherFrame);
                    Console.Write("    filter returned "); Console.WriteInt(filterResult);
                    Console.WriteLine("");
                }

                if (filterResult == 1)
                {
                    dc->TargetIp = (ulong)(image + r.JumpTarget);
                    Console.Write("    HANDLER MATCHED, target=0x"); Console.WriteHex(dc->TargetIp);
                    Console.WriteLine("");
                    return ExceptionDispositionExt.ExceptionExecuteHandlerMarker;
                }
                if (filterResult == -1)
                {
                    return 0;
                }
            }

            return 1;   // ExceptionContinueSearch
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ScopeRecord
        {
            public uint BeginAddress;
            public uint EndAddress;
            public uint HandlerAddress;
            public uint JumpTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct ExceptionPointers
        {
            public ExceptionRecord* ExceptionRecord;
            public Context* ContextRecord;
        }

        // _CxxThrowException — implemented в SehDispatch.CxxThrow (Phase 6.1.b
        // SEH unwind port). Drives full first-pass search + second-pass
        // unwind with __CxxFrameHandler3 personality routine.

        // ---------------------------------------------------------------
        // L1 misc CRT.
        // ---------------------------------------------------------------

        // void __cdecl _purecall(void);
        [RuntimeExport("_purecall")]
        public static void Purecall()
        {
            Panic.Fail("_purecall fired (pure virtual function call — CoreCLR bug if reached)");
        }

        // __chkstk — see ChkstkStub.cs / ChkstkPatcher.cs. The MSVC ABI for
        // __chkstk is net-zero on RSP — the CALLER emits the actual
        // `sub rsp, rax` after the call returns. So our no-op `ret` works
        // on the unikernel where guard-page probing is unnecessary (flat
        // large kernel stack, no guard pages).
        //
        // Earlier in Phase 6.1.a we ran with libcmt's __chkstk, but its asm
        // reads `gs:[10h]` (TEB.StackLimit). On bare metal TEB.StackLimit
        // is a one-shot snapshot from boot SP; when current RSP descends
        // below it (CoreCLR frames >= 4 KiB), __chkstk enters its
        // page-by-page zero-write extension loop and corrupts kernel memory.
        // The patched stub bypasses that path.

        // ---------------------------------------------------------------
        // L1 — CRT string functions (real impl, trivial).
        // ---------------------------------------------------------------

        // char* strchr(const char* s, int c) — find first byte == c
        [RuntimeExport("strchr")]
        public static byte* Strchr(byte* s, int c)
        {
            byte target = (byte)c;
            while (*s != 0)
            {
                if (*s == target) return s;
                s++;
            }
            return target == 0 ? s : null;
        }

        // char* strrchr(const char* s, int c) — find LAST byte == c
        [RuntimeExport("strrchr")]
        public static byte* Strrchr(byte* s, int c)
        {
            byte target = (byte)c;
            byte* last = null;
            while (*s != 0)
            {
                if (*s == target) last = s;
                s++;
            }
            if (target == 0) return s;
            return last;
        }

        // char* strstr(const char* haystack, const char* needle)
        [RuntimeExport("strstr")]
        public static byte* Strstr(byte* haystack, byte* needle)
        {
            if (*needle == 0) return haystack;
            for (byte* h = haystack; *h != 0; h++)
            {
                byte* hp = h;
                byte* np = needle;
                while (*np != 0 && *hp == *np) { hp++; np++; }
                if (*np == 0) return h;
            }
            return null;
        }

        // void* memchr(const void* s, int c, size_t n) — find first byte == c in n bytes
        [RuntimeExport("memchr")]
        public static void* Memchr(void* s, int c, ulong n)
        {
            byte target = (byte)c;
            byte* p = (byte*)s;
            for (ulong i = 0; i < n; i++)
            {
                if (p[i] == target) return p + i;
            }
            return null;
        }

        // wchar_t* wcsstr(const wchar_t* haystack, const wchar_t* needle)
        [RuntimeExport("wcsstr")]
        public static char* Wcsstr(char* haystack, char* needle)
        {
            if (*needle == 0) return haystack;
            for (char* h = haystack; *h != 0; h++)
            {
                char* hp = h;
                char* np = needle;
                while (*np != 0 && *hp == *np) { hp++; np++; }
                if (*np == 0) return h;
            }
            return null;
        }

        // wchar_t* wcschr(const wchar_t* s, wchar_t c)
        [RuntimeExport("wcschr")]
        public static char* Wcschr(char* s, char c)
        {
            while (*s != 0)
            {
                if (*s == c) return s;
                s++;
            }
            return c == 0 ? s : null;
        }

        // wchar_t* wcsrchr(const wchar_t* s, wchar_t c)
        [RuntimeExport("wcsrchr")]
        public static char* Wcsrchr(char* s, char c)
        {
            char* last = null;
            while (*s != 0)
            {
                if (*s == c) last = s;
                s++;
            }
            if (c == 0) return s;
            return last;
        }

        // ---------------------------------------------------------------
        // L5 / L4 — EH/std debug stubs.
        // ---------------------------------------------------------------

        // __CxxFrameHandler4 — newer MSVC C++ EH personality (Release /Ox
        // compact FH4 tables). Implemented in CxxFrameHandler4.cs
        // (CxxFrameHandler4.FrameHandler4) which owns the single
        // [RuntimeExport("__CxxFrameHandler4")]. No stub here.

        // bool __uncaught_exception(void) — std lib internal, returns true if в throw
        [RuntimeExport("__uncaught_exception")]
        public static int UncaughtException()
        {
            return 0; // Never в a throw on Phase 6.1.0b
        }

        // _CrtDbgReportW(int reportType, const wchar_t* filename, int line, ...)
        // — debug CRT assertion / warning reporter. Surface to console and
        // return 0 (= "handled, do not break") so caller continues. This
        // covers both fatal asserts AND non-fatal warnings — we'd rather
        // see the message than halt blindly. Real fatal cases are still
        // catchable downstream if the caller treats _CrtDbgReportW's exit
        // code as terminal.
        [RuntimeExport("_CrtDbgReportW")]
        public static int CrtDbgReportW(int reportType, char* filename, int line, char* module, char* fmt, void* vargs)
        {
            Console.Write("[CrtDbgReport type=");
            Console.WriteInt(reportType);
            Console.Write(" line=");
            Console.WriteInt(line);
            Console.Write("] file=");
            WriteWChar(filename);
            Console.Write(" mod=");
            WriteWChar(module);
            Console.Write(" fmt=");
            WriteWChar(fmt);
            Console.WriteLine("");
            return 0; // do not break / continue
        }

        // Helper: print a null-terminated wide string as ASCII (non-printable
        // chars rendered as '?'). Used by diagnostic stubs.
        private static void WriteWChar(char* p)
        {
            if (p == null) { Console.Write("(null)"); return; }
            while (*p != 0)
            {
                char wc = *p++;
                if ((wc >= ' ' && wc < (char)0x7F) || wc == '\n' || wc == '\t')
                    Console.WriteChar(wc);
                else
                    Console.WriteChar('?');
            }
        }

        // void longjmp(jmp_buf env, int val) — non-local control transfer.
        // Used в CoreCLR PropagateLongJmpThroughNativeFrames (rare path).
        // Fatal — Phase 6.1.0b doesn't exercise это.
        [RuntimeExport("longjmp")]
        public static void Longjmp(void* env, int val)
        {
            Panic.Fail("longjmp fired (CoreCLR PropagateLongJmpThroughNativeFrames; Phase 6.1.0b stub)");
        }

        // void __std_exception_copy(struct __std_exception_data*, struct __std_exception_data*)
        // — pass-through copy of exception data.
        [RuntimeExport("__std_exception_copy")]
        public static void StdExceptionCopy(void* src, void* dst)
        {
            // Just zero out dst — exception body не реально used в 6.1.0b
            byte* p = (byte*)dst;
            if (p != null) { p[0] = 0; p[8] = 0; }
        }

        // void __std_exception_destroy(struct __std_exception_data*)
        [RuntimeExport("__std_exception_destroy")]
        public static void StdExceptionDestroy(void* data)
        {
            // No-op
        }

        // void* __current_exception(void) — TLS slot for current exception
        [RuntimeExport("__current_exception")]
        public static void* CurrentException()
        {
            return null;
        }

        // void* __current_exception_context(void) — TLS slot for context
        [RuntimeExport("__current_exception_context")]
        public static void* CurrentExceptionContext()
        {
            return null;
        }

        // ---------------------------------------------------------------
        // L4 — debug CRT internals (libcmtd) — fatal/no-op.
        // These are called by libcmtd's __scrt_initialize_crt / __scrt_dllmain*
        // which CoreCLR debug build pulls in. Phase 6.1.0b: trivial stubs.
        // ---------------------------------------------------------------

        [RuntimeExport("__vcrt_initialize")]
        public static int VcrtInitialize() { return 1; }

        [RuntimeExport("__vcrt_uninitialize")]
        public static int VcrtUninitialize(int isTerminate) { return 1; }

        [RuntimeExport("__vcrt_uninitialize_critical")]
        public static int VcrtUninitializeCritical() { return 1; }

        [RuntimeExport("__vcrt_thread_attach")]
        public static int VcrtThreadAttach() { return 1; }

        [RuntimeExport("__vcrt_thread_detach")]
        public static int VcrtThreadDetach() { return 1; }

        [RuntimeExport("__acrt_initialize")]
        public static int AcrtInitialize() { return 1; }

        [RuntimeExport("__acrt_uninitialize")]
        public static int AcrtUninitialize(int isTerminate) { return 1; }

        [RuntimeExport("__acrt_uninitialize_critical")]
        public static int AcrtUninitializeCritical() { return 1; }

        [RuntimeExport("__acrt_thread_attach")]
        public static int AcrtThreadAttach() { return 1; }

        [RuntimeExport("__acrt_thread_detach")]
        public static int AcrtThreadDetach() { return 1; }

        [RuntimeExport("_is_c_termination_complete")]
        public static int IsCTerminationComplete() { return 0; }

        // _malloc_dbg / _free_dbg — debug CRT heap entry points emitted by
        // libcmtd / Debug-build CoreCLR's `new`/`delete` operators. Route
        // to SharpOSHost so they share the SharpOS GC heap with regular
        // malloc/free. blockType + filename + line are debug-tracking args
        // we ignore (no leak tracking needed in unikernel).
        [RuntimeExport("_malloc_dbg")]
        public static void* MallocDbg(ulong size, int blockType, byte* filename, int line)
        {
            if (size == 0) return NativeArena.Allocate(1);
            return NativeArena.Allocate(size);
        }

        [RuntimeExport("_free_dbg")]
        public static void FreeDbg(void* ptr, int blockType)
        {
            // No-op — GC sweeps unreachable blocks.
        }

        [RuntimeExport("_calloc_dbg")]
        public static void* CallocDbg(ulong num, ulong size, int blockType, byte* filename, int line)
        {
            ulong total = num * size;
            if (total == 0) return null;
            // NativeArena.Allocate already zero-fills.
            return NativeArena.Allocate(total);
        }

        [RuntimeExport("_realloc_dbg")]
        public static void* ReallocDbg(void* old, ulong size, int blockType, byte* filename, int line)
        {
            if (size == 0) return null;
            void* fresh = NativeArena.Allocate(size);
            if (fresh != null && old != null)
            {
                byte* dst = (byte*)fresh;
                byte* src = (byte*)old;
                for (ulong i = 0; i < size; i++) dst[i] = src[i];
            }
            return fresh;
        }

        // ---------------------------------------------------------------
        // Static-ctor hijack stubs (Phase 6.1.a — replaces libcmtd's
        // internal init paths that were faulting on uninit sentinels).
        //
        // ctor dep audit found 3 truly-external CRT symbols used by 197
        // static C++ ctors in our linked surface. By providing our own
        // implementations CoreCLR's ctors call us directly and libcmtd's
        // _register_thread_local_exe_atexit_callback / TLS dtor table
        // paths never run.
        //
        // See work/PAL/symbol-audit/ctor-deps/summary.md.
        // ---------------------------------------------------------------

        // int atexit(void (*func)(void)) — register at-exit callback.
        // 6 ctors call this (log/pgo/profdetach/stubhelpers).
        // Kernel never shuts down → callbacks never need to run → no-op.
        [RuntimeExport("atexit")]
        public static int Atexit(void* func) { return 0; }

        // int __tlregdtor(_PVFV func) — libcmtd internal: register
        // thread-local destructor for current thread's TLS shutdown.
        // 3 ctors call this (ceemain tls_destructionMonitor, eventpipe).
        // D5 says single-threaded boot → no thread teardown → no-op.
        [RuntimeExport("__tlregdtor")]
        public static int Tlregdtor(void* func) { return 0; }
    }

    // Globals что MSVC CRT linker expects as DATA, not functions.
    internal static class CrtGlobals
    {
        // ---------------------------------------------------------------
        // CRT data symbols — `__security_cookie` via CoffStub.Generator.
        //
        // step 121: ILC's [RuntimeExport] на static field НЕ emit'ит native
        // data symbol — это исторический ILC gap. CoffStub.Generator
        // (bootasm/CoffStub.Generator/) обходит gap: атрибут на C# static
        // field → MSBuild Task материализует tiny COFF .obj с этим полем
        // как native external data symbol, .obj автоматом в @(NativeLibrary).
        //
        // Закрывает kernel-side libcmt cut (step 120 fork-side merge +
        // step 121 kernel-side managed-only).
        //
        // Остальные libcmt data symbols (`_fltused`, `_tls_index`) пока
        // тащатся из libcmt'овой копии внутри merged coreclr_static.lib —
        // OS.obj их не references (только fork C++ code). Если когда-то
        // понадобятся kernel-side — добавим аналогичные [CoffDataSymbol]'ы.
        // ---------------------------------------------------------------

        [BootAsm.CoffDataSymbol("__security_cookie", Section = ".data", Alignment = 8)]
        public static ulong SecurityCookie = 0x2B992DDFA232UL;

        // `__security_check_cookie` — verifies cookie matches at epilogue.
        // Phase 6.1.b: false-positive прошёл empirically на CoreCLR boot —
        // MSVC's __security_init_cookie clears low byte (`& ~0xff`) BEFORE
        // first use, while our static SecurityCookie keeps its full literal
        // value. Different compilation units also reference different
        // `__security_cookie` symbol storage under /FORCE:MULTIPLE linkage.
        // Reconciling is complex; net result is no real stack-canary semantic
        // is enforced anyway, so make this a true no-op (avoids #UD from
        // libcmt's __report_gsfailure on mismatch).
        [RuntimeExport("__security_check_cookie")]
        public static void SecurityCheckCookie(ulong cookie)
        {
            _ = cookie;
        }

        // `_tls_index` — link-time TLS module slot index. См. block-комментарий
        // выше (CRT data symbols) почему [RuntimeExport] здесь снят.
        // Сейчас удовлетворяется libcmt'овой копией внутри coreclr_static.lib.
    }
}
