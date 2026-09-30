// System.IO.StreamReader — line-reader over a Stream, API shape from
// dotnet/runtime v8.0.27 (MIT). ManagedDoom uses exactly
// `new StreamReader(stream)` + ReadLine()-until-null (DeHackEd parsing).
//
// Cuts vs original:
//   - TextReader base class (no other TextReader in this std yet).
//   - Encoding detection / BOM handling — bytes decode 1:1 as Latin-1
//     (DOOM assets are ASCII). Revisit when a UTF-8 caller appears.
//   - Buffered decoder loop, async surface, Peek/Read(char[],..)/
//     ReadToEnd/ReadBlock, leaveOpen ctor family.

using System.Text;

namespace System.IO
{
    public class StreamReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly byte[] _byteBuffer;
        private int _byteLen;
        private int _bytePos;

        public StreamReader(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            _stream = stream;
            _byteBuffer = new byte[1024];
        }

        public Stream BaseStream => _stream;

        // Returns the next line without the terminator ('\n' or "\r\n"),
        // or null at end of stream. A trailing line without '\n' is returned.
        public string ReadLine()
        {
            if (_bytePos >= _byteLen && !FillBuffer())
                return null;

            var sb = new StringBuilder();
            do
            {
                while (_bytePos < _byteLen)
                {
                    byte b = _byteBuffer[_bytePos++];
                    if (b == (byte)'\n')
                        return sb.ToString();
                    if (b != (byte)'\r')
                        sb.Append((char)b);
                }
            }
            while (FillBuffer());

            return sb.ToString();
        }

        private bool FillBuffer()
        {
            _bytePos = 0;
            _byteLen = _stream.Read(_byteBuffer, 0, _byteBuffer.Length);
            return _byteLen > 0;
        }

        public void Dispose()
        {
            _stream.Dispose();
        }
    }
}
