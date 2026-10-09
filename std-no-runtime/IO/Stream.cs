// System.IO.Stream — minimal port from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/IO/Stream.cs (MIT)
// plus the FileMode/FileAccess/SeekOrigin enums (verbatim values).
//
// Cuts vs original:
//   - BeginRead/EndRead/BeginWrite/EndWrite; ReadAsync/WriteAsync/FlushAsync
//     exist (step198) but their base versions complete synchronously.
//   - CopyTo/CopyToAsync, timeouts, CanTimeout, synchronized
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
        // A stream with no backing store: reads find nothing, writes vanish.
        // Fresh per access rather than a static readonly singleton — the
        // original's is, and a static with an initializer in std reaches code
        // that runs before statics exist.
        public static Stream Null => new NullStream();

        private sealed class NullStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => true;
            public override long Length => 0;
            public override long Position { get => 0; set { } }
            public override int Read(byte[] buffer, int offset, int count) => 0;
            public override int Read(Span<byte> buffer) => 0;
            public override int ReadByte() => -1;
            public override long Seek(long offset, SeekOrigin origin) => 0;
            public override void Write(byte[] buffer, int offset, int count) { }
            public override void Write(ReadOnlySpan<byte> buffer) { }
            public override void WriteByte(byte value) { }
            public override void Flush() { }
            public override void SetLength(long length) { }
        }

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

        // Span overloads, as the original's base implementations: through a
        // rented array and the byte[] core. Added for the BinaryWriter /
        // BinaryReader port; a stream with a span-native path overrides them.
        public virtual int Read(Span<byte> buffer)
        {
            byte[] sharedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                int numRead = Read(sharedBuffer, 0, buffer.Length);
                if ((uint)numRead > (uint)buffer.Length)
                    throw new IOException("Stream.Read returned more bytes than requested.");
                new ReadOnlySpan<byte>(sharedBuffer, 0, numRead).CopyTo(buffer);
                return numRead;
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(sharedBuffer);
            }
        }

        // Async surface (step198: System.Text.Json's ParseAsync, BabyKusto). The
        // members are the BCL's; the base implementations complete
        // synchronously through the sync core — upstream's run the sync call
        // on the thread pool (BeginRead/EndRead), which std does not have. A
        // stream with a real asynchronous path overrides them, as upstream.
        public System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, default);

        public virtual System.Threading.Tasks.Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return System.Threading.Tasks.Task.FromCanceled<int>(cancellationToken);
            try { return System.Threading.Tasks.Task.FromResult(Read(buffer, offset, count)); }
            catch (Exception e) { return System.Threading.Tasks.Task.FromException<int>(e); }
        }

        public virtual System.Threading.Tasks.ValueTask<int> ReadAsync(Memory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return System.Threading.Tasks.ValueTask.FromCanceled<int>(cancellationToken);
            try { return new System.Threading.Tasks.ValueTask<int>(Read(buffer.Span)); }
            catch (Exception e) { return System.Threading.Tasks.ValueTask.FromException<int>(e); }
        }

        public System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer, offset, count, default);

        public virtual System.Threading.Tasks.Task WriteAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
            try { Write(buffer, offset, count); return System.Threading.Tasks.Task.CompletedTask; }
            catch (Exception e) { return System.Threading.Tasks.Task.FromException(e); }
        }

        public virtual System.Threading.Tasks.ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, System.Threading.CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return System.Threading.Tasks.ValueTask.FromCanceled(cancellationToken);
            try { Write(buffer.Span); return default; }
            catch (Exception e) { return System.Threading.Tasks.ValueTask.FromException(e); }
        }

        public System.Threading.Tasks.Task FlushAsync() => FlushAsync(default);

        public virtual System.Threading.Tasks.Task FlushAsync(System.Threading.CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return System.Threading.Tasks.Task.FromCanceled(cancellationToken);
            try { Flush(); return System.Threading.Tasks.Task.CompletedTask; }
            catch (Exception e) { return System.Threading.Tasks.Task.FromException(e); }
        }

        public virtual void Write(ReadOnlySpan<byte> buffer)
        {
            byte[] sharedBuffer = System.Buffers.ArrayPool<byte>.Shared.Rent(buffer.Length);
            try
            {
                buffer.CopyTo(sharedBuffer);
                Write(sharedBuffer, 0, buffer.Length);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(sharedBuffer);
            }
        }

        public void ReadExactly(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = Read(buffer.Slice(total));
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
