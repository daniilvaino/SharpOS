// Ported from dotnet/runtime (.NET 10 fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/ThrowHelper.cs
// The members the ported date/time stack (DateTime, TimeSpan, DateOnly,
// TimeOnly, Globalization date/time formatting and parsing) calls; bodies
// verbatim but where marked.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowArgumentException_InvalidTimeSpanStyles()
        {
            throw new ArgumentException(SR.Argument_InvalidTimeSpanStyles, "styles");
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_Year()
        {
            // SharpOS: upstream goes through GetArgumentOutOfRangeException(ExceptionArgument.year, ExceptionResource.ArgumentOutOfRange_Year).
            throw new ArgumentOutOfRangeException("year", SR.ArgumentOutOfRange_Year);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_Month(int month)
        {
            throw new ArgumentOutOfRangeException(nameof(month), month, SR.ArgumentOutOfRange_Month);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_DayNumber(int dayNumber)
        {
            throw new ArgumentOutOfRangeException(nameof(dayNumber), dayNumber, SR.ArgumentOutOfRange_DayNumber);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_BadYearMonthDay()
        {
            throw new ArgumentOutOfRangeException(null, SR.ArgumentOutOfRange_BadYearMonthDay);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_BadHourMinuteSecond()
        {
            throw new ArgumentOutOfRangeException(null, SR.ArgumentOutOfRange_BadHourMinuteSecond);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentOutOfRange_TimeSpanTooLong()
        {
            throw new ArgumentOutOfRangeException(null, SR.Overflow_TimeSpanTooLong);
        }

        [DoesNotReturn]
        internal static void ThrowOverflowException_NegateTwosCompNum()
        {
            throw new OverflowException(SR.Overflow_NegateTwosCompNum);
        }

        [DoesNotReturn]
        internal static void ThrowOverflowException_TimeSpanTooLong()
        {
            throw new OverflowException(SR.Overflow_TimeSpanTooLong);
        }

        [DoesNotReturn]
        internal static void ThrowOverflowException_TimeSpanDuration()
        {
            throw new OverflowException(SR.Overflow_Duration);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException_Arg_CannotBeNaN()
        {
            throw new ArgumentException(SR.Arg_CannotBeNaN);
        }

        [DoesNotReturn]
        internal static void ThrowArgumentException_Arg_CannotBeNaN(ExceptionArgument argument)
        {
            throw new ArgumentException(SR.Arg_CannotBeNaN, GetArgumentName(argument));
        }
    }
}
