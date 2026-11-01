// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Buffers.Text
{
    public static partial class Utf8Formatter
    {
        /// <summary>
        /// Formats a Guid as a UTF-8 string.
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
        ///     D (default)     nnnnnnnn-nnnn-nnnn-nnnn-nnnnnnnnnnnn
        ///     B               {nnnnnnnn-nnnn-nnnn-nnnn-nnnnnnnnnnnn}
        ///     P               (nnnnnnnn-nnnn-nnnn-nnnn-nnnnnnnnnnnn)
        ///     N               nnnnnnnnnnnnnnnnnnnnnnnnnnnnnnnn
        /// </remarks>
        /// <exceptions>
        /// <cref>System.FormatException</cref> if the format is not valid for this data type.
        /// </exceptions>
        public static bool TryFormat(Guid value, Span<byte> destination, out int bytesWritten, StandardFormat format = default)
        {
            // SharpOS: std's Guid has no TryFormatCore; its ToString gives the
            // same D/B/P/N text, which is ASCII.
            char symbol = FormattingHelpers.GetSymbolOrDefault(format, 'D');
            if (symbol != 'D' && symbol != 'B' && symbol != 'P' && symbol != 'N')
                ThrowHelper.ThrowFormatException_BadFormatSpecifier();
            string text = value.ToString(symbol.ToString());
            if (destination.Length < text.Length)
            {
                bytesWritten = 0;
                return false;
            }
            for (int i = 0; i < text.Length; i++)
                destination[i] = (byte)text[i];
            bytesWritten = text.Length;
            return true;
        }
    }
}
