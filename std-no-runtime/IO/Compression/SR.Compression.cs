// SR keys of System.IO.Compression (dotnet/runtime v8.0.27 Strings.resx), generated.
// Only the keys no other SR partial in std already has.

namespace System
{
    internal static partial class SR
    {
        internal const string CannotReadFromDeflateStream = @"Reading from the compression stream is not supported.";
        internal const string CannotWriteToDeflateStream = @"Writing to the compression stream is not supported.";
        internal const string GenericInvalidData = @"Found invalid data while decoding.";
        internal const string InvalidBeginCall = @"Only one asynchronous reader or writer is allowed time at one time.";
        internal const string InvalidBlockLength = @"Block length does not match with its complement.";
        internal const string InvalidHuffmanData = @"Failed to construct a huffman tree using the length array. The stream might be corrupted.";
        internal const string NotSupported = @"This operation is not supported.";
        internal const string UnknownBlockType = @"Unknown block type. Stream might be corrupted.";
        internal const string UnknownState = @"Decoder is in some unknown state. This might be caused by corrupted data.";
        internal const string ZLibErrorDLLLoadError = @"The underlying compression routine could not be loaded correctly.";
        internal const string ZLibErrorInconsistentStream = @"The stream state of the underlying compression routine is inconsistent.";
        internal const string ZLibErrorIncorrectInitParameters = @"The underlying compression routine received incorrect initialization parameters.";
        internal const string ZLibErrorNotEnoughMemory = @"The underlying compression routine could not reserve sufficient memory.";
        internal const string ZLibErrorVersionMismatch = @"The version of the underlying compression routine does not match expected version.";
        internal const string ZLibErrorUnexpected = @"The underlying compression routine returned an unexpected error code.";
        internal const string CDCorrupt = @"Central Directory corrupt.";
        internal const string CentralDirectoryInvalid = @"Central Directory is invalid.";
        internal const string CreateInReadMode = @"Cannot create entries on an archive opened in read mode.";
        internal const string CreateModeCapabilities = @"Cannot use create mode on a non-writable stream.";
        internal const string CreateModeCreateEntryWhileOpen = @"Entries cannot be created while previously created entries are still open.";
        internal const string CreateModeWriteOnceAndOneEntryAtATime = @"Entries in create mode may only be written to once, and only one entry may be held open at a time.";
        internal const string DateTimeOutOfRange = @"The DateTimeOffset specified cannot be converted into a Zip file timestamp.";
        internal const string DeletedEntry = @"Cannot modify deleted entry.";
        internal const string DeleteOnlyInUpdate = @"Delete can only be used when the archive is in Update mode.";
        internal const string DeleteOpenEntry = @"Cannot delete an entry currently open for writing.";
        internal const string EntriesInCreateMode = @"Cannot access entries in Create mode.";
        internal const string EntryNameAndCommentEncodingNotSupported = @"The specified encoding is not supported for entry names and comments.";
        internal const string EntryNamesTooLong = @"Entry names cannot require more than 2^16 bits.";
        internal const string EntryTooLarge = @"Entries larger than 4GB are not supported in Update mode.";
        internal const string EOCDNotFound = @"End of Central Directory record could not be found.";
        internal const string FieldTooBigCompressedSize = @"Compressed Size cannot be held in an Int64.";
        internal const string FieldTooBigLocalHeaderOffset = @"Local Header Offset cannot be held in an Int64.";
        internal const string FieldTooBigNumEntries = @"Number of Entries cannot be held in an Int64.";
        internal const string FieldTooBigOffsetToCD = @"Offset to Central Directory cannot be held in an Int64.";
        internal const string FieldTooBigOffsetToZip64EOCD = @"Offset to Zip64 End Of Central Directory record cannot be held in an Int64.";
        internal const string FieldTooBigUncompressedSize = @"Uncompressed Size cannot be held in an Int64.";
        internal const string FrozenAfterWrite = @"Cannot modify entry in Create mode after entry has been opened for writing.";
        internal const string HiddenStreamName = @"A stream from ZipArchiveEntry has been disposed.";
        internal const string LengthAfterWrite = @"Length properties are unavailable once an entry has been opened for writing.";
        internal const string LocalFileHeaderCorrupt = @"A local file header is corrupt.";
        internal const string NumEntriesWrong = @"Number of entries expected in End Of Central Directory does not correspond to number of entries in Central Directory.";
        internal const string ReadingNotSupported = @"This stream from ZipArchiveEntry does not support reading.";
        internal const string ReadModeCapabilities = @"Cannot use read mode on a non-readable stream.";
        internal const string ReadOnlyArchive = @"Cannot modify read-only archive.";
        internal const string SeekingNotSupported = @"This stream from ZipArchiveEntry does not support seeking.";
        internal const string SplitSpanned = @"Split or spanned archives are not supported.";
        internal const string TruncatedData = @"Found truncated data while decoding.";
        internal const string UnexpectedEndOfStream = @"Zip file corrupt: unexpected end of stream reached.";
        internal const string UnsupportedCompression = @"The archive entry was compressed using an unsupported compression method.";
        internal const string UnsupportedCompressionMethod = @"The archive entry was compressed using {0} and is not supported.";
        internal const string UpdateModeCapabilities = @"Update mode requires a stream with read, write, and seek capabilities.";
        internal const string UpdateModeOneStream = @"Entries cannot be opened multiple times in Update mode.";
        internal const string WritingNotSupported = @"This stream from ZipArchiveEntry does not support writing.";
        internal const string Zip64EOCDNotWhereExpected = @"Zip 64 End of Central Directory Record not where indicated.";
        internal const string PlatformNotSupported_Compression = @"System.IO.Compression is not supported on this platform.";
    }
}
