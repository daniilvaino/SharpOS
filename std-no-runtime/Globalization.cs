// One culture, and it is the invariant one.
//
// Cultures in the real BCL are a database: ICU on disk, hundreds of locales,
// per-locale collation and number formats. We have none of that and are not
// pretending to — this exists so that code written the careful way, passing
// CultureInfo.InvariantCulture to ToString instead of relying on ambient
// state, compiles and does exactly what it asked for.
//
// GetCultures therefore returns the invariant culture alone. That is an honest
// answer rather than an empty one: it IS the list of cultures this system
// supports. Callers that offer the user a language menu will show one entry.

namespace System.Globalization
{
    [Flags]
    public enum CultureTypes
    {
        NeutralCultures = 1,
        SpecificCultures = 2,
        InstalledWin32Cultures = 4,
        AllCultures = NeutralCultures | SpecificCultures | InstalledWin32Cultures,
        UserCustomCulture = 8,
        ReplacementCultures = 16,
    }

    public sealed class CultureInfo : IFormatProvider
    {
        private readonly string _name;

        // BCL: InvariantCulture and CurrentCulture are read-only; a culture
        // made with `new` is not, and neither is its NumberFormat.
        private bool _isReadOnly;
        private NumberFormatInfo? _numInfo;
        internal DateTimeFormatInfo? _dateTimeInfo; // BCL field name: DateTimeFormatInfo.GetInstance reads it
        private Calendar? _calendar;

        public CultureInfo(string name) { _name = name ?? ""; }

        // useUserOverride selects the user's regional settings over the
        // culture's defaults; there are none here.
        public CultureInfo(string name, bool useUserOverride) : this(name) { _ = useUserOverride; }

        /// <summary>A read-only culture by name, as the BCL caches them: the invariant data under that name.</summary>
        public static CultureInfo GetCultureInfo(string name)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(name));
            return new CultureInfo(name) { _isReadOnly = true };
        }

        /// <summary>Ordinal comparison, as invariant globalization mode has it (CompareInfo.cs).</summary>
        public CompareInfo CompareInfo => new CompareInfo(_name);

        /// <summary>Left to right, simple case mapping (TextInfo.cs).</summary>
        public TextInfo TextInfo => new TextInfo(_name);

        // Properties rather than static fields: a lazily initialised static
        // reference field is the one construct this environment cannot run
        // (limits §1). Culture objects are stateless here, so a fresh instance
        // is as good as a shared one.
        public static CultureInfo InvariantCulture => new CultureInfo("") { _isReadOnly = true };
        public static CultureInfo CurrentCulture => InvariantCulture;
        public static CultureInfo CurrentUICulture => InvariantCulture;
        public static CultureInfo DefaultThreadCurrentUICulture => InvariantCulture;

        public string Name => _name;
        public string EnglishName => _name.Length == 0 ? "Invariant Language (Invariant Country)" : _name;
        public string DisplayName => EnglishName;
        public string NativeName => EnglishName;
        public string TwoLetterISOLanguageName => _name.Length >= 2 ? _name.Substring(0, 2) : "iv";

        public bool IsNeutralCulture => _name.Length == 0;

        /// <summary>The culture data (BCL member): every culture here has the invariant data (Globalization/CultureData.cs).</summary>
        internal CultureData _cultureData => CultureData.Invariant;

        /// <summary>
        /// The culture's date and time formatting data: the invariant data, as
        /// CultureData.Invariant has it. A read-only culture hands out the shared
        /// read-only DateTimeFormatInfo.InvariantInfo; a culture made with
        /// <c>new</c> gets its own writable instance, as in the BCL.
        /// </summary>
        public DateTimeFormatInfo DateTimeFormat
        {
            get
            {
                if (_dateTimeInfo == null)
                {
                    _dateTimeInfo = _isReadOnly
                        ? DateTimeFormatInfo.InvariantInfo
                        : new DateTimeFormatInfo(); // writable, invariant data
                }
                return _dateTimeInfo;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value, nameof(value));
                if (_isReadOnly)
                    throw new InvalidOperationException("Instance is read-only.");
                _dateTimeInfo = value;
            }
        }

        /// <summary>The culture's calendar: Gregorian, the invariant culture's only one (read-only for a read-only culture).</summary>
        public Calendar Calendar
        {
            get
            {
                if (_calendar == null)
                {
                    Calendar calendar = new GregorianCalendar();
                    _calendar = _isReadOnly ? Calendar.ReadOnly(calendar) : calendar;
                }
                return _calendar;
            }
        }

        /// <summary>The calendars the culture can use: Gregorian alone, as for the invariant culture.</summary>
        public Calendar[] OptionalCalendars => new Calendar[] { new GregorianCalendar() };

        public bool IsReadOnly => _isReadOnly;

        /// <summary>
        /// The culture's number formatting data: the invariant data, the only
        /// data there is. A read-only culture hands out the shared read-only
        /// NumberFormatInfo.InvariantInfo; a culture made with <c>new</c> gets
        /// its own writable copy, as in the BCL.
        /// </summary>
        public NumberFormatInfo NumberFormat
        {
            get
            {
                if (_numInfo == null)
                {
                    _numInfo = _isReadOnly
                        ? NumberFormatInfo.InvariantInfo
                        : new NumberFormatInfo(); // writable, invariant data
                }
                return _numInfo;
            }
            set
            {
                ArgumentNullException.ThrowIfNull(value, nameof(value));
                if (_isReadOnly)
                    throw new InvalidOperationException("Instance is read-only.");
                _numInfo = value;
            }
        }

        public CultureInfo Parent => InvariantCulture;

        public static CultureInfo[] GetCultures(CultureTypes types)
            => new CultureInfo[] { InvariantCulture };

        public object? GetFormat(Type? formatType)
        {
            if (formatType == typeof(NumberFormatInfo))
            {
                return NumberFormat;
            }
            if (formatType == typeof(DateTimeFormatInfo))
            {
                return DateTimeFormat;
            }
            return null;
        }

        public override string ToString() => _name;
    }
}
