// zlib's crc32(crc, buf, len) over this class's update — for
// System.IO.Compression (Crc32Helper, the gzip trailer), where BCL calls the
// native zlib. zlib takes and returns the finished value, so the state is
// inverted on the way in and out.

namespace System.IO.Hashing
{
    public sealed partial class Crc32
    {
        internal static uint ZLibUpdate(uint crc, ReadOnlySpan<byte> source) => ~Update(~crc, source);
    }
}
