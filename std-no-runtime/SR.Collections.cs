// SR keys of System.Private.CoreLib and System.Collections (dotnet/runtime
// v8.0.27 Strings.resx) for AggregateException and BitArray (step224).

namespace System
{
    internal static partial class SR
    {
        internal const string AggregateException_ctor_DefaultMessage = @"One or more errors occurred.";
        internal const string AggregateException_ctor_InnerExceptionNull = @"An element of innerExceptions was null.";
        internal const string AggregateException_InnerException = @"(Inner Exception #{0}) ";
        internal const string Arg_ArrayLengthsDiffer = @"Array lengths must be the same.";
        internal const string InvalidOperation_EnumEnded = @"Enumeration already finished.";
        internal const string Argument_ArrayTooLarge = @"The input array length must not exceed Int32.MaxValue / {0}. Otherwise BitArray.Length would exceed Int32.MaxValue.";
        internal const string Arg_BitArrayTypeUnsupported = @"Only supported array types for CopyTo on BitArrays are Boolean[], Int32[] and Byte[].";
    }
}
