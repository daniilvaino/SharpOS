// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Convert.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). The conversions that
// understand base 2/8/10/16 — ToByte..ToUInt64(string, fromBase) and
// ToString(byte/short/int/long, toBase) — over ParseNumbers (Number/ParseNumbers.cs).
// Verbatim except the Throw*OverflowException helpers std's Convert lacks, spelled
// as throws (4x, marked "SharpOS").

namespace System
{
    public static partial class Convert
    {
        //
        // Conversions which understand Base XXX numbers.
        //
        // Parses value in base base.  base can only
        // be 2, 8, 10, or 16.  If base is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        public static byte ToByte(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }

            if (value == null)
            {
                return 0;
            }

            int r = ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.IsTight | ParseNumbers.TreatAsUnsigned);
            if ((uint)r > byte.MaxValue)
                throw new OverflowException(SR.Overflow_Byte); // SharpOS: was ThrowByteOverflowException();
            return (byte)r;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        [CLSCompliant(false)]
        public static sbyte ToSByte(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }

            if (value == null)
            {
                return 0;
            }

            int r = ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.IsTight | ParseNumbers.TreatAsI1);
            if (fromBase != 10 && r <= byte.MaxValue)
                return (sbyte)r;

            if (r < sbyte.MinValue || r > sbyte.MaxValue)
                throw new OverflowException(SR.Overflow_SByte); // SharpOS: was ThrowSByteOverflowException();
            return (sbyte)r;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        public static short ToInt16(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }

            if (value == null)
            {
                return 0;
            }

            int r = ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.IsTight | ParseNumbers.TreatAsI2);
            if (fromBase != 10 && r <= ushort.MaxValue)
                return (short)r;

            if (r < short.MinValue || r > short.MaxValue)
                throw new OverflowException(SR.Overflow_Int16); // SharpOS: was ThrowInt16OverflowException();
            return (short)r;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        [CLSCompliant(false)]
        public static ushort ToUInt16(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }

            if (value == null)
            {
                return 0;
            }

            int r = ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.IsTight | ParseNumbers.TreatAsUnsigned);
            if ((uint)r > ushort.MaxValue)
                throw new OverflowException(SR.Overflow_UInt16); // SharpOS: was ThrowUInt16OverflowException();
            return (ushort)r;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        public static int ToInt32(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return value != null ?
                ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.IsTight) :
                0;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        [CLSCompliant(false)]
        public static uint ToUInt32(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return value != null ?
                (uint)ParseNumbers.StringToInt(value.AsSpan(), fromBase, ParseNumbers.TreatAsUnsigned | ParseNumbers.IsTight) :
                0;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        public static long ToInt64(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return value != null ?
                ParseNumbers.StringToLong(value.AsSpan(), fromBase, ParseNumbers.IsTight) :
                0;
        }

        // Parses value in base fromBase.  fromBase can only
        // be 2, 8, 10, or 16.  If fromBase is 16, the number may be preceded
        // by 0x; any other leading or trailing characters cause an error.
        //
        [CLSCompliant(false)]
        public static ulong ToUInt64(string? value, int fromBase)
        {
            if (fromBase != 2 && fromBase != 8 && fromBase != 10 && fromBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return value != null ?
                (ulong)ParseNumbers.StringToLong(value.AsSpan(), fromBase, ParseNumbers.TreatAsUnsigned | ParseNumbers.IsTight) :
                0;
        }

        // Convert the byte value to a string in base fromBase
        public static string ToString(byte value, int toBase)
        {
            if (toBase != 2 && toBase != 8 && toBase != 10 && toBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return ParseNumbers.IntToString((int)value, toBase, -1, ' ', ParseNumbers.PrintAsI1);
        }

        // Convert the Int16 value to a string in base fromBase
        public static string ToString(short value, int toBase)
        {
            if (toBase != 2 && toBase != 8 && toBase != 10 && toBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return ParseNumbers.IntToString((int)value, toBase, -1, ' ', ParseNumbers.PrintAsI2);
        }

        // Convert the Int32 value to a string in base toBase
        public static string ToString(int value, int toBase)
        {
            if (toBase != 2 && toBase != 8 && toBase != 10 && toBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return ParseNumbers.IntToString(value, toBase, -1, ' ', 0);
        }

        // Convert the Int64 value to a string in base toBase
        public static string ToString(long value, int toBase)
        {
            if (toBase != 2 && toBase != 8 && toBase != 10 && toBase != 16)
            {
                throw new ArgumentException(SR.Arg_InvalidBase);
            }
            return ParseNumbers.LongToString(value, toBase, -1, ' ', 0);
        }
    }
}
