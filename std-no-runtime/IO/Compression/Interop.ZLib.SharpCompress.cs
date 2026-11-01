// zlib for System.IO.Compression, in C#: the seven entry points BCL's
// Interop.zlib.cs P/Invokes into libSystem.IO.Compression.Native, over
// SharpCompress's managed zlib (ZlibCodec, a DotNetZip port). Everything
// above them — Deflater, Inflater, DeflateStream, GZipStream, ZLibStream,
// ZipArchive — is BCL's own code, unchanged.
//
// What this adds to the codec, because zlib does it and the codec does not:
//   - the gzip wrapper (windowBits 24..31): header, CRC-32 and size trailer;
//     and windowBits 40..47, gzip or zlib by the first byte;
//   - Z_BUF_ERROR when a call can make no progress — the codec answers Z_OK,
//     and Deflater.Flush loops for as long as it hears Z_OK;
//   - z_stream's pointers: the codec works on arrays, so input and output go
//     through two buffers kept with the stream.
// Its state lives behind a GCHandle in z_stream.internalState, where zlib
// keeps its own.

using System;
using System.IO.Compression;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpCompress.Compressors.Deflate;

internal static partial class Interop
{
    internal static partial class ZLib
    {
        internal static unsafe ZLibNative.ErrorCode DeflateInit2_(
            ZLibNative.ZStream* stream,
            ZLibNative.CompressionLevel level,
            ZLibNative.CompressionMethod method,
            int windowBits,
            int memLevel,
            ZLibNative.CompressionStrategy strategy)
        {
            if (method != ZLibNative.CompressionMethod.Deflated || strategy != ZLibNative.CompressionStrategy.DefaultStrategy)
                return ZLibNative.ErrorCode.StreamError;
            ManagedZStream? state = ManagedZStream.ForDeflate(level, windowBits);
            if (state == null) return ZLibNative.ErrorCode.StreamError;
            stream->internalState = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe ZLibNative.ErrorCode Deflate(ZLibNative.ZStream* stream, ZLibNative.FlushCode flush)
        {
            ManagedZStream? state = ManagedZStream.Of(stream);
            return state == null ? ZLibNative.ErrorCode.StreamError : state.Deflate(stream, flush);
        }

        internal static unsafe ZLibNative.ErrorCode DeflateEnd(ZLibNative.ZStream* stream) => End(stream);

        internal static unsafe ZLibNative.ErrorCode InflateInit2_(ZLibNative.ZStream* stream, int windowBits)
        {
            ManagedZStream? state = ManagedZStream.ForInflate(windowBits);
            if (state == null) return ZLibNative.ErrorCode.StreamError;
            stream->internalState = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            return ZLibNative.ErrorCode.Ok;
        }

        internal static unsafe ZLibNative.ErrorCode Inflate(ZLibNative.ZStream* stream, ZLibNative.FlushCode flush)
        {
            ManagedZStream? state = ManagedZStream.Of(stream);
            return state == null ? ZLibNative.ErrorCode.StreamError : state.Inflate(stream);
        }

        internal static unsafe ZLibNative.ErrorCode InflateEnd(ZLibNative.ZStream* stream) => End(stream);

        internal static unsafe uint crc32(uint crc, byte* buffer, int len) =>
            len <= 0 ? crc : Crc32.ZLibUpdate(crc, new ReadOnlySpan<byte>(buffer, len));

        private static unsafe ZLibNative.ErrorCode End(ZLibNative.ZStream* stream)
        {
            if (stream->internalState == IntPtr.Zero) return ZLibNative.ErrorCode.StreamError;
            GCHandle.FromIntPtr(stream->internalState).Free();
            stream->internalState = IntPtr.Zero;
            return ZLibNative.ErrorCode.Ok;
        }
    }
}

namespace System.IO.Compression
{
    internal sealed unsafe class ManagedZStream
    {
        private enum Wrapper { Raw, ZLib, GZip, Detect }

        private enum Phase { Header, Body, Trailer, Done }

        private const int GZipFlagHeaderCrc = 0x02, GZipFlagExtra = 0x04, GZipFlagName = 0x08, GZipFlagComment = 0x10;

        private readonly ZlibCodec _codec = new ZlibCodec();
        private readonly bool _deflating;
        private Wrapper _wrapper;
        private Phase _phase;

        private byte[] _in = new byte[0];
        private byte[] _out = new byte[0];

        // Bytes owed to the caller's output (gzip header and trailer, when
        // deflating) that did not fit last time.
        private readonly byte[] _pending = new byte[16];
        private int _pendingStart, _pendingCount;

        // gzip: the running CRC-32 and size of the uncompressed data; when
        // inflating, the header and trailer as they arrive.
        private readonly Crc32 _crc = new Crc32();
        private uint _size;
        private readonly byte[] _frame = new byte[10];
        private int _frameCount;
        private int _flags;
        private int _headerStep;
        private int _extraLeft;

        private ManagedZStream(bool deflating) => _deflating = deflating;

        public static ManagedZStream? Of(ZLibNative.ZStream* stream) =>
            stream->internalState == IntPtr.Zero ? null : (ManagedZStream?)GCHandle.FromIntPtr(stream->internalState).Target;

        public static ManagedZStream? ForDeflate(ZLibNative.CompressionLevel level, int windowBits)
        {
            var state = new ManagedZStream(deflating: true);
            int bits;
            if (windowBits >= -15 && windowBits <= -8) { state._wrapper = Wrapper.Raw; bits = -windowBits; }
            else if (windowBits >= 8 && windowBits <= 15) { state._wrapper = Wrapper.ZLib; bits = windowBits; }
            else if (windowBits >= 24 && windowBits <= 31) { state._wrapper = Wrapper.GZip; bits = windowBits - 16; }
            else return null;
            if (bits == 8) bits = 9; // as zlib: a 256-byte window is not supported, 512 is used

            SharpCompress.Compressors.Deflate.CompressionLevel codecLevel = level switch
            {
                ZLibNative.CompressionLevel.DefaultCompression => SharpCompress.Compressors.Deflate.CompressionLevel.Default,
                _ when (int)level >= 0 && (int)level <= 9 => (SharpCompress.Compressors.Deflate.CompressionLevel)(int)level,
                _ => (SharpCompress.Compressors.Deflate.CompressionLevel)(-1),
            };
            if ((int)codecLevel < 0) return null;
            if (state._codec.InitializeDeflate(codecLevel, bits, state._wrapper == Wrapper.ZLib) != ZlibConstants.Z_OK) return null;
            state._phase = state._wrapper == Wrapper.GZip ? Phase.Header : Phase.Body;
            return state;
        }

        public static ManagedZStream? ForInflate(int windowBits)
        {
            var state = new ManagedZStream(deflating: false);
            if (windowBits >= -15 && windowBits <= -8) state._wrapper = Wrapper.Raw;
            else if (windowBits >= 8 && windowBits <= 15) state._wrapper = Wrapper.ZLib;
            else if (windowBits >= 24 && windowBits <= 31) state._wrapper = Wrapper.GZip;
            else if (windowBits >= 40 && windowBits <= 47) state._wrapper = Wrapper.Detect;
            else return null;

            // The codec always gets the window it may need: inflating with a
            // larger one than the data was made with is harmless.
            if (state._wrapper == Wrapper.Raw || state._wrapper == Wrapper.ZLib)
            {
                if (state._codec.InitializeInflate(15, state._wrapper == Wrapper.ZLib) != ZlibConstants.Z_OK) return null;
                state._phase = Phase.Body;
            }
            else
            {
                state._phase = Phase.Header;
            }
            return state;
        }

        // ---- deflate --------------------------------------------------------

        public ZLibNative.ErrorCode Deflate(ZLibNative.ZStream* stream, ZLibNative.FlushCode flush)
        {
            if (stream->availOut == 0) return ZLibNative.ErrorCode.BufError;
            bool progress = false;

            if (_phase == Phase.Header)
            {
                // RFC 1952: magic, deflate, no flags, no time, no extra flags,
                // OS unknown — what zlib writes when given no gz_header.
                Queue(0x1F); Queue(0x8B); Queue(8); Queue(0);
                Queue(0); Queue(0); Queue(0); Queue(0);
                Queue(0); Queue(255);
                _phase = Phase.Body;
            }
            progress |= Drain(stream);

            if (_phase == Phase.Body && _pendingCount == 0 && stream->availOut != 0)
            {
                int consumed, produced, rc;
                try
                {
                    rc = RunCodec(stream, deflate: true, ToFlushType(flush), out consumed, out produced);
                }
                catch (ZlibException)
                {
                    return ZLibNative.ErrorCode.StreamError;
                }
                progress |= consumed != 0 || produced != 0;

                if (rc == ZlibConstants.Z_STREAM_END)
                {
                    if (_wrapper == Wrapper.GZip)
                    {
                        uint crc = _crc.GetCurrentHashAsUInt32();
                        for (int i = 0; i < 4; i++) Queue((byte)(crc >> (8 * i)));
                        for (int i = 0; i < 4; i++) Queue((byte)(_size >> (8 * i)));
                    }
                    _phase = Phase.Done;
                }
                else if (rc != ZlibConstants.Z_OK)
                {
                    return rc == ZlibConstants.Z_BUF_ERROR ? ZLibNative.ErrorCode.BufError : ZLibNative.ErrorCode.StreamError;
                }
                progress |= Drain(stream);
            }

            if (_phase == Phase.Done && _pendingCount == 0) return ZLibNative.ErrorCode.StreamEnd;
            return progress ? ZLibNative.ErrorCode.Ok : ZLibNative.ErrorCode.BufError;
        }

        private static FlushType ToFlushType(ZLibNative.FlushCode flush) => flush switch
        {
            ZLibNative.FlushCode.SyncFlush => FlushType.Sync,
            ZLibNative.FlushCode.Finish => FlushType.Finish,
            ZLibNative.FlushCode.Block => FlushType.Partial,
            _ => FlushType.None,
        };

        private void Queue(byte b) => _pending[(_pendingStart + _pendingCount++) % _pending.Length] = b;

        private bool Drain(ZLibNative.ZStream* stream)
        {
            bool any = false;
            while (_pendingCount != 0 && stream->availOut != 0)
            {
                *(byte*)stream->nextOut = _pending[_pendingStart];
                stream->nextOut += 1;
                stream->availOut--;
                _pendingStart = (_pendingStart + 1) % _pending.Length;
                _pendingCount--;
                any = true;
            }
            return any;
        }

        // One codec call over the caller's input and output, through the two
        // arrays; the CRC and size follow the uncompressed side.
        private int RunCodec(ZLibNative.ZStream* stream, bool deflate, FlushType flush, out int consumed, out int produced)
        {
            int inCount = (int)stream->availIn;
            int outCount = (int)stream->availOut;
            if (_in.Length < inCount) _in = new byte[Math.Max(inCount, 4096)];
            if (_out.Length < outCount) _out = new byte[Math.Max(outCount, 4096)];
            if (inCount != 0) new ReadOnlySpan<byte>((void*)stream->nextIn, inCount).CopyTo(_in);

            _codec.InputBuffer = _in;
            _codec.NextIn = 0;
            _codec.AvailableBytesIn = inCount;
            _codec.OutputBuffer = _out;
            _codec.NextOut = 0;
            _codec.AvailableBytesOut = outCount;

            int rc = deflate ? _codec.Deflate(flush) : _codec.Inflate(FlushType.None);

            consumed = inCount - _codec.AvailableBytesIn;
            produced = outCount - _codec.AvailableBytesOut;
            if (produced != 0) new ReadOnlySpan<byte>(_out, 0, produced).CopyTo(new Span<byte>((void*)stream->nextOut, produced));

            if (_wrapper == Wrapper.GZip)
            {
                ReadOnlySpan<byte> plain = deflate ? new ReadOnlySpan<byte>(_in, 0, consumed) : new ReadOnlySpan<byte>(_out, 0, produced);
                _crc.Append(plain);
                _size += (uint)plain.Length;
            }

            stream->nextIn += consumed;
            stream->availIn -= (uint)consumed;
            stream->nextOut += produced;
            stream->availOut -= (uint)produced;
            return rc;
        }

        // ---- inflate --------------------------------------------------------

        public ZLibNative.ErrorCode Inflate(ZLibNative.ZStream* stream)
        {
            if (stream->availOut == 0) return ZLibNative.ErrorCode.BufError;
            bool progress = false;

            while (true)
            {
                switch (_phase)
                {
                    case Phase.Header:
                    {
                        int before = (int)stream->availIn;
                        ZLibNative.ErrorCode header = ReadGZipHeader(stream);
                        progress |= (int)stream->availIn != before;
                        if (header != ZLibNative.ErrorCode.Ok) return header;
                        if (_phase == Phase.Header) return progress ? ZLibNative.ErrorCode.Ok : ZLibNative.ErrorCode.BufError;
                        break;
                    }

                    case Phase.Body:
                    {
                        int consumed, produced, rc;
                        try
                        {
                            rc = RunCodec(stream, deflate: false, FlushType.None, out consumed, out produced);
                        }
                        catch (ZlibException)
                        {
                            return ZLibNative.ErrorCode.DataError;
                        }
                        progress |= consumed != 0 || produced != 0;
                        if (rc == ZlibConstants.Z_STREAM_END)
                        {
                            _phase = _wrapper == Wrapper.GZip ? Phase.Trailer : Phase.Done;
                            _frameCount = 0;
                            break;
                        }
                        if (rc == ZlibConstants.Z_DATA_ERROR) return ZLibNative.ErrorCode.DataError;
                        if (rc != ZlibConstants.Z_OK && rc != ZlibConstants.Z_BUF_ERROR) return ZLibNative.ErrorCode.StreamError;
                        return progress ? ZLibNative.ErrorCode.Ok : ZLibNative.ErrorCode.BufError;
                    }

                    case Phase.Trailer:
                    {
                        while (_frameCount < 8 && stream->availIn != 0)
                        {
                            _frame[_frameCount++] = *(byte*)stream->nextIn;
                            stream->nextIn += 1;
                            stream->availIn--;
                            progress = true;
                        }
                        if (_frameCount < 8) return progress ? ZLibNative.ErrorCode.Ok : ZLibNative.ErrorCode.BufError;
                        uint crc = (uint)(_frame[0] | _frame[1] << 8 | _frame[2] << 16 | _frame[3] << 24);
                        uint size = (uint)(_frame[4] | _frame[5] << 8 | _frame[6] << 16 | _frame[7] << 24);
                        if (crc != _crc.GetCurrentHashAsUInt32() || size != _size) return ZLibNative.ErrorCode.DataError;
                        _phase = Phase.Done;
                        break;
                    }

                    default:
                        return ZLibNative.ErrorCode.StreamEnd;
                }
            }
        }

        // RFC 1952 header, a byte at a time as input allows: the fixed ten,
        // then FEXTRA, FNAME, FCOMMENT and FHCRC as the flags ask. For
        // windowBits 40..47 the first byte decides between gzip and zlib.
        private ZLibNative.ErrorCode ReadGZipHeader(ZLibNative.ZStream* stream)
        {
            while (stream->availIn != 0)
            {
                byte b = *(byte*)stream->nextIn;

                if (_wrapper == Wrapper.Detect)
                {
                    if (b != ZLibNative.GZip_Header_ID1)
                    {
                        _wrapper = Wrapper.ZLib;
                        if (_codec.InitializeInflate(15, true) != ZlibConstants.Z_OK) return ZLibNative.ErrorCode.StreamError;
                        _phase = Phase.Body;
                        return ZLibNative.ErrorCode.Ok;
                    }
                    _wrapper = Wrapper.GZip;
                }

                stream->nextIn += 1;
                stream->availIn--;

                switch (_headerStep)
                {
                    case 0: // the fixed ten bytes
                        _frame[_frameCount++] = b;
                        if (_frameCount < 10) continue;
                        if (_frame[0] != ZLibNative.GZip_Header_ID1 || _frame[1] != ZLibNative.GZip_Header_ID2 || _frame[2] != 8)
                            return ZLibNative.ErrorCode.DataError;
                        _flags = _frame[3];
                        _frameCount = 0;
                        _headerStep = 1;
                        break;
                    case 1: // FEXTRA length, low byte
                        _extraLeft = b;
                        _headerStep = 2;
                        continue;
                    case 2: // FEXTRA length, high byte
                        _extraLeft |= b << 8;
                        _headerStep = 3;
                        break;
                    case 3: // FEXTRA data
                        _extraLeft--;
                        break;
                    case 4: // FNAME, to its zero
                        if (b == 0) _flags &= ~GZipFlagName;
                        break;
                    case 5: // FCOMMENT, to its zero
                        if (b == 0) _flags &= ~GZipFlagComment;
                        break;
                    case 6: // FHCRC, two bytes
                        _extraLeft--;
                        break;
                }

                if (!AdvanceHeader())
                    continue;

                if (_codec.InitializeInflate(15, false) != ZlibConstants.Z_OK) return ZLibNative.ErrorCode.StreamError;
                _phase = Phase.Body;
                return ZLibNative.ErrorCode.Ok;
            }
            return ZLibNative.ErrorCode.Ok;
        }

        // Moves past the optional parts the flags no longer ask for; true when
        // the header is complete.
        private bool AdvanceHeader()
        {
            if (_headerStep == 1 && (_flags & GZipFlagExtra) == 0) _headerStep = 4;
            if (_headerStep == 3 && _extraLeft == 0) { _flags &= ~GZipFlagExtra; _headerStep = 4; }
            if (_headerStep == 4 && (_flags & GZipFlagName) == 0) _headerStep = 5;
            if (_headerStep == 5 && (_flags & GZipFlagComment) == 0) { _headerStep = 6; _extraLeft = 2; }
            if (_headerStep == 6 && ((_flags & GZipFlagHeaderCrc) == 0 || _extraLeft == 0)) _headerStep = 7;
            return _headerStep == 7;
        }
    }
}
