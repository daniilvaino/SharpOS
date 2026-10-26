// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/RegionInfo.cs
// What RegionInfo is in globalization-invariant mode, std's only mode
// (Globalization.cs): there is no region data but the invariant culture's,
// so a region by name is not found — ArgumentException, as the BCL throws
// when CultureData.GetCultureDataForRegion comes back empty — and
// CurrentRegion is the invariant one ("IV", Invariant Country, XDR). The
// member values are CultureData.Invariant's.
//
// SharpOS: the CultureData field is replaced by the invariant values it
// would hold; RegionInfo(int culture) accepts no LCID (only the invariant one
// exists, and it has no region — the BCL's answer for it).

using System.Diagnostics.CodeAnalysis;

namespace System.Globalization
{
    /// <summary>
    /// This class represents settings specified by de jure or de facto
    /// standards for a particular country/region. In contrast to
    /// CultureInfo, the RegionInfo does not represent preferences of the
    /// user and does not depend on the user's language or culture.
    /// </summary>
    public class RegionInfo
    {
        // Name of this region (ie: es-US): serialized, the field used for deserialization
        private string _name;

        // The RegionInfo for our current region
        internal static RegionInfo? s_currentRegionInfo;

        public RegionInfo(string name)
        {
            ArgumentNullException.ThrowIfNull(name, nameof(name));

            // The InvariantCulture has no matching region
            if (name.Length == 0)
            {
                throw new ArgumentException("There is no region associated with the Invariant Culture (Culture ID: 0x7F).", nameof(name));
            }

            // SharpOS: CultureData.GetCultureDataForRegion finds no region in invariant mode.
            throw new ArgumentException("Culture name '" + name + "' is not supported.", nameof(name));
        }

        public RegionInfo(int culture)
        {
            // The InvariantCulture has no matching region
            if (culture == 0x007F)
            {
                throw new ArgumentException("There is no region associated with the Invariant Culture (Culture ID: 0x7F).");
            }

            throw new CultureNotFoundException(nameof(culture), "Culture ID " + culture.ToString() + " is not a supported culture.");
        }

        private RegionInfo()
        {
            _name = "IV";
        }

        /// <summary>
        /// This instance provides methods based on the current user settings.
        /// </summary>
        public static RegionInfo CurrentRegion => s_currentRegionInfo ??= new RegionInfo();

        /// <summary>
        /// Returns the name of the region (ie: en-US)
        /// </summary>
        public virtual string Name => _name;

        public virtual string EnglishName => "Invariant Country";

        public virtual string DisplayName => "Invariant Country";

        public virtual string NativeName => "Invariant Country";

        public virtual string TwoLetterISORegionName => "IV";

        public virtual string ThreeLetterISORegionName => "ivc";

        public virtual string ThreeLetterWindowsRegionName => ThreeLetterISORegionName;

        public virtual bool IsMetric => true;

        public virtual int GeoId => 244;

        public virtual string CurrencyEnglishName => "International Monetary Fund";

        public virtual string CurrencyNativeName => "International Monetary Fund";

        public virtual string CurrencySymbol => "\x00a4";

        public virtual string ISOCurrencySymbol => "XDR";

        public override bool Equals([NotNullWhen(true)] object? value) =>
            value is RegionInfo otherRegion && Name.Equals(otherRegion.Name);

        public override int GetHashCode() => Name.GetHashCode();

        public override string ToString() => Name;
    }
}
