// SR keys of System.Private.CoreLib (dotnet/runtime v8.0.27 Strings.resx) for
// SemaphoreSlim, WaitHandle and Stream's timeouts (step223).

namespace System
{
    internal static partial class SR
    {
        internal const string Lazy_StaticInit_InvalidOperation = @"ValueFactory returned null.";
        internal const string Lazy_CreateValue_NoParameterlessCtorForT = @"The lazily-initialized type does not have a public, parameterless constructor.";
        internal const string Threading_SemaphoreFullException = @"Adding the specified count to the semaphore would cause it to exceed its maximum count.";
        internal const string InvalidOperation_TimeoutsNotSupported = @"Timeouts are not supported on this stream.";
        internal const string ArgumentNull_ArrayElement = @"At least one element in the specified array was null.";
        internal const string ArgumentOutOfRange_NeedNonNegOrNegative1 = @"Number must be either non-negative and less than or equal to Int32.MaxValue or -1.";
        internal const string Argument_EmptyWaithandleArray = @"Waithandle array may not be empty.";
        internal const string SemaphoreSlim_Release_CountWrong = @"The releaseCount argument must be greater than zero.";
        internal const string SemaphoreSlim_Wait_TimeoutWrong = @"The timeout must represent a value between -1 and Int32.MaxValue, inclusive.";
        internal const string SemaphoreSlim_ctor_InitialCountWrong = @"The initialCount argument must be non-negative and less than or equal to the maximumCount.";
        internal const string SemaphoreSlim_ctor_MaxCountWrong = @"The maximumCount argument must be a positive number. If a maximum is not required, use the constructor without a maxCount parameter.";
    }
}
