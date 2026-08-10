// App-local stand-in for System.IO.BinaryWriter — see Compat\BinaryReader.cs
// for why these live here rather than in std.
//
// Nothing in this port writes a savestate. But upstream's savestate code is
// woven through the core — MC6502State, Ppu and every mapper carry WriteState
// and ReadState methods next to the emulation they belong to — so the type has
// to exist for those files to compile. Excluding them is not on the table
// without editing upstream, which is the one thing this port does not do.
//
// Written properly rather than stubbed, because a half-working serializer is a
// trap: the day savestates get wired up, silently truncated writes would look
// like emulator bugs. Little-endian, matching the BCL on x64.

namespace System.IO
{
    public class BinaryWriter : IDisposable
    {
        private readonly Stream _stream;

        public BinaryWriter(Stream output)
        {
            _stream = output ?? throw new ArgumentNullException(nameof(output));
        }

        public Stream BaseStream => _stream;

        public void Write(bool value) => _stream.WriteByte(value ? (byte)1 : (byte)0);

        public void Write(byte value) => _stream.WriteByte(value);

        public void Write(sbyte value) => _stream.WriteByte((byte)value);

        public void Write(short value) => WriteBytes((ulong)(ushort)value, 2);

        public void Write(ushort value) => WriteBytes(value, 2);

        public void Write(int value) => WriteBytes((ulong)(uint)value, 4);

        public void Write(uint value) => WriteBytes(value, 4);

        public void Write(long value) => WriteBytes((ulong)value, 8);

        public void Write(ulong value) => WriteBytes(value, 8);

        public void Write(byte[] buffer)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            _stream.Write(buffer, 0, buffer.Length);
        }

        public void Write(byte[] buffer, int index, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            _stream.Write(buffer, index, count);
        }

        public void Flush() => _stream.Flush();

        public void Dispose() { }

        private void WriteBytes(ulong value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                _stream.WriteByte((byte)(value >> (i * 8)));
            }
        }
    }
}
