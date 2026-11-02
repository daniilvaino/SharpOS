// SR keys of System.Private.CoreLib (dotnet/runtime v8.0.27 Strings.resx) for SafeBuffer
// and UnmanagedMemoryStream (ArgumentOutOfRange_Enum moved here from IO/Compression).

namespace System
{
    internal static partial class SR
    {
        internal const string ArgumentOutOfRange_Enum = @"Enum value was out of legal range.";
        internal const string Arg_BufferTooSmall = @"Not enough space available in the buffer.";
        internal const string ArgumentOutOfRange_AddressSpace = @"The number of bytes cannot exceed the virtual address space on a 32 bit machine.";
        internal const string ArgumentOutOfRange_LengthGreaterThanCapacity = @"The length cannot be greater than the capacity.";
        internal const string ArgumentOutOfRange_UIntPtrMax = @"The length of the buffer must be less than the maximum UIntPtr value for your platform.";
        internal const string ArgumentOutOfRange_UnmanagedMemStreamLength = @"UnmanagedMemoryStream length must be non-negative and less than 2^63 - 1 - baseAddress.";
        internal const string ArgumentOutOfRange_UnmanagedMemStreamWrapAround = @"The UnmanagedMemoryStream capacity would wrap around the high end of the address space.";
        internal const string Argument_InvalidSafeBufferOffLen = @"Offset and length were greater than the size of the SafeBuffer.";
        internal const string Argument_NeedStructWithNoRefs = @"The specified Type must be a struct containing no references.";
        internal const string IO_FixedCapacity = @"Unable to expand length of this stream beyond its capacity.";
        internal const string IndexOutOfRange_UMSPosition = @"Unmanaged memory stream position was beyond the capacity of the stream.";
        internal const string InvalidOperation_CalledTwice = @"The method cannot be called twice on the same instance.";
        internal const string InvalidOperation_MustCallInitialize = @"You must call Initialize on this object instance before using it.";
        internal const string NotSupported_UmsSafeBuffer = @"This operation is not supported for an UnmanagedMemoryStream created from a SafeBuffer.";
    }
}
