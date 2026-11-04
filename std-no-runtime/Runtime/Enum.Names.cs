// System.Enum by member name: ToString (G/D/X/F), Format, GetName(s),
// GetValues, Parse/TryParse, IsDefined — BCL's signatures and behaviour.
//
// The names and values come from the image's reflection metadata, as in stock
// NativeAOT: the enum's type definition by its MethodTable (ImageMetadata), its
// literal fields and [Flags] read the way
// System.Private.Reflection.Execution/NativeFormatEnumInfo.cs reads them
// (release/8.0, MIT), sorted by value as Enum.GetEnumInfo does.
//
// Ported from dotnet/runtime release/8.0
// src/libraries/System.Private.CoreLib/src/System/Enum.cs: the [Flags]
// formatting (GetSingleFlagsEnumNameForValue, TryFindFlagsNames,
// WriteMultipleFoundFlagsNames), the parse by names (TryParseByName), the
// format letters.
//
// SharpOS cuts:
// - An enum with no metadata (a generic type's nested enum, which stock reads
//   through its definition) formats and parses by number; GetNames and
//   GetValues answer empty, IsDefined true.
// - GetValues(Type) gives an object[] of the boxed members, not an array of
//   the enum type: there is no Array.CreateInstance.
// - No IFormattable/ISpanFormattable/IConvertible on Enum itself.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Internal.Metadata.NativeFormat;
using SharpOS.Std.NoRuntime;

namespace System
{
    public abstract partial class Enum
    {
        // Upstream EnumInfo<TStorage>: names and values sorted by value
        // (unsigned, at the width of the underlying type), and [Flags].
        private sealed class EnumInfo
        {
            public string[] Names;
            public ulong[] Values;
            public bool Flags;
        }

        // Made on first use: an initializer here would give Enum a class
        // constructor, and the kernel runs Enum code before its statics exist.
        private static Dictionary<nint, EnumInfo> s_infos;
        private static object s_infoLock;

        // --- ToString ------------------------------------------------------

        public override unsafe string ToString()
        {
            object box = this;
            GcMethodTable* table = TableOf(box);
            return Format(table, RawBits(table, DataOf(box)), 'G');
        }

        public unsafe string ToString(string? format)
        {
            object box = this;
            GcMethodTable* table = TableOf(box);
            return Format(table, RawBits(table, DataOf(box)), FormatLetter(format));
        }

        public string ToString(string? format, IFormatProvider? provider) => ToString(format);

        public static unsafe string Format(Type enumType, object value, string format)
        {
            CheckEnumType(enumType);
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (format == null) throw new ArgumentNullException(nameof(format));
            var table = (GcMethodTable*)enumType._handle;
            return Format(table, BitsOfValue(table, value), FormatLetter(format));
        }

        // Upstream's internal TryFormatUnconstrained (Enum.cs), which the
        // interpolated string handlers call.
        internal static unsafe bool TryFormatUnconstrained<TEnum>(TEnum value, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default)
        {
            var table = (GcMethodTable*)typeof(TEnum)._handle;
            string text = Format(table, RawBits(table, (byte*)Unsafe.AsPointer(ref value)), FormatLetter(format));
            if (text.AsSpan().TryCopyTo(destination))
            {
                charsWritten = text.Length;
                return true;
            }
            charsWritten = 0;
            return false;
        }

        private static char FormatLetter(ReadOnlySpan<char> format)
        {
            if (format.Length == 0) return 'G';
            if (format.Length == 1)
            {
                switch (format[0] | 0x20)
                {
                    case 'g': return 'G';
                    case 'd': return 'D';
                    case 'x': return 'X';
                    case 'f': return 'F';
                }
            }
            throw new FormatException("Format string can be only \"G\", \"g\", \"X\", \"x\", \"F\", \"f\", \"D\" or \"d\".");
        }

        private static unsafe string Format(GcMethodTable* table, ulong bits, char letter)
        {
            if (letter == 'D') return FormatNumber(table->ElementType, bits);
            if (letter == 'X') return FormatHex(table->ElementType, bits);

            EnumInfo info = GetEnumInfo(table);
            if (info != null)
            {
                if (letter == 'F' || info.Flags)
                {
                    string? text = FormatFlagNames(info, bits);
                    if (text != null) return text;
                }
                else
                {
                    int index = IndexOfValue(info, bits);
                    if (index >= 0) return info.Names[index];
                }
            }
            return FormatNumber(table->ElementType, bits);
        }

        // Upstream FormatFlagNames over GetSingleFlagsEnumNameForValue and
        // TryFindFlagsNames: the largest values first, each one whose bits are
        // all set taken out; null when bits remain no member covers.
        private static string? FormatFlagNames(EnumInfo info, ulong resultValue)
        {
            string[] names = info.Names;
            ulong[] values = info.Values;
            if (resultValue == 0)
                return values.Length > 0 && values[0] == 0 ? names[0] : "0";

            int index;
            for (index = values.Length - 1; (uint)index < (uint)values.Length; index--)
            {
                if (values[index] <= resultValue)
                {
                    if (values[index] == resultValue) return names[index];
                    break;
                }
            }

            Span<int> foundItems = stackalloc int[64];
            int foundItemsCount = 0;
            while ((uint)index < (uint)values.Length)
            {
                ulong currentValue = values[index];
                if (index == 0 && currentValue == 0)
                    break;
                if ((resultValue & currentValue) == currentValue)
                {
                    resultValue &= ~currentValue;
                    foundItems[foundItemsCount++] = index;
                    if (resultValue == 0)
                        break;
                }
                index--;
            }
            if (resultValue != 0)
                return null;

            // Smallest value first, as upstream writes them.
            var sb = new System.Text.StringBuilder();
            for (int i = foundItemsCount - 1; i >= 0; i--)
            {
                if (i != foundItemsCount - 1) sb.Append(", ");
                sb.Append(names[foundItems[i]]);
            }
            return sb.ToString();
        }

        private static string FormatNumber(GcEETypeElementType kind, ulong bits) => kind switch
        {
            GcEETypeElementType.SByte => ((sbyte)bits).ToString(),
            GcEETypeElementType.Byte => ((byte)bits).ToString(),
            GcEETypeElementType.Int16 => ((short)bits).ToString(),
            GcEETypeElementType.UInt16 => ((ushort)bits).ToString(),
            GcEETypeElementType.Int32 => ((int)bits).ToString(),
            GcEETypeElementType.UInt32 => ((uint)bits).ToString(),
            GcEETypeElementType.Int64 => ((long)bits).ToString(),
            _ => bits.ToString(),
        };

        // "X": the storage in hex, all its digits, upper case.
        private static string FormatHex(GcEETypeElementType kind, ulong bits)
        {
            int digits = 2 * StorageSize(kind);
            var chars = new char[digits];
            for (int i = digits - 1; i >= 0; i--, bits >>= 4)
                chars[i] = "0123456789ABCDEF"[(int)(bits & 0xF)];
            return new string(chars);
        }

        // --- Names and values ----------------------------------------------

        public static string? GetName<TEnum>(TEnum value) where TEnum : struct, Enum => GetName(typeof(TEnum), value);

        public static unsafe string? GetName(Type enumType, object value)
        {
            CheckEnumType(enumType);
            if (value == null) throw new ArgumentNullException(nameof(value));
            var table = (GcMethodTable*)enumType._handle;
            ulong bits = BitsOfValue(table, value);
            EnumInfo info = GetEnumInfo(table);
            if (info == null) return null;
            int index = IndexOfValue(info, bits);
            return index >= 0 ? info.Names[index] : null;
        }

        public static string[] GetNames<TEnum>() where TEnum : struct, Enum => GetNames(typeof(TEnum));

        public static unsafe string[] GetNames(Type enumType)
        {
            CheckEnumType(enumType);
            EnumInfo info = GetEnumInfo((GcMethodTable*)enumType._handle);
            if (info == null) return Array.Empty<string>();
            var result = new string[info.Names.Length];
            Array.Copy(info.Names, result, result.Length);
            return result;
        }

        public static unsafe TEnum[] GetValues<TEnum>() where TEnum : struct, Enum
        {
            EnumInfo info = GetEnumInfo((GcMethodTable*)typeof(TEnum)._handle);
            if (info == null) return Array.Empty<TEnum>();
            var result = new TEnum[info.Values.Length];
            for (int i = 0; i < result.Length; i++)
            {
                ulong bits = info.Values[i];
                result[i] = Unsafe.As<ulong, TEnum>(ref bits);
            }
            return result;
        }

        public static unsafe Array GetValues(Type enumType)
        {
            CheckEnumType(enumType);
            EnumInfo info = GetEnumInfo((GcMethodTable*)enumType._handle);
            if (info == null) return Array.Empty<object>();
            var result = new object[info.Values.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = ToObjectCore(enumType, unchecked((long)info.Values[i]));
            return result;
        }

        public static bool IsDefined<TEnum>(TEnum value) where TEnum : struct, Enum => IsDefined(typeof(TEnum), value);

        public static unsafe bool IsDefined(Type enumType, object value)
        {
            CheckEnumType(enumType);
            if (value == null) throw new ArgumentNullException(nameof(value));
            var table = (GcMethodTable*)enumType._handle;
            EnumInfo info = GetEnumInfo(table);
            if (info == null) return true;
            if (value is string name)
                return Array.IndexOf(info.Names, name) >= 0;
            return IndexOfValue(info, BitsOfValue(table, value)) >= 0;
        }

        // --- Parse ---------------------------------------------------------

        public static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum => (TEnum)Parse(typeof(TEnum), value, ignoreCase: false);

        public static TEnum Parse<TEnum>(string value, bool ignoreCase) where TEnum : struct, Enum => (TEnum)Parse(typeof(TEnum), value, ignoreCase);

        public static object Parse(Type enumType, string value) => Parse(enumType, value, ignoreCase: false);

        public static object Parse(Type enumType, string value, bool ignoreCase)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            CheckEnumType(enumType);
            if (value.AsSpan().Trim().Length == 0)
                throw new ArgumentException("Must specify valid information for parsing in the string.", nameof(value));
            if (TryParseCore(enumType, value, ignoreCase, out object? result))
                return result!;
            throw new ArgumentException("Requested value '" + value + "' was not found.");
        }

        public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct =>
            TryParse(value, ignoreCase: false, out result);

        public static bool TryParse<TEnum>(string? value, bool ignoreCase, out TEnum result) where TEnum : struct
        {
            if (value != null && typeof(TEnum).IsEnum && TryParseCore(typeof(TEnum), value, ignoreCase, out object? boxed))
            {
                result = (TEnum)boxed!;
                return true;
            }
            result = default;
            return false;
        }

        public static bool TryParse(Type enumType, string? value, out object? result) =>
            TryParse(enumType, value, ignoreCase: false, out result);

        public static bool TryParse(Type enumType, string? value, bool ignoreCase, out object? result)
        {
            CheckEnumType(enumType);
            result = null;
            return value != null && TryParseCore(enumType, value, ignoreCase, out result);
        }

        // Upstream TryParseByValueOrName: a number when it starts like one,
        // else names separated by commas, their values or-ed together.
        private static unsafe bool TryParseCore(Type enumType, string value, bool ignoreCase, out object? result)
        {
            result = null;
            ReadOnlySpan<char> text = value.AsSpan().Trim();
            if (text.Length == 0) return false;

            if (char.IsAsciiDigit(text[0]) || text[0] == '-' || text[0] == '+')
            {
                string number = text.ToString();
                if (long.TryParse(number, out long signed)) { result = ToObjectCore(enumType, signed); return true; }
                if (ulong.TryParse(number, out ulong unsigned)) { result = ToObjectCore(enumType, unchecked((long)unsigned)); return true; }
            }

            EnumInfo info = GetEnumInfo((GcMethodTable*)enumType._handle);
            if (info == null) return false;

            ulong bits = 0;
            while (true)
            {
                int comma = text.IndexOf(',');
                ReadOnlySpan<char> part = (comma < 0 ? text : text.Slice(0, comma)).Trim();
                if (part.Length == 0) return false;
                int index = IndexOfName(info, part, ignoreCase);
                if (index < 0) return false;
                bits |= info.Values[index];
                if (comma < 0) break;
                text = text.Slice(comma + 1);
            }
            result = ToObjectCore(enumType, unchecked((long)bits));
            return true;
        }

        private static int IndexOfName(EnumInfo info, ReadOnlySpan<char> name, bool ignoreCase)
        {
            string[] names = info.Names;
            for (int i = 0; i < names.Length; i++)
                if (name.Equals(names[i].AsSpan(), ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return i;
            return -1;
        }

        // The first member with this value (values are sorted), or -1.
        private static int IndexOfValue(EnumInfo info, ulong bits)
        {
            ulong[] values = info.Values;
            int lo = 0, hi = values.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >>> 1;
                if (values[mid] < bits) lo = mid + 1;
                else hi = mid;
            }
            return lo < values.Length && values[lo] == bits ? lo : -1;
        }

        // --- The enum's members, from metadata -----------------------------

        private static unsafe EnumInfo GetEnumInfo(GcMethodTable* table)
        {
            if (s_infoLock == null) System.Threading.Interlocked.CompareExchange(ref s_infoLock, new object(), null);
            lock (s_infoLock)
            {
                s_infos ??= new Dictionary<nint, EnumInfo>();
                if (s_infos.TryGetValue((nint)table, out EnumInfo known)) return known;
                EnumInfo info = ReadEnumInfo(table);
                s_infos[(nint)table] = info;
                return info;
            }
        }

        // Stock NativeFormatEnumInfo.GetEnumValuesAndNames, the values kept as
        // the underlying type's bits, then sorted by them (stable).
        private static unsafe EnumInfo ReadEnumInfo(GcMethodTable* table)
        {
            if (!ImageMetadata.TryGetTypeDefinition(table, out TypeDefinitionHandle typeDefHandle))
                return null;
            MetadataReader reader = ImageMetadata.Reader;
            TypeDefinition typeDef = reader.GetTypeDefinition(typeDefHandle);

            var names = new List<string>();
            var values = new List<ulong>();
            foreach (FieldHandle fieldHandle in typeDef.Fields)
            {
                Field field = fieldHandle.GetField(reader);
                if (0 == (field.Flags & System.Reflection.FieldAttributes.Static))
                    continue;
                Handle handle = field.DefaultValue;
                ulong bits = handle.HandleType switch
                {
                    HandleType.ConstantSByteValue => (byte)handle.ToConstantSByteValueHandle(reader).GetConstantSByteValue(reader).Value,
                    HandleType.ConstantByteValue => handle.ToConstantByteValueHandle(reader).GetConstantByteValue(reader).Value,
                    HandleType.ConstantInt16Value => (ushort)handle.ToConstantInt16ValueHandle(reader).GetConstantInt16Value(reader).Value,
                    HandleType.ConstantUInt16Value => handle.ToConstantUInt16ValueHandle(reader).GetConstantUInt16Value(reader).Value,
                    HandleType.ConstantInt32Value => (uint)handle.ToConstantInt32ValueHandle(reader).GetConstantInt32Value(reader).Value,
                    HandleType.ConstantUInt32Value => handle.ToConstantUInt32ValueHandle(reader).GetConstantUInt32Value(reader).Value,
                    HandleType.ConstantInt64Value => (ulong)handle.ToConstantInt64ValueHandle(reader).GetConstantInt64Value(reader).Value,
                    HandleType.ConstantUInt64Value => handle.ToConstantUInt64ValueHandle(reader).GetConstantUInt64Value(reader).Value,
                    _ => throw new InvalidOperationException("an enum field without a constant value"),
                };
                names.Add(reader.GetString(field.Name));
                values.Add(bits);
            }

            bool flags = false;
            foreach (CustomAttributeHandle cah in typeDef.CustomAttributes)
            {
                if (ImageMetadata.IsCustomAttributeOfType(cah, reader, "System", "FlagsAttribute"))
                {
                    flags = true;
                    break;
                }
            }

            // Insertion sort: stable, and an enum is short.
            var info = new EnumInfo { Names = names.ToArray(), Values = values.ToArray(), Flags = flags };
            for (int i = 1; i < info.Values.Length; i++)
            {
                ulong v = info.Values[i];
                string n = info.Names[i];
                int j = i - 1;
                while (j >= 0 && info.Values[j] > v)
                {
                    info.Values[j + 1] = info.Values[j];
                    info.Names[j + 1] = info.Names[j];
                    j--;
                }
                info.Values[j + 1] = v;
                info.Names[j + 1] = n;
            }
            return info;
        }

        // --- Values of boxes -----------------------------------------------

        private static unsafe GcMethodTable* TableOf(object box) => *(GcMethodTable**)*(void**)Unsafe.AsPointer(ref box);

        private static unsafe byte* DataOf(object box) => *(byte**)Unsafe.AsPointer(ref box) + sizeof(nint);

        private static int StorageSize(GcEETypeElementType kind) => kind switch
        {
            GcEETypeElementType.SByte or GcEETypeElementType.Byte or GcEETypeElementType.Boolean => 1,
            GcEETypeElementType.Int16 or GcEETypeElementType.UInt16 or GcEETypeElementType.Char => 2,
            GcEETypeElementType.Int32 or GcEETypeElementType.UInt32 => 4,
            _ => 8,
        };

        // The stored bits, zero-extended: how EnumInfo keeps values.
        private static unsafe ulong RawBits(GcMethodTable* table, byte* data) => StorageSize(table->ElementType) switch
        {
            1 => *data,
            2 => *(ushort*)data,
            4 => *(uint*)data,
            _ => *(ulong*)data,
        };

        // A value for GetName/IsDefined/Format: a box of this enum, or of an
        // integer — truncated to the enum's width, as upstream converts it.
        private static unsafe ulong BitsOfValue(GcMethodTable* table, object value)
        {
            ulong bits;
            if (value is Enum)
            {
                if (TableOf(value) != table)
                    throw new ArgumentException("Object must be the same type as the enum.", nameof(value));
                return RawBits(table, DataOf(value));
            }
            switch (value)
            {
                case int v: bits = unchecked((ulong)v); break;
                case long v: bits = unchecked((ulong)v); break;
                case sbyte v: bits = unchecked((ulong)v); break;
                case byte v: bits = v; break;
                case short v: bits = unchecked((ulong)v); break;
                case ushort v: bits = v; break;
                case uint v: bits = v; break;
                case ulong v: bits = v; break;
                default: throw new ArgumentException("The value passed in must be an enum base or an underlying type for an enum, such as an Int32.", nameof(value));
            }
            int size = StorageSize(table->ElementType);
            return size == 8 ? bits : bits & ((1UL << (size * 8)) - 1);
        }
    }
}
