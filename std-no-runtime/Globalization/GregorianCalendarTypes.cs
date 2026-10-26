// Ported from dotnet/runtime (.NET 10 fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/GregorianCalendarTypes.cs
// Verbatim.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Globalization
{
    // Note: The values of the members of this enum must match the corresponding values
    // in the CalendarId enum (since we cast between GregorianCalendarTypes and CalendarId).
    public enum GregorianCalendarTypes
    {
        Localized = CalendarId.GREGORIAN,
        USEnglish = CalendarId.GREGORIAN_US,
        MiddleEastFrench = CalendarId.GREGORIAN_ME_FRENCH,
        Arabic = CalendarId.GREGORIAN_ARABIC,
        TransliteratedEnglish = CalendarId.GREGORIAN_XLIT_ENGLISH,
        TransliteratedFrench = CalendarId.GREGORIAN_XLIT_FRENCH,
    }
}
