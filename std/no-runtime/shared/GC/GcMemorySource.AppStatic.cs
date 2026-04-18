// GcMemorySource backed by a static byte pool inside the app binary.
// Linked into HelloSharpFs.csproj, FetchApp.csproj (not OS.csproj — kernel
// uses its own KernelHeap-backed variant).
//
// The pool is a fixed-size struct stored in the binary's .bss, sized via
// StructLayout(Size = ...). Each AllocateBlock bumps a cursor through it.
// Phase 2 only — phase 4 will migrate apps to the unified GcHeap properly
// (strings/objects allocated here get reclaimed by Mark/Sweep).

using System.Runtime.InteropServices;

namespace SharpOS.Std.NoRuntime
{
    [StructLayout(LayoutKind.Sequential, Size = PoolSizeLiteral.Size)]
    internal struct GcAppPool { }

    internal static class PoolSizeLiteral
    {
        public const int Size = 1 * 1024 * 1024; // 1 MB
    }

    internal static unsafe class GcMemorySource
    {
        private static GcAppPool s_pool;
        private static uint s_used;

        public static bool IsInitialized => true;

        public static void* AllocateBlock(uint size)
        {
            uint aligned = (size + 15u) & ~15u;

            if ((ulong)s_used + aligned > (ulong)PoolSizeLiteral.Size)
                return null;

            fixed (GcAppPool* basePtr = &s_pool)
            {
                void* result = (byte*)basePtr + s_used;
                s_used += aligned;
                return result;
            }
        }
    }
}
