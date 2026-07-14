using System.Runtime;
using System.Runtime.InteropServices;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Phase E9.b -- Win32 Event PAL bridge. Forked CoreCLR's
    // CreateEventW / CreateEventExW / SetEvent / ResetEvent route
    // through these so kernel-side OS.Kernel.Threading.Event drives the
    // signal/wait machinery via Scheduler. Replaces the pre-E9.b fake-
    // handle path that lied "signaled" through every WaitForSingleObject.
    //
    // Handles are allocated via HandleTable (256-slot global; same
    // pool as Thread handles in E9.a). ThreadStubs.WaitForSingleObject
    // already has the Event branch -- it just sees a HandleTable.Lookup
    // result of type Event now instead of null.
    //
    // Win32 Event flags accepted (passed through CreateEventExW):
    //   CREATE_EVENT_MANUAL_RESET  = 0x00000001
    //   CREATE_EVENT_INITIAL_SET   = 0x00000002
    // Other flag bits and `name` are ignored -- SharpOS has no named
    // kernel objects.
    internal static unsafe class EventBridge
    {
        private const uint CREATE_EVENT_MANUAL_RESET = 0x00000001;
        private const uint CREATE_EVENT_INITIAL_SET  = 0x00000002;

        [RuntimeExport("SharpOSHost_CreateEvent")]
        public static ulong CreateEvent(int manualReset, int initialState)
        {
            if (!HandleTable.Init()) return 0;
            Event ev = new Event(manualReset != 0, initialState != 0);
            return HandleTable.Alloc(ev);
        }

        [RuntimeExport("SharpOSHost_CreateEventEx")]
        public static ulong CreateEventEx(uint flags)
        {
            if (!HandleTable.Init()) return 0;
            bool manual = (flags & CREATE_EVENT_MANUAL_RESET) != 0;
            bool initial = (flags & CREATE_EVENT_INITIAL_SET) != 0;
            Event ev = new Event(manual, initial);
            return HandleTable.Alloc(ev);
        }

        [RuntimeExport("SharpOSHost_SetEvent")]
        public static int SetEvent(ulong handle)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Event ev) return 0;
            ev.Set();
            return 1;
        }

        [RuntimeExport("SharpOSHost_ResetEvent")]
        public static int ResetEvent(ulong handle)
        {
            object? target = HandleTable.Lookup(handle);
            if (target is not Event ev) return 0;
            ev.Reset();
            return 1;
        }
    }
}
