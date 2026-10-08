// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// System.IO.StreamReader — a UTF-8 TextReader over a Stream.
//
// Shaped after dotnet/runtime
//   src/libraries/System.Private.CoreLib/src/System/IO/StreamReader.cs
// (field names _stream/_byteBuffer/_byteLen/_bytePos/_charBuffer/_charLen/
// _charPos, ReadBuffer, the Peek/Read/ReadLine/ReadToEnd loops). The
// original's decoding goes through Encoding.GetDecoder(); this std's Encoding
// has no streaming Decoder, so the byte -> char step is a local incremental
// UTF-8 decoder (DecodeByte below) that keeps its state across buffer
// refills. It follows the WHATWG / Unicode "maximal subpart" rule, which is
// what .NET's UTF8Encoding replacement fallback does: each ill-formed subpart
// becomes one U+FFFD, a truncated sequence at end of stream becomes one
// U+FFFD, overlongs / surrogates / > U+10FFFF are rejected at the first byte
// that makes them so. Supplementary code points come out as surrogate pairs.
//
// Cuts vs original:
//   - Encoding selection: always UTF-8. Constructors taking Encoding /
//     detectEncodingFromByteOrderMarks / bufferSize / leaveOpen, and the
//     path/FileStream constructors, are absent; only StreamReader(Stream).
//   - BOM handling: a UTF-8 BOM (EF BB BF) at the very start is skipped, also
//     when split across reads (implemented as "drop U+FEFF if it is the first
//     decoded char"). UTF-16/UTF-32 BOM detection is not done.
//   - Async surface, DiscardBufferedData, NullStreamReader (TextReader.Null
//     is a plain NullTextReader), CurrentEncoding returns a fresh UTF8Encoding.
//   - ReadLine follows the TextReader contract: '\n', "\r\n" and a lone '\r'
//     end a line. (The earlier Latin-1 reader here dropped lone '\r'.)

using System.Text;

namespace System.IO
{
    public class StreamReader : TextReader
    {
        private const int DefaultBufferSize = 1024;

        private Stream? _stream;
        private readonly byte[] _byteBuffer;
        private int _byteLen;
        private int _bytePos;

        // Each byte yields at most two chars (U+FFFD for a broken prefix plus
        // the byte itself re-read, or a surrogate pair for a 4-byte sequence
        // completed by that byte), so twice the byte buffer never overflows.
        private readonly char[] _charBuffer;
        private int _charLen;
        private int _charPos;

        // Incremental UTF-8 decoder state (WHATWG "UTF-8 decoder").
        private int _codePoint;
        private int _bytesNeeded;
        private int _bytesSeen;
        private int _lowerBoundary = 0x80;
        private int _upperBoundary = 0xBF;

        // True until the first char has been decoded; a leading U+FEFF is the BOM.
        private bool _checkPreamble = true;

        public StreamReader(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Stream was not readable.", nameof(stream));
            _stream = stream;
            _byteBuffer = new byte[DefaultBufferSize];
            _charBuffer = new char[DefaultBufferSize * 2];
        }

        public virtual Stream BaseStream => _stream!;

        public virtual Encoding CurrentEncoding => new UTF8Encoding();

        public bool EndOfStream
        {
            get
            {
                ThrowIfDisposed();
                if (_charPos < _charLen)
                    return false;
                return ReadBuffer() == 0;
            }
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && _stream != null)
                    _stream.Dispose();
            }
            finally
            {
                _stream = null;
                _charPos = 0;
                _charLen = 0;
                base.Dispose(disposing);
            }
        }

        public override int Peek()
        {
            ThrowIfDisposed();

            if (_charPos == _charLen && ReadBuffer() == 0)
                return -1;
            return _charBuffer[_charPos];
        }

        public override int Read()
        {
            ThrowIfDisposed();

            if (_charPos == _charLen && ReadBuffer() == 0)
                return -1;
            return _charBuffer[_charPos++];
        }

        public override int Read(char[] buffer, int index, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer, nameof(buffer));
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index), "Non-negative number required.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
            if (buffer.Length - index < count)
                throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
            ThrowIfDisposed();

            int charsRead = 0;
            while (count > 0)
            {
                int n = _charLen - _charPos;
                if (n == 0)
                    n = ReadBuffer();
                if (n == 0)
                    break; // end of stream
                if (n > count)
                    n = count;
                Array.Copy(_charBuffer, _charPos, buffer, index + charsRead, n);
                _charPos += n;
                charsRead += n;
                count -= n;

                // Upstream's _isBlocked: the stream handed back less than a
                // full buffer, so another Read could wait (pipe, console).
                // Return what we have instead.
                if (_byteLen < _byteBuffer.Length)
                    break;
            }
            return charsRead;
        }

        public override string ReadToEnd()
        {
            ThrowIfDisposed();

            var sb = new StringBuilder(_charLen - _charPos);
            do
            {
                sb.Append(_charBuffer, _charPos, _charLen - _charPos);
                _charPos = _charLen; // we consumed these characters
                ReadBuffer();
            } while (_charLen > 0);
            return sb.ToString();
        }

        // Returns the next line without its terminator ('\n', "\r\n" or '\r'),
        // or null at end of stream. A trailing line without a terminator is
        // returned as is.
        public override string? ReadLine()
        {
            ThrowIfDisposed();

            if (_charPos == _charLen && ReadBuffer() == 0)
                return null;

            StringBuilder? sb = null;
            do
            {
                int i = _charPos;
                do
                {
                    char ch = _charBuffer[i];
                    if (ch == '\r' || ch == '\n')
                    {
                        string s;
                        if (sb != null)
                        {
                            sb.Append(_charBuffer, _charPos, i - _charPos);
                            s = sb.ToString();
                        }
                        else
                        {
                            s = new string(_charBuffer, _charPos, i - _charPos);
                        }
                        _charPos = i + 1;
                        if (ch == '\r' && (_charPos < _charLen || ReadBuffer() > 0))
                        {
                            if (_charBuffer[_charPos] == '\n')
                                _charPos++;
                        }
                        return s;
                    }
                    i++;
                } while (i < _charLen);

                i = _charLen - _charPos;
                sb ??= new StringBuilder(i + 80);
                sb.Append(_charBuffer, _charPos, i);
            } while (ReadBuffer() > 0);

            return sb.ToString();
        }

        // Refills _charBuffer from the stream. Returns the number of chars now
        // available; 0 only at end of stream. Keeps reading while the bytes
        // read so far only form an incomplete sequence.
        internal virtual int ReadBuffer()
        {
            _charLen = 0;
            _charPos = 0;

            while (_charLen == 0)
            {
                _byteLen = _stream!.Read(_byteBuffer, 0, _byteBuffer.Length);
                _bytePos = 0;
                if (_byteLen == 0)
                {
                    // End of stream: a sequence cut short is one U+FFFD.
                    if (_bytesNeeded != 0)
                    {
                        ResetDecoder();
                        EmitChar('�');
                    }
                    return _charLen;
                }

                while (_bytePos < _byteLen)
                    DecodeByte(_byteBuffer[_bytePos++]);
            }
            return _charLen;
        }

        private void DecodeByte(byte b)
        {
            while (true)
            {
                if (_bytesNeeded == 0)
                {
                    if (b <= 0x7F)
                    {
                        EmitChar((char)b);
                    }
                    else if (b >= 0xC2 && b <= 0xDF)
                    {
                        _bytesNeeded = 1;
                        _codePoint = b & 0x1F;
                    }
                    else if (b >= 0xE0 && b <= 0xEF)
                    {
                        if (b == 0xE0) _lowerBoundary = 0xA0;      // no overlongs
                        else if (b == 0xED) _upperBoundary = 0x9F; // no surrogates
                        _bytesNeeded = 2;
                        _codePoint = b & 0x0F;
                    }
                    else if (b >= 0xF0 && b <= 0xF4)
                    {
                        if (b == 0xF0) _lowerBoundary = 0x90;      // no overlongs
                        else if (b == 0xF4) _upperBoundary = 0x8F; // <= U+10FFFF
                        _bytesNeeded = 3;
                        _codePoint = b & 0x07;
                    }
                    else
                    {
                        // 0x80..0xC1, 0xF5..0xFF: never valid as a lead byte.
                        EmitChar('�');
                    }
                    return;
                }

                if (b < _lowerBoundary || b > _upperBoundary)
                {
                    // The sequence so far is a maximal subpart: one U+FFFD,
                    // then this byte starts over as a possible lead byte.
                    ResetDecoder();
                    EmitChar('�');
                    continue;
                }

                _lowerBoundary = 0x80;
                _upperBoundary = 0xBF;
                _codePoint = (_codePoint << 6) | (b & 0x3F);
                _bytesSeen++;
                if (_bytesSeen != _bytesNeeded)
                    return;

                int cp = _codePoint;
                ResetDecoder();
                if (cp <= 0xFFFF)
                {
                    EmitChar((char)cp);
                }
                else
                {
                    cp -= 0x10000;
                    EmitChar((char)(0xD800 + (cp >> 10)));
                    EmitChar((char)(0xDC00 + (cp & 0x3FF)));
                }
                return;
            }
        }

        private void ResetDecoder()
        {
            _codePoint = 0;
            _bytesNeeded = 0;
            _bytesSeen = 0;
            _lowerBoundary = 0x80;
            _upperBoundary = 0xBF;
        }

        private void EmitChar(char c)
        {
            if (_checkPreamble)
            {
                _checkPreamble = false;
                if (c == '﻿')
                    return; // UTF-8 BOM
            }
            _charBuffer[_charLen++] = c;
        }

        private void ThrowIfDisposed()
        {
            if (_stream == null)
                throw new ObjectDisposedException(null, "Cannot read from a closed TextReader.");
        }
    }
}
