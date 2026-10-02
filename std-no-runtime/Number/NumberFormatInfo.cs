// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/NumberFormatInfo.cs
//
// The invariant data is upstream's own field initializers, unchanged, so
// NumberFormatInfo.InvariantInfo is the real invariant NumberFormatInfo. The
// UTF-8 (TChar) accessors the formatter uses are upstream's.
//
// SharpOS cuts / changes (each marked inline):
//   * internal NumberFormatInfo(CultureData) - there is no culture database;
//     every culture here is the invariant one.
//   * s_asciiDigits - used only by Number.Parsing, which is not ported; as a
//     static field with an initializer it would also give the type a class
//     constructor.
//   * InvariantInfo builds the read-only instance itself (release/7.0 shape):
//     upstream asks CultureInfo.InvariantCulture.NumberFormat, and our
//     CultureInfo answers that with InvariantInfo - a loop.
//   * CurrentInfo returns InvariantInfo directly: CultureInfo.CurrentCulture
//     is always the read-only invariant culture, whose NumberFormat is
//     InvariantInfo. Going there directly skips allocating a CultureInfo.
//   * GetInstance's CultureInfo fast path asks cultureProvider.NumberFormat:
//     our CultureInfo has no _isInherited / _numInfo pair.
//   * Clone() and the group-size/NativeDigits getters copy fields and arrays
//     by hand: Object.MemberwiseClone and Array.Clone do not exist here.
//   * VerifyNativeDigits skips the CharUnicodeInfo check (digit value and
//     PrivateUse category): there are no Unicode category tables. Count,
//     null and length/surrogate-pair checks are kept.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace System.Globalization
{
    /// <remarks>
    /// Property             Default Description
    /// PositiveSign           '+'   Character used to indicate positive values.
    /// NegativeSign           '-'   Character used to indicate negative values.
    /// NumberDecimalSeparator '.'   The character used as the decimal separator.
    /// NumberGroupSeparator   ','   The character used to separate groups of
    ///                              digits to the left of the decimal point.
    /// NumberDecimalDigits    2     The default number of decimal places.
    /// NumberGroupSizes       3     The number of digits in each group to the
    ///                              left of the decimal point.
    /// NaNSymbol             "NaN"  The string used to represent NaN values.
    /// PositiveInfinitySymbol"Infinity" The string used to represent positive
    ///                              infinities.
    /// NegativeInfinitySymbol"-Infinity" The string used to represent negative
    ///                              infinities.
    ///
    /// Property                  Default  Description
    /// CurrencyDecimalSeparator  '.'      The character used as the decimal
    ///                                    separator.
    /// CurrencyGroupSeparator    ','      The character used to separate groups
    ///                                    of digits to the left of the decimal
    ///                                    point.
    /// CurrencyDecimalDigits     2        The default number of decimal places.
    /// CurrencyGroupSizes        3        The number of digits in each group to
    ///                                    the left of the decimal point.
    /// CurrencyPositivePattern   0        The format of positive values.
    /// CurrencyNegativePattern   0        The format of negative values.
    /// CurrencySymbol            "$"      String used as local monetary symbol.
    /// </remarks>
    public sealed class NumberFormatInfo : IFormatProvider, ICloneable
    {
        private static volatile NumberFormatInfo? s_invariantInfo;
        // SharpOS cut: s_asciiDigits - see header.

        internal int[] _numberGroupSizes = new int[] { 3 };
        internal int[] _currencyGroupSizes = new int[] { 3 };
        internal int[] _percentGroupSizes = new int[] { 3 };
        internal string _positiveSign = "+";
        internal string _negativeSign = "-";
        internal string _numberDecimalSeparator = ".";
        internal string _numberGroupSeparator = ",";
        internal string _currencyGroupSeparator = ",";
        internal string _currencyDecimalSeparator = ".";
        internal string _currencySymbol = "\x00a4";  // U+00a4 is the symbol for International Monetary Fund.
        internal string _nanSymbol = "NaN";
        internal string _positiveInfinitySymbol = "Infinity";
        internal string _negativeInfinitySymbol = "-Infinity";
        internal string _percentDecimalSeparator = ".";
        internal string _percentGroupSeparator = ",";
        internal string _percentSymbol = "%";
        internal string _perMilleSymbol = "\u2030";

        internal byte[]? _positiveSignUtf8;
        internal byte[]? _negativeSignUtf8;
        internal byte[]? _currencySymbolUtf8;
        internal byte[]? _numberDecimalSeparatorUtf8;
        internal byte[]? _currencyDecimalSeparatorUtf8;
        internal byte[]? _currencyGroupSeparatorUtf8;
        internal byte[]? _numberGroupSeparatorUtf8;
        internal byte[]? _percentSymbolUtf8;
        internal byte[]? _percentDecimalSeparatorUtf8;
        internal byte[]? _percentGroupSeparatorUtf8;
        internal byte[]? _perMilleSymbolUtf8;
        internal byte[]? _nanSymbolUtf8;
        internal byte[]? _positiveInfinitySymbolUtf8;
        internal byte[]? _negativeInfinitySymbolUtf8;

        internal string[] _nativeDigits = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };

        internal int _numberDecimalDigits = 2;
        internal int _currencyDecimalDigits = 2;
        internal int _currencyPositivePattern;
        internal int _currencyNegativePattern;
        internal int _numberNegativePattern = 1;
        internal int _percentPositivePattern;
        internal int _percentNegativePattern;
        internal int _percentDecimalDigits = 2;

        internal int _digitSubstitution = (int)DigitShapes.None;

        internal bool _isReadOnly;

        private bool _hasInvariantNumberSigns = true;

        // When _allowHyphenDuringParsing is set to true, we'll allow the number parser to accept the hyphen `-` U+002D as a negative sign even if the culture
        // negative sign is not the hyphen. For example, the Swedish culture (e.g. "sv-SE") has U+2212 as the negative sign.
        private bool _allowHyphenDuringParsing;

        public NumberFormatInfo()
        {
        }

        private static void VerifyNativeDigits(string[] nativeDig, string propertyName)
        {
            ArgumentNullException.ThrowIfNull(nativeDig);

            if (nativeDig.Length != 10)
            {
                throw new ArgumentException(SR.Argument_InvalidNativeDigitCount, propertyName);
            }

            for (int i = 0; i < nativeDig.Length; i++)
            {
                if (nativeDig[i] == null)
                {
                    throw new ArgumentNullException(propertyName, SR.ArgumentNull_ArrayValue);
                }

                if (nativeDig[i].Length != 1)
                {
                    if (nativeDig[i].Length != 2)
                    {
                        // Not 1 or 2 UTF-16 code points
                        throw new ArgumentException(SR.Argument_InvalidNativeDigitValue, propertyName);
                    }
                    else if (!char.IsSurrogatePair(nativeDig[i][0], nativeDig[i][1]))
                    {
                        // 2 UTF-6 code points, but not a surrogate pair
                        throw new ArgumentException(SR.Argument_InvalidNativeDigitValue, propertyName);
                    }
                }

                // SharpOS cut: CharUnicodeInfo digit-value / PrivateUse check - see header.
            }
        }

        private static void VerifyDigitSubstitution(DigitShapes digitSub, string propertyName)
        {
            switch (digitSub)
            {
                case DigitShapes.Context:
                case DigitShapes.None:
                case DigitShapes.NativeNational:
                    // Success.
                    break;

                default:
                    throw new ArgumentException(SR.Argument_InvalidDigitSubstitution, propertyName);
            }
        }

        internal bool HasInvariantNumberSigns => _hasInvariantNumberSigns;
        internal bool AllowHyphenDuringParsing => _allowHyphenDuringParsing;

        private void InitializeInvariantAndNegativeSignFlags()
        {
            _hasInvariantNumberSigns = _positiveSign == "+" && _negativeSign == "-";

            // The list of the Minus characters are picked up from the CLDR parse lenient data.
            // e.g. https://github.com/unicode-org/cldr/blob/feb602b06bd18ba7333464bd648b68292e8aa54d/common/main/sw.xml#L1001

            _allowHyphenDuringParsing = _negativeSign.Length == 1 &&
                                        _negativeSign[0] switch {
                                            '\u2012' or         // Figure Dash
                                            '\u207B' or         // Superscript Minus
                                            '\u208B' or         // Subscript Minus
                                            '\u2212' or         // Minus Sign
                                            '\u2796' or         // Heavy Minus Sign
                                            '\uFE63' or         // Small Hyphen-Minus
                                            '\uFF0D' => true,   // Fullwidth Hyphen-Minus
                                            _ => false
                                        };
        }

        // SharpOS cut: internal NumberFormatInfo(CultureData) - see header.

        private void VerifyWritable()
        {
            if (_isReadOnly)
            {
                throw new InvalidOperationException(SR.InvalidOperation_ReadOnly);
            }
        }

        /// <summary>
        /// Returns a default NumberFormatInfo that will be universally
        /// supported and constant irrespective of the current culture.
        /// Used by FromString methods.
        /// </summary>
        public static NumberFormatInfo InvariantInfo => s_invariantInfo ??=
            // Lazy create the invariant info. This cannot be done in a .cctor because exceptions can
            // be thrown out of a .cctor stack that will need this.
            new NumberFormatInfo { _isReadOnly = true }; // SharpOS: see header

        public static NumberFormatInfo GetInstance(IFormatProvider? formatProvider)
        {
            return formatProvider == null ?
                CurrentInfo : // Fast path for a null provider
                GetProviderNonNull(formatProvider);

            static NumberFormatInfo GetProviderNonNull(IFormatProvider provider)
            {
                // Fast path for a regular CultureInfo
                // SharpOS: no _isInherited / _numInfo on our CultureInfo - see header.
                if (provider is CultureInfo cultureProvider)
                {
                    return cultureProvider.NumberFormat;
                }

                return
                    provider as NumberFormatInfo ?? // Fast path for an NFI
                    provider.GetFormat(typeof(NumberFormatInfo)) as NumberFormatInfo ??
                    CurrentInfo;
            }
        }

        public object Clone()
        {
            NumberFormatInfo n = MemberwiseCloneFields(); // SharpOS: no MemberwiseClone - see header.
            n._isReadOnly = false;
            return n;
        }

        // SharpOS: field-by-field stand-in for Object.MemberwiseClone (shallow,
        // like the original: arrays are shared, and setters replace them rather
        // than writing into them). The cached UTF-8 copies are left null; they
        // are rebuilt from the strings on first use.
        private NumberFormatInfo MemberwiseCloneFields()
        {
            NumberFormatInfo n = new NumberFormatInfo();
            n._numberGroupSizes = _numberGroupSizes;
            n._currencyGroupSizes = _currencyGroupSizes;
            n._percentGroupSizes = _percentGroupSizes;
            n._positiveSign = _positiveSign;
            n._negativeSign = _negativeSign;
            n._numberDecimalSeparator = _numberDecimalSeparator;
            n._numberGroupSeparator = _numberGroupSeparator;
            n._currencyGroupSeparator = _currencyGroupSeparator;
            n._currencyDecimalSeparator = _currencyDecimalSeparator;
            n._currencySymbol = _currencySymbol;
            n._nanSymbol = _nanSymbol;
            n._positiveInfinitySymbol = _positiveInfinitySymbol;
            n._negativeInfinitySymbol = _negativeInfinitySymbol;
            n._percentDecimalSeparator = _percentDecimalSeparator;
            n._percentGroupSeparator = _percentGroupSeparator;
            n._percentSymbol = _percentSymbol;
            n._perMilleSymbol = _perMilleSymbol;
            n._nativeDigits = _nativeDigits;
            n._numberDecimalDigits = _numberDecimalDigits;
            n._currencyDecimalDigits = _currencyDecimalDigits;
            n._currencyPositivePattern = _currencyPositivePattern;
            n._currencyNegativePattern = _currencyNegativePattern;
            n._numberNegativePattern = _numberNegativePattern;
            n._percentPositivePattern = _percentPositivePattern;
            n._percentNegativePattern = _percentNegativePattern;
            n._percentDecimalDigits = _percentDecimalDigits;
            n._digitSubstitution = _digitSubstitution;
            n._isReadOnly = _isReadOnly;
            n._hasInvariantNumberSigns = _hasInvariantNumberSigns;
            n._allowHyphenDuringParsing = _allowHyphenDuringParsing;
            return n;
        }

        // SharpOS: stand-in for Array.Clone on the arrays this type hands out.
        private static T[] CloneArray<T>(T[] array)
        {
            T[] copy = new T[array.Length];
            for (int i = 0; i < array.Length; i++)
            {
                copy[i] = array[i];
            }
            return copy;
        }

        public int CurrencyDecimalDigits
        {
            get => _currencyDecimalDigits;
            set
            {
                if (value < 0 || value > 99)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 99);
                }

                VerifyWritable();
                _currencyDecimalDigits = value;
            }
        }

        public string CurrencyDecimalSeparator
        {
            get => _currencyDecimalSeparator;
            set
            {
                VerifyWritable();
                ArgumentException.ThrowIfNullOrEmpty(value);
                _currencyDecimalSeparator = value;
                _currencyDecimalSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> CurrencyDecimalSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_currencyDecimalSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_currencyDecimalSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_currencyDecimalSeparator));
        }

        public bool IsReadOnly => _isReadOnly;

        /// <summary>
        /// Check the values of the groupSize array.
        /// Every element in the groupSize array should be between 1 and 9
        /// except the last element could be zero.
        /// </summary>
        internal static void CheckGroupSize(string propName, int[] groupSize)
        {
            for (int i = 0; i < groupSize.Length; i++)
            {
                if (groupSize[i] < 1)
                {
                    if (i == groupSize.Length - 1 && groupSize[i] == 0)
                    {
                        return;
                    }

                    throw new ArgumentException(SR.Argument_InvalidGroupSize, propName);
                }
                else if (groupSize[i] > 9)
                {
                    throw new ArgumentException(SR.Argument_InvalidGroupSize, propName);
                }
            }
        }

        public int[] CurrencyGroupSizes
        {
            get => CloneArray(_currencyGroupSizes); // SharpOS: no Array.Clone
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();

                int[] inputSizes = CloneArray(value); // SharpOS: no Array.Clone
                CheckGroupSize(nameof(value), inputSizes);
                _currencyGroupSizes = inputSizes;
            }
        }

        public int[] NumberGroupSizes
        {
            get => CloneArray(_numberGroupSizes); // SharpOS: no Array.Clone
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();

                int[] inputSizes = CloneArray(value); // SharpOS: no Array.Clone
                CheckGroupSize(nameof(value), inputSizes);
                _numberGroupSizes = inputSizes;
            }
        }

        public int[] PercentGroupSizes
        {
            get => CloneArray(_percentGroupSizes); // SharpOS: no Array.Clone
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                int[] inputSizes = CloneArray(value); // SharpOS: no Array.Clone
                CheckGroupSize(nameof(value), inputSizes);
                _percentGroupSizes = inputSizes;
            }
        }

        public string CurrencyGroupSeparator
        {
            get => _currencyGroupSeparator;
            set
            {
                VerifyWritable();
                ArgumentNullException.ThrowIfNull(value);
                _currencyGroupSeparator = value;
                _currencyGroupSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> CurrencyGroupSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_currencyGroupSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_currencyGroupSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_currencyGroupSeparator));
        }

        public string CurrencySymbol
        {
            get => _currencySymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _currencySymbol = value;
                _currencySymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> CurrencySymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_currencySymbol) :
                MemoryMarshal.Cast<byte, TChar>(_currencySymbolUtf8 ??= Encoding.UTF8.GetBytes(_currencySymbol));
        }

        internal byte[]? CurrencySymbolUtf8 => _currencySymbolUtf8 ??= Encoding.UTF8.GetBytes(_currencySymbol);

        /// <summary>
        /// Returns the current culture's NumberFormatInfo. Used by Parse methods.
        /// </summary>

        public static NumberFormatInfo CurrentInfo
        {
            get
            {
                // SharpOS: CultureInfo.CurrentCulture is always the read-only
                // invariant culture, whose NumberFormat is InvariantInfo - see header.
                return InvariantInfo;
            }
        }

        public string NaNSymbol
        {
            get => _nanSymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _nanSymbol = value;
                _nanSymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> NaNSymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_nanSymbol) :
                MemoryMarshal.Cast<byte, TChar>(_nanSymbolUtf8 ??= Encoding.UTF8.GetBytes(_nanSymbol));
        }

        public int CurrencyNegativePattern
        {
            get => _currencyNegativePattern;
            set
            {
                if (value < 0 || value > 16)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 16);
                }

                VerifyWritable();
                _currencyNegativePattern = value;
            }
        }

        public int NumberNegativePattern
        {
            get => _numberNegativePattern;
            set
            {
                // NOTENOTE: the range of value should correspond to negNumberFormats[] in vm\COMNumber.cpp.
                if (value < 0 || value > 4)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 4);
                }

                VerifyWritable();
                _numberNegativePattern = value;
            }
        }

        public int PercentPositivePattern
        {
            get => _percentPositivePattern;
            set
            {
                // NOTENOTE: the range of value should correspond to posPercentFormats[] in vm\COMNumber.cpp.
                if (value < 0 || value > 3)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 3);
                }

                VerifyWritable();
                _percentPositivePattern = value;
            }
        }

        public int PercentNegativePattern
        {
            get => _percentNegativePattern;
            set
            {
                // NOTENOTE: the range of value should correspond to posPercentFormats[] in vm\COMNumber.cpp.
                if (value < 0 || value > 11)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 11);
                }

                VerifyWritable();
                _percentNegativePattern = value;
            }
        }

        public string NegativeInfinitySymbol
        {
            get => _negativeInfinitySymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _negativeInfinitySymbol = value;
                _negativeInfinitySymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> NegativeInfinitySymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_negativeInfinitySymbol) :
                MemoryMarshal.Cast<byte, TChar>(_negativeInfinitySymbolUtf8 ??= Encoding.UTF8.GetBytes(_negativeInfinitySymbol));
        }

        public string NegativeSign
        {
            get => _negativeSign;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _negativeSign = value;
                _negativeSignUtf8 = null;
                InitializeInvariantAndNegativeSignFlags();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> NegativeSignTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_negativeSign) :
                MemoryMarshal.Cast<byte, TChar>(_negativeSignUtf8 ??= Encoding.UTF8.GetBytes(_negativeSign));
        }

        public int NumberDecimalDigits
        {
            get => _numberDecimalDigits;
            set
            {
                if (value < 0 || value > 99)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 99);
                }

                VerifyWritable();
                _numberDecimalDigits = value;
            }
        }

        public string NumberDecimalSeparator
        {
            get => _numberDecimalSeparator;
            set
            {
                VerifyWritable();
                ArgumentException.ThrowIfNullOrEmpty(value);
                _numberDecimalSeparator = value;
                _numberDecimalSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> NumberDecimalSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_numberDecimalSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_numberDecimalSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_numberDecimalSeparator));
        }

        public string NumberGroupSeparator
        {
            get => _numberGroupSeparator;
            set
            {
                VerifyWritable();
                ArgumentNullException.ThrowIfNull(value);
                _numberGroupSeparator = value;
                _numberGroupSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> NumberGroupSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_numberGroupSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_numberGroupSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_numberGroupSeparator));
        }

        public int CurrencyPositivePattern
        {
            get => _currencyPositivePattern;
            set
            {
                if (value < 0 || value > 3)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 3);
                }

                VerifyWritable();
                _currencyPositivePattern = value;
            }
        }

        public string PositiveInfinitySymbol
        {
            get => _positiveInfinitySymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _positiveInfinitySymbol = value;
                _positiveInfinitySymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PositiveInfinitySymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_positiveInfinitySymbol) :
                MemoryMarshal.Cast<byte, TChar>(_positiveInfinitySymbolUtf8 ??= Encoding.UTF8.GetBytes(_positiveInfinitySymbol));
        }

        public string PositiveSign
        {
            get => _positiveSign;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _positiveSign = value;
                _positiveSignUtf8 = null;
                InitializeInvariantAndNegativeSignFlags();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PositiveSignTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_positiveSign) :
                MemoryMarshal.Cast<byte, TChar>(_positiveSignUtf8 ??= Encoding.UTF8.GetBytes(_positiveSign));
        }

        public int PercentDecimalDigits
        {
            get => _percentDecimalDigits;
            set
            {
                if (value < 0 || value > 99)
                {
                    ThrowHelper.ThrowArgumentOutOfRange_Range(nameof(value), value, 0, 99);
                }

                VerifyWritable();
                _percentDecimalDigits = value;
            }
        }

        public string PercentDecimalSeparator
        {
            get => _percentDecimalSeparator;
            set
            {
                VerifyWritable();
                ArgumentException.ThrowIfNullOrEmpty(value);
                _percentDecimalSeparator = value;
                _percentDecimalSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PercentDecimalSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_percentDecimalSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_percentDecimalSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_percentDecimalSeparator));
        }

        public string PercentGroupSeparator
        {
            get => _percentGroupSeparator;
            set
            {
                VerifyWritable();
                ArgumentNullException.ThrowIfNull(value);
                _percentGroupSeparator = value;
                _percentGroupSeparatorUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PercentGroupSeparatorTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_percentGroupSeparator) :
                MemoryMarshal.Cast<byte, TChar>(_percentGroupSeparatorUtf8 ??= Encoding.UTF8.GetBytes(_percentGroupSeparator));
        }

        public string PercentSymbol
        {
            get => _percentSymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                VerifyWritable();
                _percentSymbol = value;
                _percentSymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PercentSymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_percentSymbol) :
                MemoryMarshal.Cast<byte, TChar>(_percentSymbolUtf8 ??= Encoding.UTF8.GetBytes(_percentSymbol));
        }

        public string PerMilleSymbol
        {
            get => _perMilleSymbol;
            set
            {
                ArgumentNullException.ThrowIfNull(value);

                VerifyWritable();
                _perMilleSymbol = value;
                _perMilleSymbolUtf8 = null;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ReadOnlySpan<TChar> PerMilleSymbolTChar<TChar>() where TChar : unmanaged, IUtfChar<TChar>
        {
            Debug.Assert(typeof(TChar) == typeof(char) || typeof(TChar) == typeof(byte));
            return typeof(TChar) == typeof(char) ?
                MemoryMarshal.Cast<char, TChar>(_perMilleSymbol) :
                MemoryMarshal.Cast<byte, TChar>(_perMilleSymbolUtf8 ??= Encoding.UTF8.GetBytes(_perMilleSymbol));
        }

        public string[] NativeDigits
        {
            get => CloneArray(_nativeDigits); // SharpOS: no Array.Clone
            set
            {
                VerifyWritable();
                VerifyNativeDigits(value, nameof(value));
                _nativeDigits = value;
            }
        }

        public DigitShapes DigitSubstitution
        {
            get => (DigitShapes)_digitSubstitution;
            set
            {
                VerifyWritable();
                VerifyDigitSubstitution(value, nameof(value));
                _digitSubstitution = (int)value;
            }
        }

        public object? GetFormat(Type? formatType)
        {
            return formatType == typeof(NumberFormatInfo) ? this : null;
        }

        public static NumberFormatInfo ReadOnly(NumberFormatInfo nfi)
        {
            ArgumentNullException.ThrowIfNull(nfi);

            if (nfi.IsReadOnly)
            {
                return nfi;
            }

            NumberFormatInfo info = nfi.MemberwiseCloneFields(); // SharpOS: no MemberwiseClone
            info._isReadOnly = true;
            return info;
        }

        // private const NumberStyles InvalidNumberStyles = unchecked((NumberStyles) 0xFFFFFC00);
        private const NumberStyles InvalidNumberStyles = ~(NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite
                                                           | NumberStyles.AllowLeadingSign | NumberStyles.AllowTrailingSign
                                                           | NumberStyles.AllowParentheses | NumberStyles.AllowDecimalPoint
                                                           | NumberStyles.AllowThousands | NumberStyles.AllowExponent
                                                           | NumberStyles.AllowCurrencySymbol | NumberStyles.AllowHexSpecifier
                                                           | NumberStyles.AllowBinarySpecifier);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ValidateParseStyleInteger(NumberStyles style)
        {
            // Check for undefined flags or using AllowHexSpecifier/AllowBinarySpecifier each with anything other than AllowLeadingWhite/AllowTrailingWhite.
            if ((style & (InvalidNumberStyles | NumberStyles.AllowHexSpecifier | NumberStyles.AllowBinarySpecifier)) != 0 &&
                (style & ~NumberStyles.HexNumber) != 0 &&
                (style & ~NumberStyles.BinaryNumber) != 0)
            {
                ThrowInvalid(style);

                static void ThrowInvalid(NumberStyles value)
                {
                    throw new ArgumentException(
                        (value & InvalidNumberStyles) != 0 ? SR.Argument_InvalidNumberStyles : SR.Arg_InvalidHexBinaryStyle,
                        nameof(style));
                }
            }
        }

        internal static void ValidateParseStyleFloatingPoint(NumberStyles style)
        {
            // Check for undefined flags or hex number
            if ((style & (InvalidNumberStyles | NumberStyles.AllowHexSpecifier | NumberStyles.AllowBinarySpecifier)) != 0)
            {
                ThrowInvalid(style);

                static void ThrowInvalid(NumberStyles value) =>
                    throw new ArgumentException((value & InvalidNumberStyles) != 0 ? SR.Argument_InvalidNumberStyles : SR.Arg_HexBinaryStylesNotSupported, nameof(style));
            }
        }
    }
}
