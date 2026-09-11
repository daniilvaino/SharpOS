using System.Runtime;
using System.Threading;

namespace SharpOS.AppSdk
{
    // Compare-and-swap for an app, written as machine code because C# cannot
    // say it.
    //
    // std's Interlocked falls back to read-compare-write when no backend is
    // installed, which is correct until a second thread exists. Apps now have
    // threads (service table v3) and the kernel preempts them, so the fallback
    // is no longer good enough here.
    //
    // Same shape as ByRefAssignRefStub next door: keep a managed method so ILC
    // reserves an address for it, then overwrite the body with the instructions
    // at startup. Nothing needs to come from the kernel — cmpxchg is a plain
    // instruction, not a runtime service.
    internal static unsafe class AtomicStub
    {
        // Win64: RCX = location, EDX = value, R8D = comparand → EAX = old.
        //   44 89 C0       mov eax, r8d
        //   F0 0F B1 11    lock cmpxchg [rcx], edx
        //   C3             ret
        private static uint CompareExchange32(uint* location, uint value, uint comparand)
        {
            // Unpatched fallback. Reached only if patching failed, and wrong in
            // exactly the way the patch exists to fix — so say so rather than
            // return a plausible answer.
            AppHost.WriteError("AtomicStub: 32-bit CAS not patched\r\n");
            for (; ; ) { }
        }

        // Win64: RCX = location, RDX = value, R8 = comparand → RAX = old.
        //   4C 89 C0          mov rax, r8
        //   F0 48 0F B1 11    lock cmpxchg [rcx], rdx
        //   C3                ret
        private static ulong CompareExchange64(ulong* location, ulong value, ulong comparand)
        {
            AppHost.WriteError("AtomicStub: 64-bit CAS not patched\r\n");
            for (; ; ) { }
        }

        //   0F AE F0    mfence
        //   C3          ret
        private static void Barrier()
        {
            AppHost.WriteError("AtomicStub: barrier not patched\r\n");
            for (; ; ) { }
        }

        /// <summary>
        /// Writes the three bodies and hands them to std. Returns false if the
        /// pages could not be made writable, in which case Interlocked keeps
        /// its managed fallback and says so through IsAtomic.
        /// </summary>
        public static bool Install()
        {
            delegate*<uint*, uint, uint, uint> cas32 = &CompareExchange32;
            delegate*<ulong*, ulong, ulong, ulong> cas64 = &CompareExchange64;
            delegate*<void> barrier = &Barrier;

            byte* p32 = (byte*)cas32;
            byte* p64 = (byte*)cas64;
            byte* pBar = (byte*)barrier;

            if (p32 == null || p64 == null || pBar == null) return false;

            // Bytes written inline rather than from a static array: a static
            // array field would give this type a class constructor, and the
            // check ILC emits for one does not work here.
            p32[0] = 0x44; p32[1] = 0x89; p32[2] = 0xC0;               // mov eax, r8d
            p32[3] = 0xF0;                                             // lock
            p32[4] = 0x0F; p32[5] = 0xB1; p32[6] = 0x11;               // cmpxchg [rcx], edx
            p32[7] = 0xC3;                                             // ret

            p64[0] = 0x4C; p64[1] = 0x89; p64[2] = 0xC0;               // mov rax, r8
            p64[3] = 0xF0;                                             // lock
            p64[4] = 0x48; p64[5] = 0x0F; p64[6] = 0xB1; p64[7] = 0x11; // cmpxchg [rcx], rdx
            p64[8] = 0xC3;                                             // ret

            pBar[0] = 0x0F; pBar[1] = 0xAE; pBar[2] = 0xF0;            // mfence
            pBar[3] = 0xC3;                                            // ret

            AtomicBackend.Install(cas32, cas64, barrier);
            return AtomicBackend.IsAtomic;
        }
    }
}
