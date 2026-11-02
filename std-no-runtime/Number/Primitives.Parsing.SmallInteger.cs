// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/SByte.cs, Int16.cs, Byte.cs, UInt16.cs, Char.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The Parse / TryParse statics
// of sbyte, short, byte, ushort and char, over Number.Parsing.Integer.cs, as
// Primitives.Parsing.Integer.cs has them for int, long, uint, ulong. Step230:
// System.Xml's XmlConvert reads its small integers with them.
//
// SharpOS changes (marked inline):
//   * ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s) is spelled as a plain throw.
//   * Number.ThrowOverflowException(TypeCode.X) is a plain throw with the BCL's text:
//     std's Number has no TypeCode overload.
// SharpOS cuts: the IParsable / ISpanParsable / INumberBase interface declarations (no
// generic math interfaces in this std).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System
{
    public partial struct SByte
    {
        public static sbyte Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static sbyte Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.CurrentInfo);
        }

        public static sbyte Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static sbyte Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static sbyte Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Parse(s, style, NumberFormatInfo.GetInstance(provider));
        }

        private static sbyte Parse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info)
        {
            Number.ParsingStatus status = Number.TryParseInt32(s, style, info, out int i);
            if (status != Number.ParsingStatus.OK)
            {
                Number.ThrowOverflowOrFormatException(status, s);
            }

            if ((uint)(i - MinValue - ((int)(style & NumberStyles.AllowHexSpecifier) >> 2)) > byte.MaxValue)
            {
                throw new OverflowException("Value was either too large or too small for a signed byte."); // SharpOS: was Number.ThrowOverflowException(TypeCode.SByte)
            }
            return (sbyte)i;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out sbyte result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out sbyte result)
        {
            return TryParse(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out sbyte result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out sbyte result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out sbyte result)
        {
            if (Number.TryParseInt32(s, style, info, out int i) != Number.ParsingStatus.OK
                || (uint)(i - MinValue - ((int)(style & NumberStyles.AllowHexSpecifier) >> 2)) > byte.MaxValue)
            {
                result = 0;
                return false;
            }
            result = (sbyte)i;
            return true;
        }
    }

    public partial struct Int16
    {
        public static short Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static short Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.CurrentInfo);
        }

        public static short Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static short Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static short Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Parse(s, style, NumberFormatInfo.GetInstance(provider));
        }

        private static short Parse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info)
        {
            Number.ParsingStatus status = Number.TryParseInt32(s, style, info, out int i);
            if (status != Number.ParsingStatus.OK)
            {
                Number.ThrowOverflowOrFormatException(status, s);
            }

            if ((uint)(i - MinValue - ((int)(style & NumberStyles.AllowHexSpecifier) << 6)) > ushort.MaxValue)
            {
                throw new OverflowException("Value was either too large or too small for an Int16."); // SharpOS: was Number.ThrowOverflowException(TypeCode.Int16)
            }
            return (short)i;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out short result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out short result)
        {
            return TryParse(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out short result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out short result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out short result)
        {
            if (Number.TryParseInt32(s, style, info, out int i) != Number.ParsingStatus.OK
                || (uint)(i - MinValue - ((int)(style & NumberStyles.AllowHexSpecifier) << 6)) > ushort.MaxValue)
            {
                result = 0;
                return false;
            }
            result = (short)i;
            return true;
        }
    }

    public partial struct Byte
    {
        public static byte Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static byte Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.CurrentInfo);
        }

        public static byte Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static byte Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static byte Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Parse(s, style, NumberFormatInfo.GetInstance(provider));
        }

        private static byte Parse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info)
        {
            Number.ParsingStatus status = Number.TryParseUInt32(s, style, info, out uint i);
            if (status != Number.ParsingStatus.OK)
            {
                Number.ThrowOverflowOrFormatException(status, s);
            }

            if (i > MaxValue)
            {
                throw new OverflowException("Value was either too large or too small for an unsigned byte."); // SharpOS: was Number.ThrowOverflowException(TypeCode.Byte)
            }
            return (byte)i;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out byte result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out byte result)
        {
            return TryParse(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out byte result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out byte result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out byte result)
        {
            if (Number.TryParseUInt32(s, style, info, out uint i) != Number.ParsingStatus.OK
                || i > MaxValue)
            {
                result = 0;
                return false;
            }
            result = (byte)i;
            return true;
        }
    }

    public partial struct UInt16
    {
        public static ushort Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo);
        }

        public static ushort Parse(string s, NumberStyles style)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.CurrentInfo);
        }

        public static ushort Parse(string s, IFormatProvider? provider)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.GetInstance(provider));
        }

        public static ushort Parse(string s, NumberStyles style, IFormatProvider? provider)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)
            return Parse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider));
        }

        public static ushort Parse(ReadOnlySpan<char> s, NumberStyles style = NumberStyles.Integer, IFormatProvider? provider = null)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return Parse(s, style, NumberFormatInfo.GetInstance(provider));
        }

        private static ushort Parse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info)
        {
            Number.ParsingStatus status = Number.TryParseUInt32(s, style, info, out uint i);
            if (status != Number.ParsingStatus.OK)
            {
                Number.ThrowOverflowOrFormatException(status, s);
            }

            if (i > MaxValue)
            {
                throw new OverflowException("Value was either too large or too small for a UInt16."); // SharpOS: was Number.ThrowOverflowException(TypeCode.UInt16)
            }
            return (ushort)i;
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out ushort result)
        {
            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, out ushort result)
        {
            return TryParse(s, NumberStyles.Integer, NumberFormatInfo.CurrentInfo, out result);
        }

        public static bool TryParse([NotNullWhen(true)] string? s, NumberStyles style, IFormatProvider? provider, out ushort result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);

            if (s == null)
            {
                result = 0;
                return false;
            }

            return TryParse((ReadOnlySpan<char>)s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        public static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, IFormatProvider? provider, out ushort result)
        {
            NumberFormatInfo.ValidateParseStyleInteger(style);
            return TryParse(s, style, NumberFormatInfo.GetInstance(provider), out result);
        }

        private static bool TryParse(ReadOnlySpan<char> s, NumberStyles style, NumberFormatInfo info, out ushort result)
        {
            if (Number.TryParseUInt32(s, style, info, out uint i) != Number.ParsingStatus.OK
                || i > MaxValue)
            {
                result = 0;
                return false;
            }
            result = (ushort)i;
            return true;
        }
    }

    public partial struct Char
    {
        public static char Parse(string s)
        {
            if (s == null) throw new ArgumentNullException(nameof(s)); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.s)

            if (s.Length != 1)
            {
                throw new FormatException("String must be exactly one character long.");
            }
            return s[0];
        }

        public static bool TryParse([NotNullWhen(true)] string? s, out char result)
        {
            result = '\0';
            if (s == null)
            {
                return false;
            }
            if (s.Length != 1)
            {
                return false;
            }
            result = s[0];
            return true;
        }
    }
}
