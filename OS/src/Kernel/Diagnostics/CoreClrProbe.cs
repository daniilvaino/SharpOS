using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase 6.1.a probe — first call to CoreCLR's coreclr_initialize from
    // kernel boot path. Goal: trigger the first SharpOSHost_* / CrtAndEh
    // fatal stub, get empirical signal what CoreCLR wants on init.
    //
    // CoreCLR linked statically into kernel image (Phase 6.1.0). Now we
    // actually invoke it. Expected outcome: Panic.Fail с named stub message
    // pointing к first reachable capability we have not implemented.
    //
    // After first panic resolved → re-run, find next. Iterate until
    // coreclr_initialize returns S_OK (Phase 6.1.a closure criterion).
    //
    // DllImport with library name "*" tells NativeAOT linker — find
    // the symbol в statically-linked binary itself, не в external DLL.
    internal static unsafe class CoreClrProbe
    {
        // int coreclr_initialize(
        //     const char* exePath,
        //     const char* appDomainFriendlyName,
        //     int propertyCount,
        //     const char** propertyKeys,
        //     const char** propertyValues,
        //     void** hostHandle,
        //     uint* domainId);
        [DllImport("*", EntryPoint = "coreclr_initialize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int coreclr_initialize(
            byte* exePath,
            byte* appDomainFriendlyName,
            int propertyCount,
            byte** propertyKeys,
            byte** propertyValues,
            void** hostHandle,
            uint* domainId);

        // Walk .CRT$XCA..$XCZ pointers and invoke each — required by
        // CoreCLR C++ globals (vtables, registries). SharpOS kernel
        // entry EfiMain bypasses normal __scrt_initialize_crt, so this
        // is our manual bootstrap before any CoreCLR code runs.
        [DllImport("*", EntryPoint = "SharpOSHost_RunCxxCtors", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SharpOSHost_RunCxxCtors();

        // Diagnostic accessor — call after CRT walker faults to see how
        // many ctors ran before death.
        [DllImport("*", EntryPoint = "SharpOSHost_GetCtorDiag", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SharpOSHost_GetCtorDiag(int* xiPhase, int* xcPhase, ulong* lastAddr);

        // Set ctor limit для bisecting failing one. 0 = run все.
        [DllImport("*", EntryPoint = "SharpOSHost_SetCtorLimit", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SharpOSHost_SetCtorLimit(int limit);

        // Bitmask of ctor indices (1-based) to SKIP. bit 0 = skip 1st, bit 3 = skip 4th.
        [DllImport("*", EntryPoint = "SharpOSHost_SetCtorSkipMask", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SharpOSHost_SetCtorSkipMask(ulong mask);

        // Read ctor table layout WITHOUT executing — for layout diagnostic.
        [DllImport("*", EntryPoint = "SharpOSHost_GetCtorTable", CallingConvention = CallingConvention.Cdecl)]
        private static extern void SharpOSHost_GetCtorTable(
            ulong* xiAStart, ulong* xiZEnd,
            ulong* xcAStart, ulong* xcZEnd,
            ulong* firstFive);

        // UTF-8 strings as stack-allocated null-terminated byte arrays.
        // "kernel\0" — kernel boot identity (no .exe per se, just kernel image).
        private static readonly byte[] s_exePath = new byte[] { (byte)'k', (byte)'e', (byte)'r', (byte)'n', (byte)'e', (byte)'l', 0 };
        private static readonly byte[] s_domainName = new byte[] { (byte)'S', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'O', (byte)'S', 0 };

        public static void Run()
        {
            Console.WriteLine("=== CoreClrProbe===");

            // Bisection — call walker с increasing limits. Report counts
            // after each. SKIP coreclr_initialize entirely (its fault would
            // confuse signal). Find the limit where walker itself crashes.
            int xiPhase = 0, xcPhase = 0;
            ulong lastAddr = 0;
            // HACK — patch CRT atexit sentinel from 0 to -1 before walker.
            // The libcmtd's _register_thread_local_exe_atexit_callback expects
            // global at 0x180F6F510 (link RVA 0xF6F510) initialized к -1
            // ("uninitialized table"). С zero-init it goes wrong path → crash.
            // Compute loaded address from sentinel diagnostic, write -1.
            ulong xiAA = 0, xiZZ = 0, xcAA = 0, xcZZ = 0;
            SharpOSHost_GetCtorTable(&xiAA, &xiZZ, &xcAA, &xcZZ, null);
            // xcAA = loaded __xc_a_sentinel = image_base + link_RVA_0xFED000
            ulong imageBase = xcAA - 0xFED000UL;
            ulong* atexitSentinel = (ulong*)(imageBase + 0xF6F510UL);
            *atexitSentinel = 0xFFFFFFFFFFFFFFFFUL; // -1

            Console.Write("patched sentinel @ 0x");
            Console.WriteHex((ulong)atexitSentinel);
            Console.WriteLine("");

            SharpOSHost_SetCtorSkipMask(0);

            int[] limits = new int[] { 4, 7, 10, 15, 20, 30, 60 };
            foreach (int limit in limits)
            {
                Console.Write("limit=");
                Console.WriteInt(limit);
                Console.Write(" (skip ctor4): ");

                SharpOSHost_SetCtorLimit(limit);
                SharpOSHost_RunCxxCtors();

                SharpOSHost_GetCtorDiag(&xiPhase, &xcPhase, &lastAddr);
                Console.Write("xi=");
                Console.WriteInt(xiPhase);
                Console.Write(" xc=");
                Console.WriteInt(xcPhase);
                Console.Write(" last=0x");
                Console.WriteHex(lastAddr);
                Console.WriteLine("");
            }
            Console.WriteLine("--- skip-4 bisection done ---");
            return;

            void* hostHandle = null;
            uint domainId = 0;

            fixed (byte* exePath = s_exePath)
            fixed (byte* domainName = s_domainName)
            {
                int hr = coreclr_initialize(
                    exePath,
                    domainName,
                    propertyCount: 0,
                    propertyKeys: null,
                    propertyValues: null,
                    hostHandle: &hostHandle,
                    domainId: &domainId);

                if (hr == 0)
                {
                    Console.WriteLine("coreclr_initialize returned S_OK!");
                    Console.WriteLine("Phase 6.1.a achieved");
                }
                else
                {
                    Console.WriteLine("coreclr_initialize returned non-zero HRESULT");
                    // Could format hr as hex here but if we got back at all
                    // some progress was made.
                }
            }
        }
    }
}
