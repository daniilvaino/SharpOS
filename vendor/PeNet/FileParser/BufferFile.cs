using System;
using System.Runtime.InteropServices;
using System.Text;

namespace PeNet.FileParser
{
    // Forked from PeNet's BufferFile for the SharpOS NoStdLib std:
    //   - backing store Memory<byte> -> byte[]        (no Memory<T> in our std)
    //   - range-operator slices [a..] -> Span.Slice   (no System.Range in our std)
    //   - MemoryMarshal.Write(span, in v) -> (span, v) (our Write<T> takes T by value)
    //   - null-terminator scans done by hand           (no MemoryExtensions.IndexOf dep)
    //   - byte-array copies via Span.CopyTo            (avoid Array.Copy signature dep)
    // Parsing semantics are otherwise identical to upstream.
    public class BufferFile : IRawFile
    {
        private byte[] _buffer;

        public long Length => _buffer.Length;

        public BufferFile(byte[] file) => _buffer = file;

        public string ReadAsciiString(long offset)
        {
            int start = (int)offset;
            int end = start;
            while (end < _buffer.Length && _buffer[end] != 0)
                end++;
            return Encoding.ASCII.GetString(_buffer, start, end - start);
        }

        public Span<byte> AsSpan(long offset, long length)
            => new Span<byte>(_buffer, (int)offset, (int)length);

        public string ReadUnicodeString(long offset)
        {
            int start = (int)offset;
            int end = start;
            while (end + 1 < _buffer.Length && !(_buffer[end] == 0 && _buffer[end + 1] == 0))
                end += 2;
            return Encoding.Unicode.GetString(_buffer, start, end - start);
        }

        public string ReadUnicodeString(long offset, long length)
            => Encoding.Unicode.GetString(_buffer, (int)offset, (int)length * 2);

        public byte ReadByte(long offset) => _buffer[(int)offset];

        public uint ReadUInt(long offset)
            => MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(_buffer, (int)offset, 4));

        public ulong ReadULong(long offset)
            => MemoryMarshal.Read<ulong>(new ReadOnlySpan<byte>(_buffer, (int)offset, 8));

        public ushort ReadUShort(long offset)
            => MemoryMarshal.Read<ushort>(new ReadOnlySpan<byte>(_buffer, (int)offset, 2));

        public void WriteByte(long offset, byte value) => _buffer[(int)offset] = value;

        public void WriteBytes(long offset, Span<byte> bytes)
            => bytes.CopyTo(new Span<byte>(_buffer, (int)offset, bytes.Length));

        public void WriteUInt(long offset, uint value)
            => MemoryMarshal.Write(new Span<byte>(_buffer, (int)offset, 4), value);

        public void WriteULong(long offset, ulong value)
            => MemoryMarshal.Write(new Span<byte>(_buffer, (int)offset, 8), value);

        public void WriteUShort(long offset, ushort value)
            => MemoryMarshal.Write(new Span<byte>(_buffer, (int)offset, 2), value);

        public byte[] ToArray()
        {
            var copy = new byte[_buffer.Length];
            new Span<byte>(_buffer).CopyTo(new Span<byte>(copy));
            return copy;
        }

        public void RemoveRange(long offset, long length)
        {
            var nb = new byte[_buffer.Length - length];
            new Span<byte>(_buffer, 0, (int)offset).CopyTo(new Span<byte>(nb, 0, (int)offset));
            int tail = (int)(_buffer.Length - offset - length);
            new Span<byte>(_buffer, (int)(offset + length), tail)
                .CopyTo(new Span<byte>(nb, (int)offset, tail));
            _buffer = nb;
        }

        public int AppendBytes(Span<byte> bytes)
        {
            int oldLength = _buffer.Length;
            var nb = new byte[_buffer.Length + bytes.Length];
            new Span<byte>(_buffer).CopyTo(new Span<byte>(nb, 0, oldLength));
            bytes.CopyTo(new Span<byte>(nb, oldLength, bytes.Length));
            _buffer = nb;
            return oldLength;
        }
    }
}
