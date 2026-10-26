// Resource strings for the System.Uri port, with the texts of dotnet/runtime's
// src/libraries/System.Private.Uri/src/Resources/Strings.resx (.NET 10, MIT). Only the keys the
// ported files use. SR.Format is the shared std one (Number/SR.cs).

namespace System
{
    internal static partial class SR
    {
        internal const string Argument_ExtraNotValid = @"Extra portion of URI not valid.";
        internal const string Argument_InvalidUriSubcomponent = @"The subcomponent, {0}, of this uri is not valid.";
        internal const string net_uri_AlreadyRegistered = @"A URI scheme name '{0}' already has a registered custom parser.";
        internal const string net_uri_BadAuthority = @"Invalid URI: The Authority/Host could not be parsed.";
        internal const string net_uri_BadAuthorityTerminator = @"Invalid URI: The Authority/Host cannot end with a backslash character ('\\').";
        internal const string net_uri_BadFormat = @"Invalid URI: The format of the URI could not be determined.";
        internal const string net_uri_BadHostName = @"Invalid URI: The hostname could not be parsed.";
        internal const string net_uri_BadPort = @"Invalid URI: Invalid port specified.";
        internal const string net_uri_BadScheme = @"Invalid URI: The URI scheme is not valid.";
        internal const string net_uri_BadUnicodeHostForIdn = @"An invalid Unicode character by IDN standards was specified in the host.";
        internal const string net_uri_BadUserPassword = @"Invalid URI: The username:password construct is badly formed.";
        internal const string net_uri_CannotCreateRelative = @"A relative URI cannot be created because the 'uriString' parameter represents an absolute URI.";
        internal const string net_uri_EmptyUri = @"Invalid URI: The URI is empty.";
        internal const string net_uri_GetComponentsCalledWhenCanonicalizationDisabled = @"GetComponents() may not be used for Path/Query on a Uri instance created with UriCreationOptions.DangerousDisablePathAndQueryCanonicalization.";
        internal const string net_uri_InitializeCalledAlreadyOrTooLate = @"UriParser's base InitializeAndValidate may only be called once on a single Uri instance and only from an override of InitializeAndValidate.";
        internal const string net_uri_InvalidUriKind = @"The value '{0}' passed for the UriKind parameter is invalid.";
        internal const string net_uri_MustRootedPath = @"Invalid URI: A Dos path must be rooted, for example, 'c:\\'.";
        internal const string net_uri_NeedFreshParser = @"The URI parser instance passed into 'uriParser' parameter is already registered with the scheme name '{0}'.";
        internal const string net_uri_NotAbsolute = @"This operation is not supported for a relative URI.";
        internal const string net_uri_NotJustSerialization = @"UriComponents.SerializationInfoString must not be combined with other UriComponents.";
        internal const string net_uri_PortOutOfRange = @"A derived type '{0}' has reported an invalid value for the Uri port '{1}'.";
        internal const string net_uri_SchemeLimit = @"Invalid URI: The Uri scheme is too long.";
        internal const string net_uri_UserDrivenParsing = @"A derived type '{0}' is responsible for parsing this Uri instance. The base implementation must not be used.";
    }
}
