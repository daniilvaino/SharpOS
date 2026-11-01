// SR keys of System.Private.CoreLib (dotnet/runtime v8.0.27 Strings.resx) for
// MemoryStream, ConcurrentQueue and ThrowHelper.IO (step223).

namespace System
{
    internal static partial class SR
    {
        internal const string IO_StreamTooLong = @"Stream was too long.";
        internal const string UnauthorizedAccess_MemStreamBuffer = @"MemoryStream's internal buffer cannot be accessed.";
        internal const string ArgumentOutOfRange_SmallCapacity = @"capacity was less than the current size.";
        internal const string NotSupported_MemStreamNotExpandable = @"Memory stream is not expandable.";
        internal const string ArgumentOutOfRange_StreamLength = @"Stream length must be non-negative and less than 2^31 - 1 - origin.";
        internal const string IO_SeekBeforeBegin = @"An attempt was made to move the position before the beginning of the stream.";
        internal const string Argument_InvalidSeekOrigin = @"Invalid seek origin.";
        internal const string ObjectDisposed_StreamClosed = @"Cannot access a closed Stream.";
        internal const string IO_EOF_ReadBeyondEOF = @"Unable to read beyond the end of the stream.";
    }
}
