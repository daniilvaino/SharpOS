// App-local stand-in for System.IO.BinaryReader.
//
// Only the members upstream touches: ReadBytes and ReadByte, which is how
// Cartridge.Load walks the iNES header and the ROM banks behind it.
//
// Deliberately NOT in std, even though this is now the second app to carry a
// copy (apps_native\TriCNES\Compat\BinaryReader.cs is the same file). std's
// canonical System.* namespaces are reserved for types that real BCL code can
// be dropped onto unchanged, and a BinaryReader missing ReadInt32, ReadString
// and its Encoding-aware constructors is not that — putting it there would
// make every future port fail somewhere further from the cause. Promoting it
// means porting the real one, which needs Encoding first; recorded in
// donext.md rather than half-done here.

namespace System.IO
{
    public sealed class BinaryReader : IDisposable
    {
        private readonly Stream _stream;

        public BinaryReader(Stream input)
        {
            _stream = input ?? throw new ArgumentNullException(nameof(input));
        }

        public Stream BaseStream => _stream;

        /// <summary>
        /// Reads up to <paramref name="count"/> bytes. Like the original, a
        /// short read returns a shorter array rather than throwing — the
        /// caller checks lengths.
        /// </summary>
        public byte[] ReadBytes(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (count == 0) return new byte[0];

            byte[] buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = _stream.Read(buffer, total, count - total);
                if (read == 0) break;
                total += read;
            }

            if (total == count) return buffer;

            byte[] shortened = new byte[total];
            Array.Copy(buffer, 0, shortened, 0, total);
            return shortened;
        }

        public byte ReadByte()
        {
            int value = _stream.ReadByte();
            if (value < 0) throw new IOException("End of stream.");
            return (byte)value;
        }

        public sbyte ReadSByte() => (sbyte)ReadByte();

        public bool ReadBoolean() => ReadByte() != 0;

        public short ReadInt16() => (short)ReadLittleEndian(2);

        public ushort ReadUInt16() => (ushort)ReadLittleEndian(2);

        public int ReadInt32() => (int)ReadLittleEndian(4);

        public uint ReadUInt32() => (uint)ReadLittleEndian(4);

        public long ReadInt64() => (long)ReadLittleEndian(8);

        public ulong ReadUInt64() => ReadLittleEndian(8);

        public int Read(byte[] buffer, int index, int count) => _stream.Read(buffer, index, count);

        // Little-endian, matching the BCL on x64 and Compat\BinaryWriter.cs.
        private ulong ReadLittleEndian(int count)
        {
            ulong value = 0;
            for (int i = 0; i < count; i++)
            {
                value |= (ulong)ReadByte() << (i * 8);
            }
            return value;
        }

        public void Dispose() { }
    }
}
