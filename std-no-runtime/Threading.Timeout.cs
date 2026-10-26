// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Timeout.cs (MIT)
// Verbatim but for the TimeSpan constructor: -1 ms as ticks.

namespace System.Threading
{
    // A constant used by methods that take a timeout (Object.Wait, Thread.Sleep
    // etc) to indicate that no timeout should occur.
    public static class Timeout
    {
        public static readonly TimeSpan InfiniteTimeSpan = new TimeSpan(Infinite * TimeSpan.TicksPerMillisecond);

        public const int Infinite = -1;
        internal const uint UnsignedInfinite = unchecked((uint)-1);
    }
}
