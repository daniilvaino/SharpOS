namespace OS.PAL.SharpOSHost
{
    // Phase E9 -- generic kernel-side handle table for Win32-style PAL
    // handles (HANDLE = opaque pointer-sized). Slot-based, fixed capacity
    // for now (256 handles is more than CoreCLR ever wants concurrently
    // in our minimal hosted scenarios; bump if a real workload demands).
    //
    // Handles are 1-indexed (0 = invalid / null handle). Returned value
    // is `(ulong)(index + 1)`. CoreCLR treats them as opaque pointer-
    // sized, never dereferences, just compares and passes back into
    // CloseHandle / WaitForSingleObject etc.
    //
    // Stored objects are typed at the call site:
    //   CreateThread     -> kernel.Threading.Thread
    //   CreateEvent      -> kernel.Threading.Event
    //   CreateSemaphore  -> kernel.Threading.Semaphore
    //   CreateMutex      -> kernel.Threading.Mutex
    //
    // Single-CPU cooperative -- no lock needed on Alloc/Free today; when
    // preemption / SMP land, wrap in SpinYieldLock (E6 infra ready).
    //
    // Init() must be called before first Alloc/Lookup. Lazy init in a
    // field initializer would trip ClassConstructorRunner (see CLAUDE.md
    // §ClassConstructorRunner trap); same pattern as Scheduler.Init().
    internal static unsafe class HandleTable
    {
        public const int MaxHandles = 256;

        private static object?[]? s_slots;
        private static bool s_initialized;

        public static bool IsInitialized => s_initialized;

        // One-time init. Safe to call multiple times.
        public static bool Init()
        {
            if (s_initialized) return true;
            s_slots = new object?[MaxHandles];
            s_initialized = true;
            return true;
        }

        // Allocate the lowest free slot, store `obj`, return 1-based handle.
        // Returns 0 on exhaustion or before Init.
        public static ulong Alloc(object obj)
        {
            if (!s_initialized) return 0;
            if (obj == null) return 0;
            object?[] slots = s_slots!;
            for (int i = 0; i < MaxHandles; i++)
            {
                if (slots[i] == null)
                {
                    slots[i] = obj;
                    return (ulong)(i + 1);
                }
            }
            return 0;
        }

        // Return the object stored at this handle, or null on invalid /
        // already-closed handle.
        public static object? Lookup(ulong handle)
        {
            if (!s_initialized) return null;
            if (handle == 0 || handle > (ulong)MaxHandles) return null;
            return s_slots![(int)(handle - 1)];
        }

        // Same as Lookup but as typed cast. Returns null on type mismatch
        // or invalid handle.
        public static T? LookupAs<T>(ulong handle) where T : class
        {
            object? o = Lookup(handle);
            return o as T;
        }

        // Release the slot. Returns true if a non-null entry was present.
        public static bool Free(ulong handle)
        {
            if (!s_initialized) return false;
            if (handle == 0 || handle > (ulong)MaxHandles) return false;
            int idx = (int)(handle - 1);
            object? was = s_slots![idx];
            s_slots[idx] = null;
            return was != null;
        }

        // Diagnostic: count of allocated handles.
        public static int LiveCount()
        {
            if (!s_initialized) return 0;
            int n = 0;
            object?[] slots = s_slots!;
            for (int i = 0; i < MaxHandles; i++)
                if (slots[i] != null) n++;
            return n;
        }
    }
}
