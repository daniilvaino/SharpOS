#if !SKIP_CORECLR
using System;
using System.Runtime.InteropServices;
using OS.Boot;
using OS.Boot.EH;
using OS.Hal;
using OS.Kernel.Memory;
using OS.PAL.SharpOSHost;
using SharpOS.Std.NoRuntime;
using static Iced.Intel.AssemblerRegisters;

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

        // int coreclr_create_delegate(
        //     void* hostHandle,
        //     unsigned int domainId,
        //     const char* entryPointAssemblyName,    // simple name, no .dll
        //     const char* entryPointTypeName,        // full type name
        //     const char* entryPointMethodName,      // method name
        //     void** delegate);                       // out: native fn ptr
        [DllImport("*", EntryPoint = "coreclr_create_delegate", CallingConvention = CallingConvention.Cdecl)]
        private static extern int coreclr_create_delegate(
            void* hostHandle,
            uint domainId,
            byte* entryPointAssemblyName,
            byte* entryPointTypeName,
            byte* entryPointMethodName,
            void** del);

        // int coreclr_execute_assembly(
        //     void* hostHandle, unsigned int domainId,
        //     int argc, const char** argv,
        //     const char* managedAssemblyPath, unsigned int* exitCode);
        // Normal-program entry — runs the assembly's Main (what dotnet/corerun
        // use). Stage A: host a byte-for-byte stock `dotnet build` app.
        [DllImport("*", EntryPoint = "coreclr_execute_assembly", CallingConvention = CallingConvention.Cdecl)]
        private static extern int coreclr_execute_assembly(
            void* hostHandle,
            uint domainId,
            int argc,
            byte** argv,
            byte* managedAssemblyPath,
            uint* exitCode);

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
        // [NoInlining] anchor: see comment at try block in Run(). NativeAOT
        // cannot prove an arbitrary method call is throw-free, so wrapping the
        // PInvoke here keeps the surrounding try/catch from being optimized
        // out (no .pdata personality otherwise).
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int InvokeCoreClrInitialize(
            byte* exePath, byte* domainName, int propertyCount,
            byte** propertyKeys, byte** propertyValues,
            void** hostHandle, uint* domainId)
        {
            return coreclr_initialize(exePath, domainName, propertyCount,
                propertyKeys, propertyValues, hostHandle, domainId);
        }
        private static readonly byte[] s_exePath = new byte[] { (byte)'k', (byte)'e', (byte)'r', (byte)'n', (byte)'e', (byte)'l', 0 };
        private static readonly byte[] s_domainName = new byte[] { (byte)'S', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'O', (byte)'S', 0 };

        // Property keys/values for coreclr_initialize. TRUSTED_PLATFORM_ASSEMBLIES
        // is a `;`-separated list of full paths to assemblies known to the host.
        // CoreCLR uses it for assembly resolution in coreclr_create_delegate.
        private static readonly byte[] s_propKeyTPA = new byte[] {
            (byte)'T', (byte)'R', (byte)'U', (byte)'S', (byte)'T', (byte)'E', (byte)'D', (byte)'_',
            (byte)'P', (byte)'L', (byte)'A', (byte)'T', (byte)'F', (byte)'O', (byte)'R', (byte)'M', (byte)'_',
            (byte)'A', (byte)'S', (byte)'S', (byte)'E', (byte)'M', (byte)'B', (byte)'L', (byte)'I', (byte)'E', (byte)'S', 0 };
        // \sharpos\Hello.dll;\sharpos\System.Private.CoreLib.dll
        private static readonly byte[] s_propValTPA = new byte[] {
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s', (byte)'\\',
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'.', (byte)'d', (byte)'l', (byte)'l',
            (byte)';',
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s', (byte)'\\',
            (byte)'S', (byte)'y', (byte)'s', (byte)'t', (byte)'e', (byte)'m', (byte)'.',
            (byte)'P', (byte)'r', (byte)'i', (byte)'v', (byte)'a', (byte)'t', (byte)'e', (byte)'.',
            (byte)'C', (byte)'o', (byte)'r', (byte)'e', (byte)'L', (byte)'i', (byte)'b', (byte)'.',
            (byte)'d', (byte)'l', (byte)'l', 0 };
        // APP_PATHS so probing locates assemblies by name.
        private static readonly byte[] s_propKeyAppPaths = new byte[] {
            (byte)'A', (byte)'P', (byte)'P', (byte)'_', (byte)'P', (byte)'A', (byte)'T', (byte)'H', (byte)'S', 0 };
        // step 122 experiment: APP_PATHS = "\sharpos\pwsh;\sharpos\fx;\sharpos"
        // CoreCLR пробует probe каталог под каждым из них (через ; разделитель)
        // если binder не нашёл assembly через TPA. pwsh-specific первым,
        // fx fallback, root последним.
        private static readonly byte[] s_propValAppPaths = new byte[] {
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s',
            (byte)'\\', (byte)'p', (byte)'w', (byte)'s', (byte)'h', (byte)';',
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s',
            (byte)'\\', (byte)'f', (byte)'x', (byte)';',
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s', 0 };

        // --- GC bound config (Phase 6.2 GC-arena step 1) ---
        // Workstation, non-concurrent (single-thread, no background-GC), hard
        // 64 MiB heap, 128 MiB region range, 1 MiB region, RetainVM (Decommit
        // → standby, no VA churn — matches our demand-mapped VM manager).
        // Public knob names from gc/gcconfig.h; INT values accept 0x hex.
        // Explicit const-element byte[] literals only — ILC freezes these as
        // RVA blobs (no cctor); a helper/method initializer would force a
        // class-cctor → ClassConstructorRunner #GP (nostdlib-limits §1).
        private static readonly byte[] s_kGcServer = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'S',(byte)'e',(byte)'r',(byte)'v',(byte)'e',(byte)'r',0 };
        private static readonly byte[] s_kGcConc = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'C',(byte)'o',(byte)'n',(byte)'c',(byte)'u',(byte)'r',(byte)'r',(byte)'e',(byte)'n',(byte)'t',0 };
        private static readonly byte[] s_kGcHardLim = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'H',(byte)'e',(byte)'a',(byte)'p',(byte)'H',(byte)'a',(byte)'r',(byte)'d',(byte)'L',(byte)'i',(byte)'m',(byte)'i',(byte)'t',0 };
        private static readonly byte[] s_kGcRegRange = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'R',(byte)'e',(byte)'g',(byte)'i',(byte)'o',(byte)'n',(byte)'R',(byte)'a',(byte)'n',(byte)'g',(byte)'e',0 };
        private static readonly byte[] s_kGcRegSize = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'R',(byte)'e',(byte)'g',(byte)'i',(byte)'o',(byte)'n',(byte)'S',(byte)'i',(byte)'z',(byte)'e',0 };
        private static readonly byte[] s_kGcRetainVM = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'C',(byte)'.',(byte)'R',(byte)'e',(byte)'t',(byte)'a',(byte)'i',(byte)'n',(byte)'V',(byte)'M',0 };
        // Invariant globalization — no ICU/NLS. Removes the locale P/Invoke
        // surface (GetLocaleInfoEx etc.) that throws during AppContext /
        // globalization init on bare metal. Standard for minimal embedded
        // CoreCLR with no ICU.
        private static readonly byte[] s_kGloblInv = new byte[] {
            (byte)'S',(byte)'y',(byte)'s',(byte)'t',(byte)'e',(byte)'m',(byte)'.',
            (byte)'G',(byte)'l',(byte)'o',(byte)'b',(byte)'a',(byte)'l',(byte)'i',(byte)'z',(byte)'a',(byte)'t',(byte)'i',(byte)'o',(byte)'n',(byte)'.',
            (byte)'I',(byte)'n',(byte)'v',(byte)'a',(byte)'r',(byte)'i',(byte)'a',(byte)'n',(byte)'t',0 };
        private static readonly byte[] s_vFalse = new byte[] { (byte)'f',(byte)'a',(byte)'l',(byte)'s',(byte)'e',0 };
        private static readonly byte[] s_vTrue  = new byte[] { (byte)'t',(byte)'r',(byte)'u',(byte)'e',0 };
        private static readonly byte[] s_v64M   = new byte[] { (byte)'0',(byte)'x',(byte)'4',(byte)'0',(byte)'0',(byte)'0',(byte)'0',(byte)'0',(byte)'0',0 }; // 64 MiB
        private static readonly byte[] s_v128M  = new byte[] { (byte)'0',(byte)'x',(byte)'8',(byte)'0',(byte)'0',(byte)'0',(byte)'0',(byte)'0',(byte)'0',0 }; // 128 MiB
        private static readonly byte[] s_v1M    = new byte[] { (byte)'0',(byte)'x',(byte)'1',(byte)'0',(byte)'0',(byte)'0',(byte)'0',(byte)'0',0 };           // 1 MiB

        // Stage A — managed assembly path for coreclr_execute_assembly.
        // Two targets, selector = Probes.LaunchNormalHelloCensus:
        //   true  → NormalHello.dll  (PAL/OS census probe suite, exits 42)
        //   false → PowerShellBootstrap.dll  (managed shim → PS 7.5.5)
        // Both are always built + deployed by run_build.ps1; the toggle
        // just picks which one execute_assembly loads. Const bool folds
        // the unused branch to nothing at ILC time.
        //
        // step128: PowerShellBootstrap.dll is a tiny managed shim
        // (apps/PowerShellBootstrap/) that reflection-sets SystemPolicy
        // .s_systemLockdownPolicy = None BEFORE forwarding to Microsoft
        // .PowerShell.ManagedPSEntry.Main. Direct \sharpos\pwsh\pwsh.dll
        // would land in ConstrainedLanguage mode (see step126.md).
        private static readonly byte[] s_appPathPwsh = new byte[] {
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s', (byte)'\\',
            (byte)'P', (byte)'o', (byte)'w', (byte)'e', (byte)'r', (byte)'S', (byte)'h', (byte)'e', (byte)'l', (byte)'l',
            (byte)'B', (byte)'o', (byte)'o', (byte)'t', (byte)'s', (byte)'t', (byte)'r', (byte)'a', (byte)'p',
            (byte)'.', (byte)'d', (byte)'l', (byte)'l', 0 };
        private static readonly byte[] s_appPathNormalHello = new byte[] {
            (byte)'C', (byte)':', (byte)'\\', (byte)'s', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'o', (byte)'s', (byte)'\\',
            (byte)'N', (byte)'o', (byte)'r', (byte)'m', (byte)'a', (byte)'l',
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o',
            (byte)'.', (byte)'d', (byte)'l', (byte)'l', 0 };
        private static readonly byte[] s_normalAppPath =
            Probes.LaunchNormalHelloCensus ? s_appPathNormalHello : s_appPathPwsh;

        // Names for coreclr_create_delegate — Hello.dll's SharpOSHello.Program.Run.
        private static readonly byte[] s_helloAsm = new byte[] {
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', 0 };
        private static readonly byte[] s_helloType = new byte[] {
            (byte)'S', (byte)'h', (byte)'a', (byte)'r', (byte)'p', (byte)'O', (byte)'S',
            (byte)'H', (byte)'e', (byte)'l', (byte)'l', (byte)'o', (byte)'.',
            (byte)'P', (byte)'r', (byte)'o', (byte)'g', (byte)'r', (byte)'a', (byte)'m', 0 };
        private static readonly byte[] s_helloMethod = new byte[] {
            (byte)'R', (byte)'u', (byte)'n', 0 };

        // step 72 / Frontier-C — entry for the BigStack trampoline. The
        // whole CoreCLR session runs here on a 4 MiB pre-mapped stack
        // (the 128 KiB UEFI boot stack overflows under reflection-mode
        // System.Text.Json → silent triple fault). Plain forwarder; no
        // args (Win64 trampoline calls `delegate* unmanaged<void>`).
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static void RunOnBigStackThunk() => Run();

        public static void Run()
        {
            Console.WriteLine("=== CoreClrProbe===");

            // First — dump CRT table layout (sentinels + first N entries)
            // WITHOUT executing.
            ulong xiAA = 0, xiZZ = 0, xcAA = 0, xcZZ = 0;
            SharpOSHost_GetCtorTable(&xiAA, &xiZZ, &xcAA, &xcZZ, null);
            Console.Write("XI: 0x"); Console.WriteHex(xiAA);
            Console.Write("..0x"); Console.WriteHex(xiZZ);
            Console.Write(" XC: 0x"); Console.WriteHex(xcAA);
            Console.Write("..0x"); Console.WriteHex(xcZZ);
            Console.WriteLine("");

            // Read XC table entries directly from memory. Each entry is 8 bytes.
            // Walker iterates from xcAA up to xcZZ; the bisection counter ignores
            // null entries, so an "index N" in walker terms corresponds to the
            // Nth non-null pointer in this table.
            ulong* table = (ulong*)xcAA;
            int entryCount = (int)((xcZZ - xcAA) / 8);
            int nonNull = 0;
            for (int i = 0; i < entryCount; i++)
            {
                ulong p = table[i];
                if (p == 0) continue;
                nonNull++;
                // Only print ctors near the wall (around 50-61)
                if (nonNull < 49 || nonNull > 62) continue;
                Console.Write("  ctor#");
                Console.WriteInt(nonNull);
                Console.Write(" @ [+");
                Console.WriteInt(i);
                Console.Write("] = 0x");
                Console.WriteHex(p);
                Console.WriteLine("");
            }

            // Phase 6.1.b CRITICAL FIX: Zero .data BSS region.
            // UEFI PE loader may not zero BSS portion of .data section.
            // CoreCLR's C++ static globals (g_codeRangeMap, crstDebugInfo,
            // EE state) live there and EXPECT zero initialization.
            // Without this, vtable reads / function pointer lookups get
            // garbage memory → jumps to invalid addresses.
            //
            // Section offsets (link-time RVAs from PE headers):
            //   .data init end:  0xF3CC40 (= .data start 0xF26000 + raw 0x16C40)
            //   .data end:       0xF726EC (= .data start + virt 0x4C6EC)
            //   BSS portion:     0xF3CC40 .. 0xF726EC (~219 KB)
            //
            // imageBase = xcAA - 0xFF1000 (.CRT link RVA).
            // Zero specific BSS regions in .data that CoreCLR depends on:
            //   - g_codeRangeMap (RangeSectionMap, .data offset 0x22AA0, 2064 B)
            //   - crstDebugInfo (CrstDebugInfo[4000], .data offset 0x2C310, 64000 B)
            ulong imageBase = xcAA - 0xFF1000UL;
            ulong dataStart = imageBase + 0xF26000UL;

            ulong gCRM = dataStart + 0x22AA0UL;
            ulong* p1 = (ulong*)gCRM;
            for (int z = 0; z < 2064 / 8; z++) p1[z] = 0;
            Console.WriteLine("g_codeRangeMap zeroed");

            ulong crstDi = dataStart + 0x2C310UL;
            ulong* p2 = (ulong*)crstDi;
            for (int z = 0; z < 64000 / 8; z++) p2[z] = 0;
            Console.WriteLine("crstDebugInfo zeroed");

            // Bisection — run walker с increasing limits. After each, print
            // counter to find exactly which ctor index faults.
            // Skip mask (bit N-1 = skip Nth ctor):
            //   bit 13 = skip ctor 14 (??__Es_thunkFreeList — CrstStatic::Init →
            //                          InitializeCriticalSection Win32 API)
            //   bit 53 = skip ctor 54 (??__EanalysisTimer — NormalizedTimer ctor →
            //                          minipal_hires_tick_frequency →
            //                          QueryPerformanceFrequency Win32 API)
            ulong skipMask = (1UL << 13) | (1UL << 53);

            // Run walker once with skip mask, no limit. 58 of 60 ctors run.
            int xiPhase = 0, xcPhase = 0;
            ulong lastAddr = 0;
            SharpOSHost_SetCtorSkipMask(skipMask);
            SharpOSHost_SetCtorLimit(0);
            Console.Write("skip mask = 0x");
            Console.WriteHex(skipMask);
            Console.WriteLine("");
            SharpOSHost_RunCxxCtors();
            SharpOSHost_GetCtorDiag(&xiPhase, &xcPhase, &lastAddr);
            Console.Write("walker: xi=");
            Console.WriteInt(xiPhase);
            Console.Write(" xc=");
            Console.WriteInt(xcPhase);
            Console.Write(" last=0x");
            Console.WriteHex(lastAddr);
            Console.WriteLine("");

            // --- Phase 6.1.b: TEB facade + GS_BASE MSR setup BEFORE
            // coreclr_initialize. CoreCLR jit'd / inline code emits 2982
            // distinct `mov rXX, gs:0x58` reads (TEB->TlsSlots) plus
            // gs:0x30 (TEB->Self), gs:0x10 (StackLimit). Without a TEB
            // those reads return garbage from whatever firmware left in
            // the GS shadow, leading to non-canonical pointers deep in
            // stack frames (e.g. GenerateCallStubForSig+0x74 #GP at
            // 0x00000002_0C43A7AE).
            //
            // Strategy: synthesize a minimal TEB in GcHeap memory:
            //   teb[0x30] = &teb        (TEB.Self)
            //   teb[0x58] = &tls_slots  (TEB.TlsSlots[64] pointer block)
            //   tls_slots[0] = &tls_block  (per-thread copy of TLS template)
            // Copy PE TLS template (StartAddressOfRawData..EndAddressOfRawData)
            // into tls_block. Write _tls_index = 0 into AddressOfIndex
            // so PE-emitted `mov rXX, gs:[rdx]` (where rdx = _tls_index*8)
            // resolves to tls_slots[0]. Set IA32_GS_BASE MSR (0xC0000101)
            // to &teb via wrmsr shellcode (managed code can't emit wrmsr).
            SetupTebFacade();

            // Walker passed → CoreCLR globals init'd (modulo 2 skipped
            // subsystems: UMEntryThunkFreeList + analysisTimer). Live
            // runtime path next — expect new wave of walls (Win32 imports
            // from 154 L3 classification).
            Console.WriteLine("--- calling coreclr_initialize ---");

            void* hostHandle = null;
            uint domainId = 0;

            // Stage A: TPA list is generated at build time (171 fx dll + SPC +
            // app) и shipped as \sharpos\tpa.txt — far too large for a C#
            // byte[] literal. Read it, copy into a NUL-terminated buffer so it
            // can serve as the UTF-8 TRUSTED_PLATFORM_ASSEMBLIES value.
            byte* tpaVal = null;
            if (Platform.TryReadFile("\\sharpos\\tpa.txt", out void* tpaBuf, out uint tpaSize)
                && tpaBuf != null && tpaSize > 0)
            {
                byte* nt = (byte*)NativeArena.Allocate(tpaSize + 1);
                if (nt != null)
                {
                    byte* src = (byte*)tpaBuf;
                    for (uint i = 0; i < tpaSize; i++) nt[i] = src[i];
                    nt[tpaSize] = 0;
                    tpaVal = nt;
                }
                Console.Write("tpa.txt loaded size=0x"); Console.WriteHex(tpaSize);
                Console.WriteLine("");
            }
            else
            {
                Console.WriteLine("tpa.txt NOT found — falling back to builtin TPA");
            }

            fixed (byte* exePath = s_exePath)
            fixed (byte* domainName = s_domainName)
            fixed (byte* kTpa = s_propKeyTPA)
            fixed (byte* vTpaFallback = s_propValTPA)
            fixed (byte* kApp = s_propKeyAppPaths)
            fixed (byte* vApp = s_propValAppPaths)
            fixed (byte* appPath = s_normalAppPath)
            fixed (byte* kGcSrv = s_kGcServer)   fixed (byte* kGcCon = s_kGcConc)
            fixed (byte* kGcHL  = s_kGcHardLim)  fixed (byte* kGcRR  = s_kGcRegRange)
            fixed (byte* kGcRS  = s_kGcRegSize)  fixed (byte* kGcRV  = s_kGcRetainVM)
            fixed (byte* kGInv = s_kGloblInv)
            fixed (byte* vF = s_vFalse) fixed (byte* vT = s_vTrue)
            fixed (byte* v64 = s_v64M) fixed (byte* v128 = s_v128M) fixed (byte* v1m = s_v1M)
            {
                byte* vTpa = tpaVal != null ? tpaVal : vTpaFallback;
                byte** keys   = stackalloc byte*[9] {
                    kTpa, kApp, kGcSrv, kGcCon, kGcHL, kGcRR, kGcRS, kGcRV, kGInv };
                byte** values = stackalloc byte*[9] {
                    vTpa, vApp, vF,     vF,     v64,   v128,  v1m,   vT,   vT };
                // Step 110 Part 9 — after NativeArena moved every native
                // blob out of kernel GcHeap (step 109) and precise GC walker
                // landed (step 110 Parts 1-8), the original concern that
                // motivated freezing reclamation (CoreCLR-rooted refs
                // invisible to Mark) no longer applies: kernel GcHeap holds
                // only true managed objects, and precise mark enumerates
                // them via per-frame GcInfo + registered statics.
                //
                // Leave the flag false so KernelGC.Collect actually sweeps.
                // If a regression surfaces (kernel managed ref leaking
                // through NativeArena content / cross-thread state we
                // missed), flip back to true here and audit cross-refs.
                SharpOS.Std.NoRuntime.GC.ReclamationDisabled = false;
                Console.WriteLine("[host] kernel GC reclamation enabled (precise walker)");

                // step 123: wrap CoreCLR call in try/catch so an AV inside the
                // runtime (E_FAIL throw / null deref) propagates as a managed
                // exception we can log + diagnose. NativeAOT-Release proves
                // [DllImport] PInvoke as no-throw and elides try/catch directly
                // around it (no personality in .pdata). Workaround: route call
                // through a [NoInlining] helper — the compiler can't prove the
                // helper won't throw, so EH region survives.
                int hr = unchecked((int)0x80004005);  // E_FAIL default
                try
                {
                    hr = InvokeCoreClrInitialize(
                        exePath, domainName, 9, keys, values, &hostHandle, &domainId);
                }
                catch (Exception ex)
                {
                    string msg = ex.Message ?? "<null msg>";
                    Console.WriteLine("[host] coreclr_initialize threw — message follows");
                    Console.WriteLine(msg);
                    return;
                }

                Console.Write("coreclr_initialize hr=0x");
                Console.WriteHex((ulong)(uint)hr);
                Console.WriteLine("");
                if (hr == 0)
                {
                    Console.WriteLine("=== S_OK — CoreCLR initialized ===");

                    // Stage A — host a byte-for-byte stock `dotnet build` app
                    // via the normal-program entry point (runs its Main).
                    Console.WriteLine("--- coreclr_execute_assembly(\\\\sharpos\\pwsh\\pwsh.dll) ---");
                    uint exitCode = 0xFFFFFFFF;
                    // Quiet-flag toggle DISABLED — debugging assembly-load /
                    // ntdll resolver chain. Flip back to true once stable.
                    int xr = coreclr_execute_assembly(
                        hostHandle, domainId,
                        argc: 0, argv: null,
                        managedAssemblyPath: appPath,
                        exitCode: &exitCode);
                    Console.Write("execute_assembly hr=0x"); Console.WriteHex((ulong)(uint)xr);
                    Console.Write(" exitCode="); Console.WriteInt((int)exitCode);
                    Console.WriteLine("");
                    if (xr == 0 && exitCode == 42)
                        Console.WriteLine("=== NORMAL .NET PROGRAM EXECUTED (byte-for-byte) ===");
                }
            }
        }

        // Phase 6.1.c first managed code execution. coreclr_create_delegate
        // resolves a function pointer for `SharpOSHello.Program.Run` in
        // Hello.dll (managed library compiled against our SPC.dll, placed at
        // \sharpos\Hello.dll on ESP). We invoke it passing the address of
        // SharpOSHostDiagnostics.DebugPrint as a print callback — managed
        // code calls it to write "[managed] Hello, World" to COM1.
        private static void RunHelloWorld(void* hostHandle, uint domainId)
        {
            Console.WriteLine("--- coreclr_create_delegate(Hello, SharpOSHello.Program, Run) ---");
            void* del = null;
            int chr;
            fixed (byte* a = s_helloAsm)
            fixed (byte* t = s_helloType)
            fixed (byte* m = s_helloMethod)
            {
                chr = coreclr_create_delegate(hostHandle, domainId, a, t, m, &del);
            }
            Console.Write("create_delegate hr=0x");
            Console.WriteHex((ulong)(uint)chr);
            Console.Write(" del=0x");
            Console.WriteHex((ulong)del);
            Console.WriteLine("");

            if (chr != 0 || del == null) return;

            // Cast delegate to function pointer matching Hello.Program.Run signature:
            //   int Run(IntPtr printFnPtr).
            // No CallConvCdecl modifier — on x64 there's a single calling
            // convention. CallConv types live in mscorlib's
            // System.Runtime.CompilerServices, which our std doesn't have.
            delegate* unmanaged<IntPtr, int> hello =
                (delegate* unmanaged<IntPtr, int>)del;

            // Function pointer to our [UnmanagedCallersOnly] DebugPrint.
            delegate* unmanaged<byte*, void> printFn = &SharpOSHostDiagnostics.DebugPrint;

            Console.WriteLine("--- invoking managed Hello.Run ---");
            int rv = hello((IntPtr)printFn);
            Console.Write("Hello.Run returned ");
            Console.WriteInt(rv);
            Console.WriteLine("");
            if (rv == 42)
            {
                Console.WriteLine("=== managed code execution VERIFIED ===");
            }
        }

        // Minimal TEB facade so CoreCLR's 2982 `gs:0x58` (TlsSlots) reads
        // resolve. Without this every TLS-keyed access in EEStartup,
        // CallStubGenerator, JIT init, GC barriers, etc. picks up garbage
        // and produces non-canonical pointers → #GP deep in the call chain.
        //
        // Walks the PE TLS directory dynamically (so RVAs survive image
        // rebuilds), allocates GcHeap-backed buffers, wires TEB.Self +
        // TEB.TlsSlots[0]->tls_block, copies the linker-emitted TLS template
        // into tls_block, writes _tls_index = 0, and emits a wrmsr stub
        // into BootInfo.AsmExecBuffer to load IA32_GS_BASE.
        private static void SetupTebFacade()
        {
            Console.WriteLine("--- TEB facade setup ---");

            // Phase E9.b: TEB allocation refactored into CoreClrTeb (so
            // SharpOSHost_CreateThread can call it for every new thread).
            // Here we (1) allocate the PRIMARY TEB for the boot/main thread
            // with synthetic-but-mapped stack range, (2) write its gs base
            // via the existing wrmsr shellcode, and (3) record the TEB on
            // Scheduler.Current so context switches back to main restore
            // gs base correctly.
            if (!OS.Kernel.Threading.CoreClrTeb.EnsureTemplate())
            {
                Console.WriteLine("  CoreClrTeb.EnsureTemplate failed (no PE TLS dir or CoffRuntimeFunctionTable not init)");
                return;
            }

            // Conservative stack range: 32 KiB above / below current SP,
            // page-aligned. Kernel stack is mapped well around this point,
            // and CoreCLR's stack probes / GC root scan / unwind walk
            // dereference inside this range -- synthetic values pointing
            // at unmapped VA cause #PF.
            int stackMarker = 0;
            ulong sp = (ulong)&stackMarker;
            ulong syntheticStackBase  = (sp + 0x8000UL) & ~0xFFFUL;
            ulong syntheticStackLimit = (sp - 0x8000UL) & ~0xFFFUL;

            byte* teb = OS.Kernel.Threading.CoreClrTeb.Allocate(syntheticStackBase, syntheticStackLimit);
            if (teb == null)
            {
                Console.WriteLine("  CoreClrTeb.Allocate failed");
                return;
            }

            Console.Write("  teb=0x"); Console.WriteHex((ulong)teb);
            Console.Write(" stackBase=0x"); Console.WriteHex(syntheticStackBase);
            Console.Write(" stackLimit=0x"); Console.WriteHex(syntheticStackLimit);
            Console.Write(" sp=0x"); Console.WriteHex(sp);
            Console.WriteLine("");

            // Emit wrmsr shellcode into AsmExecBuffer at offset 64 (past
            // X64Asm STI/CLI/HLT slots at 0/16/32). Args via x64 ABI:
            // RCX = TEB address.
            BootInfo bi = Platform.GetBootInfo();
            if (bi.AsmExecBuffer == null || bi.AsmExecBufferSize < 128)
            {
                Console.WriteLine("  AsmExecBuffer unavailable — cannot set GS_BASE");
                return;
            }

            byte* code = (byte*)bi.AsmExecBuffer + 64;
            // step 115 follow-up #5: wrmsr GS_BASE stub emitted via Iced.
            // RCX in = TEB address; split into EDX:EAX (high:low 32 bits)
            // and write MSR 0xC0000101 (MSR_GS_BASE).
            //
            //   mov  rax, rcx
            //   mov  rdx, rcx
            //   shr  rdx, 0x20      ; rdx = high 32 bits of TEB
            //   mov  ecx, 0xC0000101 ; MSR_GS_BASE
            //   wrmsr
            //   ret
            // for visibility; 128-byte slot has comfortable headroom.
            {
                var a = new Iced.Intel.Assembler(64);
                a.mov(rax, rcx);
                a.mov(rdx, rcx);
                a.shr(rdx, 0x20);
                a.mov(ecx, 0xC0000101);
                a.wrmsr();
                a.ret();

                var w = new GsBaseStubBufWriter(code, 64);
                a.Assemble(w, 0);
                Console.Write("  [gsbase] stub len=0x");
                Console.WriteHex((ulong)w.Count);
                Console.WriteLine("");
            }

            delegate* unmanaged<ulong, void> setGsBase = (delegate* unmanaged<ulong, void>)code;
            setGsBase((ulong)teb);

            // Phase E9.b: record TEB on the main thread so CoopSwitch
            // back to main also restores gs base. ContextBlock layout
            // has Teb at offset 0x08 (see X64Asm.CoopSwitch).
            var mainThread = OS.Kernel.Threading.Scheduler.Current;
            if (mainThread != null && mainThread.ContextBlock != null)
            {
                mainThread.Teb = teb;
                *(ulong*)(mainThread.ContextBlock + 0x08) = (ulong)teb;
            }

            Console.WriteLine("  GS_BASE = TEB via wrmsr OK; main Scheduler.Current.Teb wired");
        }

        // Local Iced CodeWriter for the wrmsr GS_BASE stub. Same shape as
        // SehDispatch / JumpStub / BigStack writers — kept inline since
        // CoreClrProbe owns its single tiny stub and the dependency cone
        // stays narrow that way.
        private sealed class GsBaseStubBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public GsBaseStubBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }
    }
}
#endif
