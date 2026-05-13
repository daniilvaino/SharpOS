using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel;
using SharpOS.Std.NoRuntime;

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
        [UnmanagedCallersOnly(EntryPoint = "memcmp")]
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
        // L5 — EH personality stubs (fatal in Phase 6.1.0b).
        // Per D13: real implementation deferred until 6.1.c (Phase 1
        // unwinder extension с MSVC personality semantics).
        // ---------------------------------------------------------------

        // EXCEPTION_DISPOSITION __CxxFrameHandler3(
        //     PEXCEPTION_RECORD pExceptionRecord,
        //     void*             pEstablisherFrame,
        //     PCONTEXT          pContextRecord,
        //     void*             pDispatcherContext);
        [RuntimeExport("__CxxFrameHandler3")]
        [UnmanagedCallersOnly(EntryPoint = "__CxxFrameHandler3")]
        public static int CxxFrameHandler3(void* record, void* frame, void* context, void* dispatcher)
        {
            Panic.Fail("__CxxFrameHandler3 fired (Phase 6.1.0b stub, D13 EH not implemented)");
            return 1; // ExceptionContinueSearch
        }

        // EXCEPTION_DISPOSITION __C_specific_handler(
        //     PEXCEPTION_RECORD pExceptionRecord,
        //     void*             pEstablisherFrame,
        //     PCONTEXT          pContextRecord,
        //     void*             pDispatcherContext);
        [RuntimeExport("__C_specific_handler")]
        [UnmanagedCallersOnly(EntryPoint = "__C_specific_handler")]
        public static int CSpecificHandler(void* record, void* frame, void* context, void* dispatcher)
        {
            Panic.Fail("__C_specific_handler fired (Phase 6.1.0b stub, D13 EH not implemented)");
            return 1; // ExceptionContinueSearch
        }

        // void _CxxThrowException(void* pExceptionObject, void* pThrowInfo);
        [RuntimeExport("_CxxThrowException")]
        [UnmanagedCallersOnly(EntryPoint = "_CxxThrowException")]
        public static void CxxThrowException(void* exceptionObj, void* throwInfo)
        {
            Panic.Fail("_CxxThrowException fired (CoreCLR threw C++ exception; Phase 6.1.0b stub)");
        }

        // ---------------------------------------------------------------
        // L1 misc CRT.
        // ---------------------------------------------------------------

        // void __cdecl _purecall(void);
        [RuntimeExport("_purecall")]
        [UnmanagedCallersOnly(EntryPoint = "_purecall")]
        public static void Purecall()
        {
            Panic.Fail("_purecall fired (pure virtual function call — CoreCLR bug if reached)");
        }

        // __chkstk — stack probe для allocations >4KB. Real impl walks
        // pages and touches each (forces page fault for guard page).
        // Phase 6.1.0b: trivial no-op (assumes stack always committed).
        [RuntimeExport("__chkstk")]
        [UnmanagedCallersOnly(EntryPoint = "__chkstk")]
        public static void Chkstk()
        {
            // No-op для kernel context. Kernel stack is wholly committed
            // (no guard pages, no demand-paged kernel stack). CoreCLR's
            // __chkstk calls для large local arrays are safe to ignore.
        }

        // ---------------------------------------------------------------
        // L1 — CRT string functions (real impl, trivial).
        // ---------------------------------------------------------------

        // char* strchr(const char* s, int c) — find first byte == c
        [RuntimeExport("strchr")]
        [UnmanagedCallersOnly(EntryPoint = "strchr")]
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
        [UnmanagedCallersOnly(EntryPoint = "strrchr")]
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
        [UnmanagedCallersOnly(EntryPoint = "strstr")]
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
        [UnmanagedCallersOnly(EntryPoint = "memchr")]
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
        [UnmanagedCallersOnly(EntryPoint = "wcsstr")]
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
        [UnmanagedCallersOnly(EntryPoint = "wcschr")]
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
        [UnmanagedCallersOnly(EntryPoint = "wcsrchr")]
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

        // __CxxFrameHandler4 — newer MSVC C++ EH personality (used by libcmtd
        // в newer VC). Same fatal stub semantics as __CxxFrameHandler3.
        [RuntimeExport("__CxxFrameHandler4")]
        [UnmanagedCallersOnly(EntryPoint = "__CxxFrameHandler4")]
        public static int CxxFrameHandler4(void* record, void* frame, void* context, void* dispatcher)
        {
            Panic.Fail("__CxxFrameHandler4 fired (Phase 6.1.0b stub)");
            return 1;
        }

        // bool __uncaught_exception(void) — std lib internal, returns true if в throw
        [RuntimeExport("__uncaught_exception")]
        [UnmanagedCallersOnly(EntryPoint = "__uncaught_exception")]
        public static int UncaughtException()
        {
            return 0; // Never в a throw on Phase 6.1.0b
        }

        // _CrtDbgReportW(int reportType, const wchar_t* filename, int line, ...)
        // — debug CRT assertion reporter. Fatal stub.
        [RuntimeExport("_CrtDbgReportW")]
        [UnmanagedCallersOnly(EntryPoint = "_CrtDbgReportW")]
        public static int CrtDbgReportW(int reportType, char* filename, int line, char* module, char* fmt, void* vargs)
        {
            Panic.Fail("_CrtDbgReportW fired (CRT assertion in CoreCLR)");
            return 0;
        }

        // void longjmp(jmp_buf env, int val) — non-local control transfer.
        // Used в CoreCLR PropagateLongJmpThroughNativeFrames (rare path).
        // Fatal — Phase 6.1.0b doesn't exercise это.
        [RuntimeExport("longjmp")]
        [UnmanagedCallersOnly(EntryPoint = "longjmp")]
        public static void Longjmp(void* env, int val)
        {
            Panic.Fail("longjmp fired (CoreCLR PropagateLongJmpThroughNativeFrames; Phase 6.1.0b stub)");
        }

        // void __std_exception_copy(struct __std_exception_data*, struct __std_exception_data*)
        // — pass-through copy of exception data.
        [RuntimeExport("__std_exception_copy")]
        [UnmanagedCallersOnly(EntryPoint = "__std_exception_copy")]
        public static void StdExceptionCopy(void* src, void* dst)
        {
            // Just zero out dst — exception body не реально used в 6.1.0b
            byte* p = (byte*)dst;
            if (p != null) { p[0] = 0; p[8] = 0; }
        }

        // void __std_exception_destroy(struct __std_exception_data*)
        [RuntimeExport("__std_exception_destroy")]
        [UnmanagedCallersOnly(EntryPoint = "__std_exception_destroy")]
        public static void StdExceptionDestroy(void* data)
        {
            // No-op
        }

        // void* __current_exception(void) — TLS slot for current exception
        [RuntimeExport("__current_exception")]
        [UnmanagedCallersOnly(EntryPoint = "__current_exception")]
        public static void* CurrentException()
        {
            return null;
        }

        // void* __current_exception_context(void) — TLS slot for context
        [RuntimeExport("__current_exception_context")]
        [UnmanagedCallersOnly(EntryPoint = "__current_exception_context")]
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
        [UnmanagedCallersOnly(EntryPoint = "__vcrt_initialize")]
        public static int VcrtInitialize() { return 1; }

        [RuntimeExport("__vcrt_uninitialize")]
        [UnmanagedCallersOnly(EntryPoint = "__vcrt_uninitialize")]
        public static int VcrtUninitialize(int isTerminate) { return 1; }

        [RuntimeExport("__vcrt_uninitialize_critical")]
        [UnmanagedCallersOnly(EntryPoint = "__vcrt_uninitialize_critical")]
        public static int VcrtUninitializeCritical() { return 1; }

        [RuntimeExport("__vcrt_thread_attach")]
        [UnmanagedCallersOnly(EntryPoint = "__vcrt_thread_attach")]
        public static int VcrtThreadAttach() { return 1; }

        [RuntimeExport("__vcrt_thread_detach")]
        [UnmanagedCallersOnly(EntryPoint = "__vcrt_thread_detach")]
        public static int VcrtThreadDetach() { return 1; }

        [RuntimeExport("__acrt_initialize")]
        [UnmanagedCallersOnly(EntryPoint = "__acrt_initialize")]
        public static int AcrtInitialize() { return 1; }

        [RuntimeExport("__acrt_uninitialize")]
        [UnmanagedCallersOnly(EntryPoint = "__acrt_uninitialize")]
        public static int AcrtUninitialize(int isTerminate) { return 1; }

        [RuntimeExport("__acrt_uninitialize_critical")]
        [UnmanagedCallersOnly(EntryPoint = "__acrt_uninitialize_critical")]
        public static int AcrtUninitializeCritical() { return 1; }

        [RuntimeExport("__acrt_thread_attach")]
        [UnmanagedCallersOnly(EntryPoint = "__acrt_thread_attach")]
        public static int AcrtThreadAttach() { return 1; }

        [RuntimeExport("__acrt_thread_detach")]
        [UnmanagedCallersOnly(EntryPoint = "__acrt_thread_detach")]
        public static int AcrtThreadDetach() { return 1; }

        [RuntimeExport("_is_c_termination_complete")]
        [UnmanagedCallersOnly(EntryPoint = "_is_c_termination_complete")]
        public static int IsCTerminationComplete() { return 0; }

        // _malloc_dbg / _free_dbg — debug CRT heap.  Fatal — kernel doesn't
        // route through these (uses SharpOS GC + kernel mm).
        [RuntimeExport("_malloc_dbg")]
        [UnmanagedCallersOnly(EntryPoint = "_malloc_dbg")]
        public static void* MallocDbg(ulong size, int blockType, byte* filename, int line)
        {
            Panic.Fail("_malloc_dbg fired (debug CRT heap; should be unreachable)");
            return null;
        }

        [RuntimeExport("_free_dbg")]
        [UnmanagedCallersOnly(EntryPoint = "_free_dbg")]
        public static void FreeDbg(void* ptr, int blockType)
        {
            Panic.Fail("_free_dbg fired");
        }
    }

    // Globals что MSVC CRT linker expects as DATA, not functions.
    internal static unsafe class CrtGlobals
    {
        // `_fltused` — CRT linker marker that floating-point code is used.
        // Just needs to exist with non-zero value.
        [RuntimeExport("_fltused")]
        public static int FltUsed = 0x9875;

        // `__security_cookie` — CFG / GS stack protection cookie. Set к
        // random value at process init; checked at function epilogues
        // via __security_check_cookie. Phase 6.1.0b: static constant.
        [RuntimeExport("__security_cookie")]
        public static ulong SecurityCookie = 0x2B992DDFA232L;

        // `__security_check_cookie` — verifies cookie matches at epilogue.
        // Phase 6.1.0b: no-op (CFG defeat).
        [RuntimeExport("__security_check_cookie")]
        [UnmanagedCallersOnly(EntryPoint = "__security_check_cookie")]
        public static void SecurityCheckCookie(ulong cookie)
        {
            // No-op для Phase 6.1.0b. Real check would compare cookie с
            // __security_cookie and __fastfail if mismatch.
        }
    }
}
