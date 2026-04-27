// Class-constructor runner — implements the helpers ILC emits before
// every read of a static reference field with an initializer.
//
// Without this support, ILC in --resilient mode emits fallback stubs
// that return non-canonical sentinel pointers (high bits 0xF000...);
// dereferencing those gives the #PF we hit in step 34 SystemBanner.
//
// Implementation ported verbatim from Test.CoreLib (which itself is
// the minimum-viable subset of System.Private.CoreLib's full version).
//
// Single-threaded contract: kernel is single-threaded for now, so the
// CAS loop is overkill but harmless. When Phase 3.5 brings SMP we'll
// already have the right structure.

using System.Threading;
using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    // Fixed runtime-known layout. ILC emits one StaticClassConstructionContext
    // per type with a cctor; the structure lives in the binary's data section,
    // is zero-initialized, and is passed by reference to the runner methods.
    //
    // Layout MUST match: cctorMethodAddress (8) + initialized (4). ILC reads
    // the address and writes the flag; we mediate everything between.
    [StructLayout(LayoutKind.Sequential)]
    public struct StaticClassConstructionContext
    {
        public IntPtr cctorMethodAddress;
        public volatile int initialized;
    }

    internal static class ClassConstructorRunner
    {
        // Two entry points ILC may emit, one per static-base flavor.
        // Both run the cctor (if needed) then pass through the static base
        // pointer. The runtime uses the return value as the actual address
        // of the static field storage.

        private static unsafe object CheckStaticClassConstructionReturnGCStaticBase(
            ref StaticClassConstructionContext context, object gcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return gcStaticBase;
        }

        private static unsafe IntPtr CheckStaticClassConstructionReturnNonGCStaticBase(
            ref StaticClassConstructionContext context, IntPtr nonGcStaticBase)
        {
            CheckStaticClassConstruction(ref context);
            return nonGcStaticBase;
        }

        // Race-aware initializer. State machine on context.initialized:
        //   0 → 2 (winner — runs cctor) → 1 (done)
        //   0 → blocked (loser — spin until winner reaches 1)
        //   1 → fast return (already done)
        //
        // CAS guarantees only one thread runs the cctor body. Memory barrier
        // after the cctor ensures any writes inside it become visible before
        // the initialized flag flips to 1.
        private static unsafe void CheckStaticClassConstruction(
            ref StaticClassConstructionContext context)
        {
            // SINGLE-THREADED simplification. The original Test.CoreLib version
            // spins when state == 2 (another thread mid-cctor). We're single-
            // threaded — state == 2 means WE are mid-cctor on this very stack,
            // recursing through a helper that read state without knowing we're
            // already inside. Spinning would deadlock. Return immediately:
            // the partially-initialized statics are still safe for the
            // recursive access (ILC already laid out the storage).
            //
            // SMP TODO: replace with per-thread cctor execution stack
            // (currentlyExecuting array of context*'s). Until Phase 3.5, the
            // single-threaded shortcut is correct.
            int oldState = context.initialized;
            if (oldState == 1) return;          // already done
            if (oldState == 2) return;          // mid-cctor — same thread, recursive call

            // First-time entry: state == 0. Claim the slot and run the cctor.
            // CAS is overkill on single-thread but harmless and keeps the
            // shape ready for SMP.
            if (Interlocked.CompareExchange(ref context.initialized, 2, 0) == 0)
            {
                // Invoke the cctor body via the function pointer ILC stamped.
                ((delegate*<void>)context.cctorMethodAddress)();

                Interlocked.MemoryBarrier();
                context.initialized = 1;
            }
        }
    }
}
