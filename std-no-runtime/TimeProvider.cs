// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/Common/src/System/TimeProvider.cs
// Verbatim in the members kept. Cuts (marked "SharpOS cut:"):
//   - CreateTimer / ITimer: std has no System.Threading.Timer.
//   - System is a lazily created property rather than an auto-property with
//     an initializer (a static initializer in std reaches the kernel before
//     its statics exist); reads are the same.

using System.Diagnostics;

namespace System
{
    /// <summary>Provides an abstraction for time.</summary>
    public abstract class TimeProvider
    {
        /// <summary>
        /// Gets a <see cref="TimeProvider"/> that provides a clock based on <see cref="DateTimeOffset.UtcNow"/>
        /// and a high-performance time stamp based on <see cref="Stopwatch"/>.
        /// </summary>
        public static TimeProvider System => s_system ??= new SystemTimeProvider(); // SharpOS cut: was `{ get; } = new SystemTimeProvider();`
        private static TimeProvider? s_system;

        /// <summary>
        /// Initializes the <see cref="TimeProvider"/>.
        /// </summary>
        protected TimeProvider()
        {
        }

        /// <summary>
        /// Gets a <see cref="DateTimeOffset"/> value whose date and time are set to the current
        /// Coordinated Universal Time (UTC) date and time and whose offset is Zero,
        /// all according to this <see cref="TimeProvider"/>'s notion of time.
        /// </summary>
        public virtual DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;

        /// <summary>
        /// Gets a <see cref="DateTimeOffset"/> value that is set to the current date and time according to this <see cref="TimeProvider"/>'s
        /// notion of time based on <see cref="GetUtcNow"/>, with the offset set to the local time zone's offset from Coordinated Universal Time (UTC).
        /// </summary>
        public DateTimeOffset GetLocalNow()
        {
            DateTimeOffset utcDateTime = GetUtcNow();
            TimeZoneInfo zoneInfo = LocalTimeZone;
            if (zoneInfo is null)
            {
                throw new InvalidOperationException(SR.InvalidOperation_TimeProviderNullLocalTimeZone);
            }
            TimeSpan offset = zoneInfo.GetUtcOffset(utcDateTime);
            if (offset.Ticks is 0)
            {
                return utcDateTime;
            }

            long localTicks = utcDateTime.Ticks + offset.Ticks;
            if ((ulong)localTicks > (ulong)DateTime.MaxValue.Ticks) // SharpOS: upstream reads static readonly s_minDateTicks / s_maxDateTicks
            {
                localTicks = localTicks < DateTime.MinValue.Ticks ? DateTime.MinValue.Ticks : DateTime.MaxValue.Ticks;
            }

            return new DateTimeOffset(localTicks, offset);
        }

        /// <summary>
        /// Gets a <see cref="TimeZoneInfo"/> object that represents the local time zone according to this <see cref="TimeProvider"/>'s notion of time.
        /// </summary>
        /// <remarks>
        /// The default implementation returns <see cref="TimeZoneInfo.Local"/>.
        /// </remarks>
        public virtual TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

        /// <summary>
        /// Gets the frequency of <see cref="GetTimestamp"/> of high-frequency value per second.
        /// </summary>
        public virtual long TimestampFrequency => Stopwatch.Frequency;

        /// <summary>
        /// Gets the current high-frequency value designed to measure small time intervals with high accuracy in the timer mechanism.
        /// </summary>
        public virtual long GetTimestamp() => Stopwatch.GetTimestamp();

        /// <summary>
        /// Gets the elapsed time between two timestamps retrieved using <see cref="GetTimestamp"/>.
        /// </summary>
        public TimeSpan GetElapsedTime(long startingTimestamp, long endingTimestamp)
        {
            long timestampFrequency = TimestampFrequency;
            if (timestampFrequency <= 0)
            {
                throw new InvalidOperationException("The TimestampFrequency must be greater than zero."); // SharpOS: SR text spelled out
            }

            return new TimeSpan((long)((endingTimestamp - startingTimestamp) * ((double)TimeSpan.TicksPerSecond / timestampFrequency)));
        }

        /// <summary>
        /// Gets the elapsed time since the <paramref name="startingTimestamp"/> value retrieved using <see cref="GetTimestamp"/>.
        /// </summary>
        public TimeSpan GetElapsedTime(long startingTimestamp) => GetElapsedTime(startingTimestamp, GetTimestamp());

        // SharpOS cut: CreateTimer(TimerCallback, object?, TimeSpan, TimeSpan) and SystemTimeProviderTimer.

        /// <summary>
        /// Used to create a <see cref="TimeProvider"/> instance returned from <see cref="System"/> and uses the default implementation
        /// provided by <see cref="TimeProvider"/> which uses <see cref="DateTimeOffset.UtcNow"/> and <see cref="Stopwatch"/>.
        /// </summary>
        private sealed class SystemTimeProvider : TimeProvider
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="SystemTimeProvider"/>.
            /// </summary>
            internal SystemTimeProvider() : base()
            {
            }
        }
    }
}
