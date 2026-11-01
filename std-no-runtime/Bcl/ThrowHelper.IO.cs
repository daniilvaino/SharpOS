// ThrowHelper members of CoreLib's MemoryStream (step223), as BCL words them.

using System.IO;

namespace System
{
    internal static partial class ThrowHelper
    {
        internal static void ThrowObjectDisposedException_StreamClosed(string? objectName) =>
            throw new ObjectDisposedException(objectName, SR.ObjectDisposed_StreamClosed);

        internal static void ThrowNotSupportedException_UnwritableStream() =>
            throw new NotSupportedException(SR.NotSupported_UnwritableStream);

        internal static void ThrowEndOfFileException() =>
            throw new EndOfStreamException(SR.IO_EOF_ReadBeyondEOF);
    }
}
