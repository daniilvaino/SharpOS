// System.IO.Stream — minimal port from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/IO/Stream.cs (MIT)
// plus the FileMode/FileAccess/SeekOrigin enums (verbatim values).
//
// Cuts vs original:
//   - All async surface (ReadAsync/WriteAsync/BeginRead/EndRead/...).
//   - Span overloads (Read(Span<byte>) / Write(ReadOnlySpan<byte>)).
//   - CopyTo/CopyToAsync, timeouts, CanTimeout, Null stream, synchronized
//     wrapper, TextReader/TextWriter integration.
// Kept: the sync byte[] Read/Write/Seek core + ReadExactly (net7+) that
// ported app code (ManagedDoom WAD/save readers) actually calls.

using System;

namespace System.IO
{
    public enum SeekOrigin
    {
        Begin = 0,
        Current = 1,
        End = 2,
    }

    public enum FileMode
    {
        CreateNew = 1,
        Create = 2,
        Open = 3,
        OpenOrCreate = 4,
        Truncate = 5,
        Append = 6,
    }

    [Flags]
    public enum FileAccess
    {
        Read = 1,
        Write = 2,
        ReadWrite = 3,
    }

    public abstract class Stream : IDisposable
    {
        public abstract bool CanRead { get; }
        public abstract bool CanSeek { get; }
        public abstract bool CanWrite { get; }
        public abstract long Length { get; }
        public abstract long Position { get; set; }

        public abstract int Read(byte[] buffer, int offset, int count);
        public abstract long Seek(long offset, SeekOrigin origin);
        public abstract void Write(byte[] buffer, int offset, int count);
        public abstract void Flush();
        public abstract void SetLength(long value);

        public virtual int ReadByte()
        {
            byte[] oneByteArray = new byte[1];
            int r = Read(oneByteArray, 0, 1);
            return r == 0 ? -1 : oneByteArray[0];
        }

        public virtual void WriteByte(byte value)
        {
            byte[] oneByteArray = new byte[1] { value };
            Write(oneByteArray, 0, 1);
        }

        // net7+ surface: read exactly the requested byte count or throw.
        public void ReadExactly(byte[] buffer)
        {
            ReadExactly(buffer, 0, buffer.Length);
        }

        public void ReadExactly(byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = Read(buffer, offset + total, count - total);
                if (read <= 0)
                    throw new EndOfStreamException("Unable to read beyond the end of the stream.");
                total += read;
            }
        }

        public virtual void Close() => Dispose(true);

        public void Dispose() => Dispose(true);

        protected virtual void Dispose(bool disposing) { }
    }
}
