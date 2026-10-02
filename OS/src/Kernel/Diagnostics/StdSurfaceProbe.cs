using System;
using System.IO;
using System.Text;
using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // The kernel's own checks of std surface added for the pipes
    // (pipe_plan.md): array casts, BinaryWriter / BinaryReader, Stream.Null.
    // AotTests runs the same in an app; the kernel compiles std on its own and
    // gets its own ILC decisions, so it is checked on its own.
    internal static unsafe class StdSurfaceProbe
    {
        private class CastBase { }
        private sealed class CastDerived : CastBase { }
        private enum CastTint { A, B }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static object Opaque(object o) => o;

        public static void Run()
        {
            ArrayCasts();
            BinaryRoundTrip();
            CharReads();
            ForgedLength();
            NullStreams();
            Interpolation();
            NumberFormats();
        }

        private static void ArrayCasts()
        {
            object strings = Opaque(new string[1]);
            object ints = Opaque(new int[1]);
            // A real CastDerived in the array: with none ever created, ILC
            // compiles `is CastBase[]` into an exact MethodTable compare.
            object derived = Opaque(new CastDerived[] { new CastDerived() });
            object tints = Opaque(new CastTint[1]);

            bool ok = strings is object[]
                      && strings is Array
                      && !(ints is ValueType)
                      && ints is uint[]
                      && !(ints is long[])
                      && tints is int[]
                      && derived is CastBase[]
                      && !(ints is object[]);
            Report("std: array casts (covariance, same-size integers, Array)", ok);

            object[] covariant = (object[])Opaque(new string[1]);
            bool refMismatch = false;
            try
            {
                ref object slot = ref covariant[0];
                slot = null;
            }
            catch (ArrayTypeMismatchException) { refMismatch = true; }
            Report("std: ref into a covariant array throws", refMismatch);
        }

        private static void BinaryRoundTrip()
        {
            var stream = new MemoryStream();
            var writer = new BinaryWriter(stream);
            string longText = new string('ж', 300);
            writer.Write(-123456789);
            writer.Write(0x1122334455667788L);
            writer.Write(2.5);
            writer.Write(1.25f);
            writer.Write(true);
            writer.Write("строка");
            writer.Write(longText);
            writer.Write7BitEncodedInt(300);
            writer.Write7BitEncodedInt64(-1L);
            stream.Position = 0;

            var reader = new BinaryReader(stream);
            bool ok = reader.ReadInt32() == -123456789
                      && reader.ReadInt64() == 0x1122334455667788L
                      && reader.ReadDouble() == 2.5
                      && reader.ReadSingle() == 1.25f
                      && reader.ReadBoolean()
                      && reader.ReadString() == "строка"
                      && reader.ReadString() == longText
                      && reader.Read7BitEncodedInt() == 300
                      && reader.Read7BitEncodedInt64() == -1L;
            Report("std: BinaryWriter -> BinaryReader round trip", ok);
        }

        private static void CharReads()
        {
            // "aж€" then U+1F600 as a surrogate pair, in UTF-8: 1, 2, 3, 4 bytes.
            string text = "aж€😀";
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
            var reader = new BinaryReader(stream);
            bool ok = reader.PeekChar() == 'a'
                      && reader.ReadChar() == 'a'
                      && reader.Read() == 'ж'
                      && reader.ReadChar() == '€';
            char[] pair = reader.ReadChars(2);
            ok = ok && pair.Length == 2 && pair[0] == '\uD83D' && pair[1] == '\uDE00' && reader.Read() == -1;
            Report("std: BinaryReader chars (UTF-8, 1-4 bytes, surrogate pair)", ok);

            var utf16 = new MemoryStream(Encoding.Unicode.GetBytes("Ыz"));
            var reader16 = new BinaryReader(utf16, Encoding.Unicode);
            Report("std: BinaryReader chars (UTF-16)", reader16.ReadChar() == 'Ы' && reader16.ReadChar() == 'z');
        }

        private static void ForgedLength()
        {
            // A length prefix of two gigabytes over one byte of data.
            var forged = new MemoryStream(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x07, (byte)'x' });
            bool refused = false;
            try { new BinaryReader(forged).ReadString(); }
            catch (EndOfStreamException) { refused = true; }
            Report("std: BinaryReader refuses a forged string length", refused);
        }

        private static void NullStreams()
        {
            BinaryWriter.Null.Write("nowhere");
            Stream s = Stream.Null;
            s.Write(new byte[] { 1, 2, 3 }, 0, 3);
            Report("std: Stream.Null and BinaryWriter.Null", s.ReadByte() == -1 && s.Length == 0);
        }

        // BCL number formatting (std-no-runtime/Number/, ported from
        // dotnet/runtime). Expected strings are what .NET prints for the
        // invariant culture; values are chosen away from rounding ties so the
        // expectation does not depend on tie-breaking.
        private static void NumberFormats()
        {
            FormatCheck("255 X", 255.ToString("X"), "FF");
            FormatCheck("255 x4", 255.ToString("x4"), "00ff");
            FormatCheck("42 D5", 42.ToString("D5"), "00042");
            FormatCheck("-5 D3", (-5).ToString("D3"), "-005");
            FormatCheck("-42", (-42).ToString(), "-42");
            FormatCheck("1234567 N0", 1234567.ToString("N0"), "1,234,567");
            FormatCheck("long.MinValue", long.MinValue.ToString(), "-9223372036854775808");
            FormatCheck("ulong.MaxValue X", ulong.MaxValue.ToString("X"), "FFFFFFFFFFFFFFFF");
            FormatCheck("(sbyte)-1 X", ((sbyte)-1).ToString("X"), "FF");
            FormatCheck("(short)-1 x", ((short)-1).ToString("x"), "ffff");
            FormatCheck("(byte)200 x", ((byte)200).ToString("x"), "c8");
            FormatCheck("int custom #,##0.00", 1234567.ToString("#,##0.00"), "1,234,567.00");
            FormatCheck("int invariant provider", (-7).ToString(System.Globalization.CultureInfo.InvariantCulture), "-7");

            FormatCheck("3.14159 F2", 3.14159.ToString("F2"), "3.14");
            FormatCheck("2.5", 2.5.ToString(), "2.5");
            FormatCheck("0.1", 0.1.ToString(), "0.1");
            FormatCheck("0.1+0.2", (0.1 + 0.2).ToString(), "0.30000000000000004");
            FormatCheck("1e20", 1e20.ToString(), "1E+20");
            FormatCheck("1e-5", 1e-5.ToString(), "1E-05");
            FormatCheck("123.456 E3", 123.456.ToString("E3"), "1.235E+002");
            FormatCheck("1/3", (1.0 / 3).ToString(), "0.3333333333333333");
            FormatCheck("123456789 G5", 123456789.0.ToString("G5"), "1.2346E+08");
            FormatCheck("-0.0", (-0.0).ToString(), "-0");
            FormatCheck("1234.5 N2", 1234.5.ToString("N2"), "1,234.50");
            FormatCheck("1234.5 C", 1234.5.ToString("C"), "¤1,234.50");
            FormatCheck("0.5 P0", 0.5.ToString("P0"), "50 %");
            FormatCheck("double custom 0.00", 2.71828.ToString("0.00"), "2.72");
            FormatCheck("0.1f", 0.1f.ToString(), "0.1");
            FormatCheck("float.MaxValue", float.MaxValue.ToString(), "3.4028235E+38");
            FormatCheck("NaN", double.NaN.ToString(), "NaN");
            FormatCheck("+Infinity", double.PositiveInfinity.ToString(), "Infinity");
            FormatCheck("-Infinity", double.NegativeInfinity.ToString(), "-Infinity");

            FormatCheck("string.Format X8/align F1/N2",
                string.Format("{0:X8}|{1,6:F1}|{2:N2}", 48879, 2.26, 1234.5), "0000BEEF|   2.3|1,234.50");

            Span<char> small = stackalloc char[3];
            Span<char> room = stackalloc char[16];
            bool tooSmall = !12345.TryFormat(small, out int none) && none == 0;
            bool fits = 12345.TryFormat(room, out int written, "N0", null) && written == 6
                        && room.Slice(0, written).ToString() == "12,345";
            Report("std: format TryFormat (short destination refused, N0 written)", tooSmall && fits);

            bool badFormat = false;
            try { 1.ToString("Q"); }
            catch (FormatException) { badFormat = true; }
            Report("std: format bad specifier throws FormatException", badFormat);

            // .NET 8 additions: the binary specifier and UTF-8 TryFormat
            // (IUtf8SpanFormattable, the TChar=byte instantiation of the engine).
            FormatCheck("255 B", 255.ToString("B"), "11111111");
            FormatCheck("5 B8", 5.ToString("B8"), "00000101");

            Span<byte> u8 = stackalloc byte[32];
            Report("std: format UTF-8 int N0",
                1234567.TryFormat(u8, out int n1, "N0", null) && Utf8Is(u8.Slice(0, n1), "1,234,567"u8));
            Report("std: format UTF-8 double N1 negative",
                (-1234.5).TryFormat(u8, out int n2, "N1", null) && Utf8Is(u8.Slice(0, n2), "-1,234.5"u8));
            Report("std: format UTF-8 double shortest / NaN",
                0.1.TryFormat(u8, out int n3) && Utf8Is(u8.Slice(0, n3), "0.1"u8)
                && double.NaN.TryFormat(u8, out int n4) && Utf8Is(u8.Slice(0, n4), "NaN"u8));
            // A non-ASCII literal in a custom format takes the encode-one-char path.
            Report("std: format UTF-8 custom literal (euro sign, 3 bytes)",
                7.TryFormat(u8, out int n5, "0 €", null) && Utf8Is(u8.Slice(0, n5), "7 €"u8));
            Span<byte> u8Small = stackalloc byte[2];
            Report("std: format UTF-8 short destination refused",
                !12345.TryFormat(u8Small, out int n6) && n6 == 0);
        }

        private static bool Utf8Is(ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected)
        {
            if (actual.Length != expected.Length) return false;
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }

        private static void FormatCheck(string name, string actual, string expected)
        {
            bool ok = actual == expected;
            Log.Begin(LogLevel.Info);
            OS.Hal.Console.Write("std: format ");
            OS.Hal.Console.Write(name);
            OS.Hal.Console.Write(": ");
            OS.Hal.Console.Write(ok ? "ok" : "FAIL");
            if (!ok)
            {
                OS.Hal.Console.Write(" got '");
                OS.Hal.Console.Write(actual ?? "(null)");
                OS.Hal.Console.Write("' want '");
                OS.Hal.Console.Write(expected);
                OS.Hal.Console.Write("'");
            }
            Log.EndLine();
        }

        // DefaultInterpolatedStringHandler (pipe_plan.md "Подготовить под
        // трубы", item 7). A span hole compiles only through the handler, so
        // this method building at all says the compiler took it.
        private static void Interpolation()
        {
            int n = 255;
            int m = -5;
            string text = "ab";
            double d = 2.5;
            ReadOnlySpan<char> span = "xyz".AsSpan(1);
            string got = $"{n:X}|{m,4}|{text,-3}|{d:F1}|[{span}]|{span,4}";
            Report("std: interpolation (format, alignment, span holes)", got == "FF|  -5|ab |2.5|[yz]|  yz");

            string viaProvider = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{1234.5:N1}");
            Span<char> initial = stackalloc char[8];
            string viaBuffer = string.Create(null, initial, $"{n}-{text}-{new string('q', 20)}");
            Report("std: string.Create with provider and initial buffer",
                   viaProvider == "1,234.5" && viaBuffer == "255-ab-" + new string('q', 20));
        }

        private static void Report(string name, bool ok)
        {
            Log.Begin(LogLevel.Info);
            OS.Hal.Console.Write(name);
            OS.Hal.Console.Write(": ");
            OS.Hal.Console.Write(ok ? "ok" : "FAIL");
            Log.EndLine();
        }
    }
}
