// Ported from dotnet/runtime release/8.0 (MIT), the CoreLib snapshot in gc-experiment/dotnet-runtime-8.0:
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Text/Utf8Formatter/Utf8Formatter.Float.cs
// Verbatim.

namespace System.Buffers.Text
{
    public static partial class Utf8Formatter
    {
        /// <summary>
        /// Formats a Double as a UTF-8 string.
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
        ///     G/g  (default)
        ///     F/f             12.45       Fixed point
        ///     E/e             1.245000e1  Exponential
        /// </remarks>
        /// <exceptions>
        /// <cref>System.FormatException</cref> if the format is not valid for this data type.
        /// </exceptions>
        public static bool TryFormat(double value, Span<byte> destination, out int bytesWritten, StandardFormat format = default) =>
            FormattingHelpers.TryFormat(value, destination, out bytesWritten, format);

        /// <summary>
        /// Formats a Single as a UTF-8 string.
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
        ///     G/g  (default)
        ///     F/f             12.45       Fixed point
        ///     E/e             1.245000e1  Exponential
        /// </remarks>
        /// <exceptions>
        /// <cref>System.FormatException</cref> if the format is not valid for this data type.
        /// </exceptions>
        public static bool TryFormat(float value, Span<byte> destination, out int bytesWritten, StandardFormat format = default) =>
            FormattingHelpers.TryFormat(value, destination, out bytesWritten, format);
    }
}
