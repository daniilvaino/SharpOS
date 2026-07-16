// System.DateTime — app-tier resolution stub. No RTC service in the app
// service table yet: Now is the epoch and Millisecond is always 0, which
// makes DateTime-seeded RNGs (ManagedDoom QuitConfirm/WipeEffect use
// DateTime.Now.Millisecond) deterministic. TODO: route to the kernel RTC
// (Hal.Rtc) via a service slot.

namespace System
{
    public readonly struct DateTime
    {
        public static DateTime Now => default;

        public int Millisecond => 0;
    }
}
