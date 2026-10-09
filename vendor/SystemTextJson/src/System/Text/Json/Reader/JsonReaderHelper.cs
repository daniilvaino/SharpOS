// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Text.Json
{
    internal static partial class JsonReaderHelper
    {
        private const string SpecialCharacters = ". '/\"[]()\t\n\r\f\b\\\u0085\u2028\u2029";

        // SharpOS: the netstandard body — no SearchValues<char> in std.
        public static bool ContainsSpecialCharacters(this ReadOnlySpan<char> text) =>
            text.IndexOfAny(SpecialCharacters.AsSpan()) >= 0;

        public static (int, int) CountNewLines(ReadOnlySpan<byte> data)
        {
            int lastLineFeedIndex = data.LastIndexOf(JsonConstants.LineFeed);
            int newLines = 0;

            if (lastLineFeedIndex >= 0)
            {
                newLines = 1;
                data = data.Slice(0, lastLineFeedIndex);
#if NET
                newLines += data.Count(JsonConstants.LineFeed);
#else
                int pos;
                while ((pos = data.IndexOf(JsonConstants.LineFeed)) >= 0)
                {
                    newLines++;
                    data = data.Slice(pos + 1);
                }
#endif
            }

            return (newLines, lastLineFeedIndex);
        }

        internal static JsonValueKind ToValueKind(this JsonTokenType tokenType)
        {
            switch (tokenType)
            {
                case JsonTokenType.None:
                    return JsonValueKind.Undefined;
                case JsonTokenType.StartArray:
                    return JsonValueKind.Array;
                case JsonTokenType.StartObject:
                    return JsonValueKind.Object;
                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    // This is the offset between the set of literals within JsonValueType and JsonTokenType
                    // Essentially: JsonTokenType.Null - JsonValueType.Null
                    return (JsonValueKind)((byte)tokenType - 4);
                default:
                    Debug.Fail($"No mapping for token type {tokenType}");
                    return JsonValueKind.Undefined;
            }
        }

        // Returns true if the TokenType is a primitive "value", i.e. String, Number, True, False, and Null
        // Otherwise, return false.
        public static bool IsTokenTypePrimitive(JsonTokenType tokenType) =>
            (tokenType - JsonTokenType.String) <= (JsonTokenType.Null - JsonTokenType.String);

        // A hex digit is valid if it is in the range: [0..9] | [A..F] | [a..f]
        // Otherwise, return false.
        public static bool IsHexDigit(byte nextByte) => HexConverter.IsHexChar(nextByte);

        // SharpOS cut: TryGetValue(..., out DateTime) - JsonHelpers.Date.cs (ISO 8601 parser) not ported.

        // SharpOS cut: TryGetEscapedDateTime.

        // SharpOS cut: TryGetValue(..., out DateTimeOffset) - no DateTimeOffset in std.

        // SharpOS cut: TryGetEscapedDateTimeOffset.

        // SharpOS cut: TryGetValue(..., out Guid) - Utf8Parser.TryParse(Guid) not ported.

        // SharpOS cut: TryGetEscapedGuid.

#if NET
        // SharpOS cut: TryGetFloatingPointConstant(..., out Half) - no System.Half in std.
#endif

        public static bool TryGetFloatingPointConstant(ReadOnlySpan<byte> span, out float value)
        {
            if (span.Length == 3)
            {
                if (span.SequenceEqual(JsonConstants.NaNValue))
                {
                    value = float.NaN;
                    return true;
                }
            }
            else if (span.Length == 8)
            {
                if (span.SequenceEqual(JsonConstants.PositiveInfinityValue))
                {
                    value = float.PositiveInfinity;
                    return true;
                }
            }
            else if (span.Length == 9)
            {
                if (span.SequenceEqual(JsonConstants.NegativeInfinityValue))
                {
                    value = float.NegativeInfinity;
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public static bool TryGetFloatingPointConstant(ReadOnlySpan<byte> span, out double value)
        {
            if (span.Length == 3)
            {
                if (span.SequenceEqual(JsonConstants.NaNValue))
                {
                    value = double.NaN;
                    return true;
                }
            }
            else if (span.Length == 8)
            {
                if (span.SequenceEqual(JsonConstants.PositiveInfinityValue))
                {
                    value = double.PositiveInfinity;
                    return true;
                }
            }
            else if (span.Length == 9)
            {
                if (span.SequenceEqual(JsonConstants.NegativeInfinityValue))
                {
                    value = double.NegativeInfinity;
                    return true;
                }
            }

            value = 0;
            return false;
        }
    }
}
