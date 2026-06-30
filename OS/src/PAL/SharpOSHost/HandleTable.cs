using OS.Hal;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9 — generic kernel-side handle table for Win32-style PAL handles.
    // Slot-based, fixed capacity. Handles are 1-indexed (0 = invalid).
    //
    // Capacity history:
    //   step126 default: 256 — fine for AOT probes, not enough for PS
    //   step126.X bump: 4096 — PS Pipeline + ObjectStream + telemetry +
    //     finalizer + ThreadPool workers easily exceed 256 concurrent.
    //     This is a temp constant bump; a generation-safe handle table is
    //     the proper long-term answer (per step125 done note).
    //
    // Single-CPU cooperative — no lock needed on Alloc/Free.
    // Init() must be called before first Alloc/Lookup. Lazy init in a
    // field initializer would trip ClassConstructorRunner.
    internal static unsafe class HandleTable
    {
        public const int MaxHandles = 4096;

        private static object?[]? s_slots;
        private static bool s_initialized;

        // Lifetime counters — single integers, lock-free under cooperative
        // scheduling. Diagnostic-only; not on the hot path.
        private static ulong s_allocTotal;
        private static ulong s_freeTotal;
        private static int   s_liveHighWater;
        private static ulong s_allocFailures;

        public static bool IsInitialized => s_initialized;

        public static bool Init()
        {
            if (s_initialized) return true;
            s_slots = new object?[MaxHandles];
            s_initialized = true;
            return true;
        }

        // Allocate the lowest free slot, store `obj`, return 1-based handle.
        // Returns 0 on exhaustion or before Init. On first exhaustion event
        // emits a one-shot summary so the failure shape is visible in the
        // log without flooding subsequent prompts.
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
                    s_allocTotal++;
                    int live = (int)(s_allocTotal - s_freeTotal);
                    if (live > s_liveHighWater) s_liveHighWater = live;
                    return (ulong)(i + 1);
                }
            }
            s_allocFailures++;
            DumpStats(prefix: "[HT-FULL]");
            return 0;
        }

        public static object? Lookup(ulong handle)
        {
            if (!s_initialized) return null;
            if (handle == 0 || handle > (ulong)MaxHandles) return null;
            return s_slots![(int)(handle - 1)];
        }

        public static T? LookupAs<T>(ulong handle) where T : class
        {
            object? o = Lookup(handle);
            return o as T;
        }

        // Release the slot. Returns true if a non-null entry was present.
        // Emits a one-line trace when a CloseHandle attempts to free an
        // already-empty slot — helps spot double-close / dangling references.
        public static bool Free(ulong handle)
        {
            if (!s_initialized) return false;
            if (handle == 0 || handle > (ulong)MaxHandles)
            {
                EmitStr("[HT-Free-OOR] handle=0x"); EmitHex(handle); EmitStr("\n");
                return false;
            }
            int idx = (int)(handle - 1);
            object? was = s_slots![idx];
            s_slots[idx] = null;
            if (was != null)
            {
                s_freeTotal++;
                return true;
            }
            EmitStr("[HT-Free-empty] handle=0x"); EmitHex(handle); EmitStr("\n");
            return false;
        }

        public static int LiveCount()
        {
            if (!s_initialized) return 0;
            int n = 0;
            object?[] slots = s_slots!;
            for (int i = 0; i < MaxHandles; i++)
                if (slots[i] != null) n++;
            return n;
        }

        // One-line stats summary, plus per-type breakdown when the table is
        // exhausted. Bypasses Console.Quiet via Platform.WriteChar — this is
        // critical diagnostic, like Panic; the kernel is about to halt and we
        // need the cause visible regardless of the chatter gate.
        public static void DumpStats(string prefix)
        {
            EmitStr(prefix);
            EmitStr(" live="); EmitInt(LiveCount());
            EmitStr(" cap="); EmitInt(MaxHandles);
            EmitStr(" alloc=0x"); EmitHex(s_allocTotal);
            EmitStr(" free=0x"); EmitHex(s_freeTotal);
            EmitStr(" highWater="); EmitInt(s_liveHighWater);
            EmitStr(" failures=0x"); EmitHex(s_allocFailures);
            EmitStr("\n");

            if (!s_initialized) return;
            object?[] slots = s_slots!;
            int events = 0, semaphores = 0, threads = 0, mutexes = 0, other = 0;
            for (int i = 0; i < MaxHandles; i++)
            {
                object? o = slots[i];
                if (o == null) continue;
                if (o is Event)            events++;
                else if (o is Semaphore)   semaphores++;
                else if (o is Thread)      threads++;
                else if (o is Win32Mutex)  mutexes++;
                else                       other++;
            }
            EmitStr(prefix);
            EmitStr(" by-type Event="); EmitInt(events);
            EmitStr(" Semaphore="); EmitInt(semaphores);
            EmitStr(" Thread="); EmitInt(threads);
            EmitStr(" Mutex="); EmitInt(mutexes);
            EmitStr(" other="); EmitInt(other);
            EmitStr("\n");
        }

        // Bypass-Quiet writers — go through OS.Hal.Platform directly so the
        // [HT-FULL] / [HT-Free-*] traces survive interactive-mode muting.
        private static void EmitStr(string s)
        {
            for (int i = 0; i < s.Length; i++) Platform.WriteChar(s[i]);
        }
        private static void EmitInt(int v)
        {
            if (v == 0) { Platform.WriteChar('0'); return; }
            if (v < 0) { Platform.WriteChar('-'); v = -v; }
            char* digits = stackalloc char[12];
            int len = 0;
            while (v > 0) { digits[len++] = (char)('0' + v % 10); v /= 10; }
            for (int i = len - 1; i >= 0; i--) Platform.WriteChar(digits[i]);
        }
        private static void EmitHex(ulong v)
        {
            char* digits = stackalloc char[16];
            int len = 0;
            do { int n = (int)(v & 0xF); digits[len++] = (char)(n < 10 ? '0' + n : 'A' + n - 10); v >>= 4; } while (v != 0);
            for (int i = len - 1; i >= 0; i--) Platform.WriteChar(digits[i]);
        }
    }
}
