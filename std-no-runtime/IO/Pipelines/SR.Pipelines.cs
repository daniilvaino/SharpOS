// SR keys of System.IO.Pipelines (dotnet-runtime-sharpos, .NET 10, Strings.resx), generated.
// Only the keys no other SR partial in std already has.

namespace System
{
    internal static partial class SR
    {
        internal const string AdvanceToInvalidCursor = @"The PipeReader has already advanced past the provided position.";
        internal const string ArgumentOutOfRange_NeedPosNum = @"Positive number required.";
        internal const string ConcurrentOperationsNotSupported = @"Concurrent reads or writes are not supported.";
        internal const string FlushCanceledOnPipeWriter = @"Flush was canceled on underlying PipeWriter.";
        internal const string GetResultBeforeCompleted = @"Can't GetResult unless awaiter is completed.";
        internal const string InvalidExaminedOrConsumedPosition = @"The examined position must be greater than or equal to the consumed position.";
        internal const string InvalidExaminedPosition = @"The examined position cannot be less than the previously examined position.";
        internal const string InvalidZeroByteRead = @"The PipeReader returned 0 bytes when the ReadResult was not completed or canceled.";
        internal const string NoReadingOperationToComplete = @"No reading operation to complete.";
        internal const string ReadCanceledOnPipeReader = @"Read was canceled on underlying PipeReader.";
        internal const string ReaderAndWriterHasToBeCompleted = @"Both reader and writer has to be completed to be able to reset the pipe.";
        internal const string ReadingAfterCompleted = @"Reading is not allowed after reader was completed.";
        internal const string ReadingIsInProgress = @"Reading is already in progress.";
        internal const string WritingAfterCompleted = @"Writing is not allowed after writer was completed.";
        internal const string UnflushedBytesNotSupported = @"UnflushedBytes is not supported.";
    }
}
