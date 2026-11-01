// System.Text.Unicode.Utf8 — BCL's static surface (System.Private.CoreLib,
// Text/Unicode/Utf8.cs: FromUtf16, ToUtf16, IsValid) over Rune's scalar
// decoders, without BCL's vectorized Utf8Utility transcoder. Same results,
// same OperationStatus contract: Done, DestinationTooSmall, NeedMoreData
// (input ends inside a sequence and isFinalBlock is false), InvalidData
// (when replaceInvalidSequences is false). First caller: System.Text.Json.

using System.Buffers;

namespace System.Text.Unicode
{
    public static class Utf8
    {
        public static OperationStatus FromUtf16(ReadOnlySpan<char> source, Span<byte> destination, out int charsRead, out int bytesWritten, bool replaceInvalidSequences = true, bool isFinalBlock = true)
        {
            int read = 0, written = 0;
            OperationStatus status = OperationStatus.Done;
            while (read < source.Length)
            {
                OperationStatus decode = Rune.DecodeFromUtf16(source.Slice(read), out Rune rune, out int consumed);
                if (decode == OperationStatus.NeedMoreData && !isFinalBlock)
                {
                    status = OperationStatus.NeedMoreData;
                    break;
                }
                if (decode != OperationStatus.Done)
                {
                    if (!replaceInvalidSequences)
                    {
                        status = OperationStatus.InvalidData;
                        break;
                    }
                    rune = Rune.ReplacementChar;
                }
                if (!rune.TryEncodeToUtf8(destination.Slice(written), out int n))
                {
                    status = OperationStatus.DestinationTooSmall;
                    break;
                }
                written += n;
                read += consumed;
            }
            charsRead = read;
            bytesWritten = written;
            return status;
        }

        public static OperationStatus ToUtf16(ReadOnlySpan<byte> source, Span<char> destination, out int bytesRead, out int charsWritten, bool replaceInvalidSequences = true, bool isFinalBlock = true)
        {
            int read = 0, written = 0;
            OperationStatus status = OperationStatus.Done;
            while (read < source.Length)
            {
                OperationStatus decode = Rune.DecodeFromUtf8(source.Slice(read), out Rune rune, out int consumed);
                if (decode == OperationStatus.NeedMoreData && !isFinalBlock)
                {
                    status = OperationStatus.NeedMoreData;
                    break;
                }
                if (decode != OperationStatus.Done)
                {
                    if (!replaceInvalidSequences)
                    {
                        status = OperationStatus.InvalidData;
                        break;
                    }
                    rune = Rune.ReplacementChar;
                }
                if (!rune.TryEncodeToUtf16(destination.Slice(written), out int n))
                {
                    status = OperationStatus.DestinationTooSmall;
                    break;
                }
                written += n;
                read += consumed;
            }
            bytesRead = read;
            charsWritten = written;
            return status;
        }

        public static bool IsValid(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                if (Rune.DecodeFromUtf8(value, out _, out int consumed) != OperationStatus.Done)
                    return false;
                value = value.Slice(consumed);
            }
            return true;
        }
    }
}
