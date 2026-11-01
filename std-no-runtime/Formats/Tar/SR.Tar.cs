// Resource strings for the System.Formats.Tar port, with the texts of dotnet/runtime v8.0's System.Formats.Tar Strings.resx (MIT).
// Only the keys no other SR partial in std already has.

namespace System
{
    internal static partial class SR
    {
        internal const string ArgumentOutOfRange_FileLengthTooBig = @"Specified file length was too large for the file system.";
        internal const string ArgumentInvalidPathChars = @"The argument '{0}' contains invalid path characters.";
        internal const string ErrorStatFile = @"Could not determine the file type of '0'.";
        internal const string IO_AlreadyExists_Name = @"Cannot create '{0}' because a file or directory with the same name already exists.";
        internal const string IO_DeviceFiles_NotSupported = @"Creating block or character device files is not supported on the current platform.";
        internal const string IO_FileExists_Name = @"The file '{0}' already exists.";
        internal const string IO_FileNotFound = @"Unable to find the specified file.";
        internal const string IO_FileNotFound_FileName = @"Could not find file '{0}'.";
        internal const string IO_FifoFiles_NotSupported = @"Creating fifo files is not supported on the current platform.";
        internal const string IO_NotSupported_UnreadableStream = @"The stream does not support reading.";
        internal const string IO_NotSupported_UnseekableStream = @"The stream does not support seeking.";
        internal const string IO_NotSupported_UnwritableStream = @"The stream does not support writing.";
        internal const string IO_PathNotFound_NoPathName = @"Could not find a part of the path.";
        internal const string IO_PathNotFound_Path = @"Could not find a part of the path '{0}'.";
        internal const string IO_PathTooLong = @"The specified file name or path is too long, or a component of the specified path is too long.";
        internal const string IO_PathTooLong_Path = @"The path '{0}' is too long, or a component of the specified path is too long.";
        internal const string IO_SetLengthRequiresSeekingAndWriting = @"SetLength requires a stream that supports seeking and writing.";
        internal const string IO_SharingViolation_File = @"The process cannot access the file '{0}' because it is being used by another process.";
        internal const string IO_SharingViolation_NoFileName = @"The process cannot access the file because it is being used by another process.";
        internal const string IO_StreamNotEmpty = @"The stream is not empty.";
        internal const string PlatformNotSupported_SystemFormatsTar = @"System.Formats.Tar is not supported on this platform.";
        internal const string SetLengthRequiresSeekingAndWriting = @"SetLength requires a stream that supports seeking and writing.";
        internal const string TarCannotConvertPaxGlobalExtendedAttributesEntry = @"Cannot convert a PaxGlobalExtendedAttributesEntry into another format.";
        internal const string TarDuplicateExtendedAttribute = @"The entry '{0}' has a duplicate extended attribute.";
        internal const string TarEntryBlockOrCharacterExpected = @"Cannot set the 'DeviceMajor' or 'DeviceMinor' fields on an entry that does not represent a block or character device.";
        internal const string TarEntryDoesNotSupportDataStream = @"The entry '{0}' has a '{1}' type, which does not support setting a data stream.";
        internal const string TarEntryHardLinkOrSymLinkExpected = @"Cannot set the LinkName field on an entry that does not represent a hard link or a symbolic link.";
        internal const string TarEntryHardLinkOrSymlinkLinkNameEmpty = @"The entry is a symbolic link or a hard link but the LinkName field is null or empty.";
        internal const string TarEntryTypeNotSupported = @"Entry type '{0}' not supported.";
        internal const string TarEntryTypeNotSupportedInFormat = @"Entry type '{0}' not supported in format '{1}'.";
        internal const string TarEntryTypeNotSupportedForExtracting = @"Entry type '{0}' not supported for extraction.";
        internal const string TarExtractingResultsFileOutside = @"Extracting the Tar entry '{0}' would have resulted in a file outside the specified destination directory: '{1}'";
        internal const string TarExtractingResultsLinkOutside = @"Extracting the Tar entry '{0}' would have resulted in a link target outside the specified destination directory: '{1}'";
        internal const string TarGnuFormatExpected = @"Entry '{0}' was expected to be in the GNU format, but did not have the expected version data.";
        internal const string TarInvalidFormat = @"The archive format is invalid: '{0}'";
        internal const string TarPosixFormatExpected = @"A POSIX format was expected (Ustar or PAX), but could not be reliably determined for entry '{0}'.";
        internal const string TarSizeFieldNegative = @"The size field is negative in a tar entry.";
        internal const string TarSizeFieldTooLargeForEntryType = @"The value of the size field for the current entry of type '{0}' is greater than the expected length.";
        internal const string TarUnexpectedMetadataEntry = @"A metadata entry of type '{0}' was unexpectedly found after a metadata entry of type '{1}'.";
        internal const string TarUnsupportedFile = @"The file type of '{0}' is not supported for tar archiving.";
        internal const string UnauthorizedAccess_IODenied_NoPathName = @"Access to the path is denied.";
        internal const string UnauthorizedAccess_IODenied_Path = @"Access to the path '{0}' is denied.";
        internal const string TarInvalidNumber = @"Unable to parse number.";
        internal const string TarEntryFieldExceedsMaxLength = @"The field '{0}' exceeds the maximum allowed length for this format.";
        internal const string TarSizeFieldTooLargeForEntryFormat = @"The value of the size field for the current entry of format '{0}' is greater than the format allows.";
        internal const string TarExtAttrDisallowedKeyChar = @"The extended attribute key '{0}' contains a disallowed '{1}' character.";
        internal const string TarExtAttrDisallowedValueChar = @"The value of the extended attribute key '{0}' contains a disallowed '{1}' character.";
        internal const string TarStreamSeekabilityUnsupportedCombination = @"Cannot write the unseekable data stream of entry '{0}' into an unseekable archive stream.";
    }
}
