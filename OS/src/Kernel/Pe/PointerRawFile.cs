using System;
using System.Runtime.InteropServices;
using System.Text;
using PeNet.FileParser;

namespace OS.Kernel.Pe
{
    // A PE file read where it already lies — the program cache's buffer — so
    // the loader does not copy it into a managed byte[] first. That copy was
    // garbage the moment the image was placed, yet it grew the kernel's
    // managed heap by a segment sized to the EXE on every launch (1-8 MiB),
    // and the kernel's segments are never handed back (done/step202.md).
    //
    // Read-only: the loader only parses headers. The caller keeps the memory
    // alive and unchanged for as long as the parsers are used.
    internal sealed unsafe class PointerRawFile : IRawFile
    {
        private readonly byte* _data;
        private readonly int _length;

        public PointerRawFile(byte* data, int length)
        {
            _data = data;
            _length = length;
        }

        public long Length => _length;

        private void Check(long offset, long count)
        {
            if (offset < 0 || count < 0 || offset + count > _length)
                throw new ArgumentOutOfRangeException(nameof(offset));
        }

        public byte ReadByte(long offset) { Check(offset, 1); return _data[offset]; }

        public ushort ReadUShort(long offset)
        {
            Check(offset, 2);
            return MemoryMarshal.Read<ushort>(new ReadOnlySpan<byte>(_data + offset, 2));
        }

        public uint ReadUInt(long offset)
        {
            Check(offset, 4);
            return MemoryMarshal.Read<uint>(new ReadOnlySpan<byte>(_data + offset, 4));
        }

        public ulong ReadULong(long offset)
        {
            Check(offset, 8);
            return MemoryMarshal.Read<ulong>(new ReadOnlySpan<byte>(_data + offset, 8));
        }

        public string ReadAsciiString(long offset)
        {
            Check(offset, 0);
            long end = offset;
            while (end < _length && _data[end] != 0)
                end++;
            return Encoding.ASCII.GetString(_data + offset, (int)(end - offset));
        }

        public string ReadUnicodeString(long offset)
        {
            Check(offset, 0);
            long end = offset;
            while (end + 1 < _length && !(_data[end] == 0 && _data[end + 1] == 0))
                end += 2;
            return Encoding.Unicode.GetString(_data + offset, (int)(end - offset));
        }

        public string ReadUnicodeString(long offset, long length)
        {
            Check(offset, length * 2);
            return Encoding.Unicode.GetString(_data + offset, (int)length * 2);
        }

        public Span<byte> AsSpan(long offset, long length)
        {
            Check(offset, length);
            return new Span<byte>(_data + offset, (int)length);
        }

        public byte[] ToArray()
        {
            var copy = new byte[_length];
            new ReadOnlySpan<byte>(_data, _length).CopyTo(copy);
            return copy;
        }

        public void WriteByte(long offset, byte value) => throw ReadOnly();
        public void WriteUShort(long offset, ushort value) => throw ReadOnly();
        public void WriteUInt(long offset, uint value) => throw ReadOnly();
        public void WriteULong(long offset, ulong value) => throw ReadOnly();
        public void WriteBytes(long offset, Span<byte> bytes) => throw ReadOnly();
        public void RemoveRange(long offset, long length) => throw ReadOnly();
        public int AppendBytes(Span<byte> bytes) => throw ReadOnly();

        private static NotSupportedException ReadOnly()
            => new NotSupportedException("the PE file is read where it lies and cannot be changed");
    }
}
