// App-local stand-in — see Compat\Drawing.cs for why these live here.
//
// Only the members the upstream Famicom Disk System loader touches, which is
// exactly one: ReadBytes. That path is dead weight for us (we run cartridges,
// not disks) but it sits inside Emulator.cs and therefore has to compile.
//
// Deliberately NOT put in std: a BinaryReader with one method is not a
// BinaryReader, and std's canonical namespaces are reserved for types real BCL
// code can be dropped onto unchanged.

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
            byte[] one = ReadBytes(1);
            if (one.Length == 0) throw new IOException("End of stream.");
            return one[0];
        }

        public void Dispose() { }
    }
}
