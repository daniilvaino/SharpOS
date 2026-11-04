// What CoreLib's kernel tier needs from the kernel, installed by it at boot.
//
// CoreLib is its own assembly (System.Private.CoreLib) and the kernel (OS) is
// built over it, so CoreLib cannot name the kernel's types. The few places its
// kernel tier reaches the kernel — the kernel heap before the managed one
// exists, the console, a fatal stop, the pipe transport — go through these,
// the way stock CoreLib reaches its OS through Interop. All of them are slow
// paths: the allocator's fast path, strings in the managed heap, Stopwatch and
// the rest never come here.
//
// Plain static fields of primitive and pointer types: they live in the image's
// data and work before the GC statics exist (Phase 1), which is when the heap
// and the console hooks are first needed.

namespace SharpOS.Std.NoRuntime
{
    public static unsafe class KernelServices
    {
        /// <summary>KernelHeap.Alloc; null until the kernel heap is up.</summary>
        public static delegate*<uint, void*> HeapAlloc;

        /// <summary>Panic.Fail: a stop that does not return.</summary>
        public static delegate*<string, void> Fail;

        /// <summary>The kernel console (OS.Hal.Console).</summary>
        public static delegate*<string, void> Write;
        public static delegate*<string, void> WriteLine;
        public static delegate*<char, void> WriteChar;

        /// <summary>An exception is being dispatched on this thread (ExInfoHead).</summary>
        public static delegate*<bool> ExceptionInFlight;

        internal static void FailOrHalt(string message)
        {
            if (Fail != null) Fail(message);
            while (true) { }
        }
    }
}
