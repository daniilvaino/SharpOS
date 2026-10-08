// Ported from dotnet/runtime release/8.0 (MIT), the CoreLib snapshot in gc-experiment/dotnet-runtime-8.0:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Text/Utf8Formatter/Utf8Formatter.Boolean.cs
// Verbatim.

namespace System.Buffers.Text
{
    public static partial class Utf8Formatter
    {
        /// <summary>
        /// Formats a Boolean as a UTF-8 string.
        /// </summary>
        /// <param name="value">Value to format</param>
        /// <param name="destination">Buffer to write the UTF-8 formatted value to</param>
        /// <param name="bytesWritten">Receives the length of the formatted text in bytes</param>
        /// <param name="format">The standard format to use</param>
        /// <returns>
        /// true for success. "bytesWritten" contains the length of the formatted text in bytes.
        /// false if buffer was too short. Iteratively increase the size of the buffer and retry until it succeeds.
        /// </returns>
        /// <remarks>
        /// Formats supported:
        ///     G (default)   True/False
        ///     l             true/false
        /// </remarks>
        /// <exceptions>
        /// <cref>System.FormatException</cref> if the format is not valid for this data type.
        /// </exceptions>
        public static bool TryFormat(bool value, Span<byte> destination, out int bytesWritten, StandardFormat format = default)
        {
            char symbol = FormattingHelpers.GetSymbolOrDefault(format, 'G');

            if (value)
            {
                if (symbol == 'G')
                {
                    if (!"True"u8.TryCopyTo(destination))
                    {
                        goto BufferTooSmall;
                    }
                }
                else if (symbol == 'l')
                {
                    if (!"true"u8.TryCopyTo(destination))
                    {
                        goto BufferTooSmall;
                    }
                }
                else
                {
                    goto BadFormat;
                }

                bytesWritten = 4;
                return true;
            }
            else
            {
                if (symbol == 'G')
                {
                    if (!"False"u8.TryCopyTo(destination))
                    {
                        goto BufferTooSmall;
                    }
                }
                else if (symbol == 'l')
                {
                    if (!"false"u8.TryCopyTo(destination))
                    {
                        goto BufferTooSmall;
                    }
                }
                else
                {
                    goto BadFormat;
                }

                bytesWritten = 5;
                return true;
            }

        BadFormat:
            ThrowHelper.ThrowFormatException_BadFormatSpecifier();

        BufferTooSmall:
            bytesWritten = 0;
            return false;
        }
    }
}
