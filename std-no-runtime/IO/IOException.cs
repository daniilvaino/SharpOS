// System.IO exception hierarchy — the subset our IO surface throws.
// Shapes mirror dotnet/runtime; cuts: HResult plumbing, serialization
// ctors, FileNotFoundException.FileName/FusionLog extras.

namespace System.IO
{
    public class IOException : Exception
    {
        public IOException() : base("I/O error occurred.") { }
        public IOException(string message) : base(message) { }
        public IOException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class EndOfStreamException : IOException
    {
        public EndOfStreamException() : base("Attempted to read past the end of the stream.") { }
        public EndOfStreamException(string message) : base(message) { }
    }

    public class FileNotFoundException : IOException
    {
        public FileNotFoundException() : base("Unable to find the specified file.") { }
        public FileNotFoundException(string message) : base(message) { }
        public FileNotFoundException(string message, string fileName) : base(message)
        {
            FileName = fileName;
        }

        public string FileName { get; }
    }

    // Data in a stream that is not what the reader can take (step197: a byte
    // stream over a pipe that received an object). Not an IOException, as in
    // BCL; derives from Exception — std has no SystemException.
    public sealed class InvalidDataException : Exception
    {
        public InvalidDataException() : base("Found invalid data while decoding.") { }
        public InvalidDataException(string message) : base(message) { }
        public InvalidDataException(string message, Exception innerException) : base(message, innerException) { }
    }
}
