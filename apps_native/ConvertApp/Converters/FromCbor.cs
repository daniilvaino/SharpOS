using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO;
using SharpOS.Std.NoRuntime;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --from cbor: bytes of CBOR into Expando messages (step206).
    //
    // A message is each element of a top-level array (of definite or
    // indefinite length), or each item of a CBOR Sequence — which of the two,
    // the first byte says; the self-described tag (55799) in front is skipped.
    // --from cborseq (FromCborseq) takes the sequence only. A message is a map;
    // anything else is an error.
    //
    // A map is an Expando (a number as a key becomes its text), an array
    // object[], a byte string byte[], a text string string, an integer long
    // (ulong above long's range, double below it), a float float, a half or a
    // double double, true/false bool, null and undefined nothing. Tags: 0 and
    // 1 a DateTime (a text that does not parse stays text), 37 a Guid; any
    // other tag gives its value.
    //
    // The parse streams: an element is decoded from what has arrived; when it
    // is cut by the end of the bytes so far, more is read and it is decoded
    // again — a file of any size passes in the memory of its largest element.
    // An error names the element and ends the program with 1.
    internal class FromCbor : IConverter
    {
        public string Direction => "from";
        public virtual string Format => "cbor";
        public string Options => "";

        protected virtual bool SequenceOnly => false;

        private const int Piece = 64 * 1024;
        private const int MaxDepth = 64;

        private Stream _input;
        private PipeWriter<Expando> _output;
        private byte[] _buffer = new byte[Piece];
        private int _start, _end;
        private bool _final;
        private long _elements;

        public int Run(string[] options)
        {
            if (options.Length > 0)
            {
                Console.Error.WriteLine("CONVERT --from " + Format + ": unknown option " + options[0]);
                return 2;
            }
            using (_input = Pipe.ReadBytes())
            using (_output = Pipe.Write<Expando>())
            {
                try
                {
                    Parse();
                }
                catch (FormatException e)
                {
                    Console.Error.WriteLine("CONVERT --from " + Format + ": " + e.Message);
                    return 1;
                }
            }
            return 0;
        }

        private void Parse()
        {
            if (Need(3) && _buffer[_start] == 0xD9 && _buffer[_start + 1] == 0xD9 && _buffer[_start + 2] == 0xF7)
                _start += 3;

            int mode = 0;          // 0 a sequence, 1 an array of indefinite length, 2 of definite length
            ulong remaining = 0;
            if (!SequenceOnly && Need(1))
            {
                byte first = _buffer[_start];
                if (first == 0x9F)
                {
                    mode = 1;
                    _start++;
                }
                else if ((first & 0xE0) == 0x80)
                {
                    mode = 2;
                    remaining = ArrayLength(first);
                }
            }

            while (true)
            {
                if (mode == 1)
                {
                    if (!Need(1)) throw new FormatException("the input ended inside the top-level array");
                    if (_buffer[_start] == 0xFF)
                    {
                        _start++;
                        break;
                    }
                }
                else if (mode == 2)
                {
                    if (remaining == 0) break;
                    if (!Need(1)) throw new FormatException("the input ended inside the top-level array");
                    remaining--;
                }
                else if (!Need(1)) break;
                Element();
            }
            if (mode != 0 && Need(1))
                throw new FormatException("a value after the top-level array");
        }

        // A definite array's head: the count, big-endian after the first byte.
        private ulong ArrayLength(byte first)
        {
            int info = first & 0x1F;
            int size = info < 24 ? 0 : info == 24 ? 1 : info == 25 ? 2 : info == 26 ? 4 : info == 27 ? 8 : -1;
            if (size < 0) throw new FormatException("the top-level array's length is malformed");
            if (!Need(1 + size)) throw new FormatException("the input ended inside the top-level array's length");
            ulong count = size == 0 ? (ulong)info : 0;
            for (int i = 0; i < size; i++) count = (count << 8) | _buffer[_start + 1 + i];
            _start += 1 + size;
            return count;
        }

        private void Element()
        {
            while (true)
            {
                int available = _end - _start;
                var reader = new CborReader(new ReadOnlyMemory<byte>(_buffer, _start, available),
                                            CborConformanceMode.Lax, allowMultipleRootLevelValues: true);
                object value;
                try
                {
                    value = Read(reader, 0);
                }
                catch (CborContentException e) when (!_final && e.Message == System.Formats.Cbor.SR.Cbor_Reader_InvalidCbor_UnexpectedEndOfBuffer)
                {
                    More(true);
                    continue;
                }
                catch (CborContentException e)
                {
                    throw Bad(e.Message);
                }
                catch (InvalidOperationException e)
                {
                    throw Bad(e.Message);
                }
                _start += available - reader.BytesRemaining;
                if (value is not Expando message) throw Bad("a message is a map");
                _elements++;
                _output.Copy(message);
                return;
            }
        }

        private FormatException Bad(string why)
            => new FormatException("element " + (_elements + 1).ToString() + ": " + why);

        private object Read(CborReader reader, int depth)
        {
            if (depth > MaxDepth) throw Bad("nested deeper than " + MaxDepth.ToString());
            CborReaderState state = reader.PeekState();
            switch (state)
            {
                case CborReaderState.UnsignedInteger:
                {
                    ulong u = reader.ReadUInt64();
                    return u <= long.MaxValue ? (object)(long)u : u;
                }
                case CborReaderState.NegativeInteger:
                {
                    ulong r = reader.ReadCborNegativeIntegerRepresentation();
                    return r <= long.MaxValue ? (object)(-1L - (long)r) : -1.0 - r;
                }
                case CborReaderState.ByteString:
                case CborReaderState.StartIndefiniteLengthByteString:
                    return reader.ReadByteString();
                case CborReaderState.TextString:
                case CborReaderState.StartIndefiniteLengthTextString:
                    return reader.ReadTextString();
                case CborReaderState.StartArray:
                {
                    reader.ReadStartArray();
                    var list = new List<object>();
                    while (reader.PeekState() != CborReaderState.EndArray)
                        list.Add(Read(reader, depth + 1));
                    reader.ReadEndArray();
                    return list.ToArray();
                }
                case CborReaderState.StartMap:
                {
                    reader.ReadStartMap();
                    var map = new Expando();
                    while (reader.PeekState() != CborReaderState.EndMap)
                    {
                        string key = Key(reader);
                        map[key] = Read(reader, depth + 1);
                    }
                    reader.ReadEndMap();
                    return map;
                }
                case CborReaderState.Tag:
                    return Tagged(reader, depth);
                case CborReaderState.SinglePrecisionFloat:
                    return reader.ReadSingle();
                case CborReaderState.HalfPrecisionFloat:
                case CborReaderState.DoublePrecisionFloat:
                    return reader.ReadDouble();
                case CborReaderState.Boolean:
                    return BoolBox.Of(reader.ReadBoolean());
                case CborReaderState.Null:
                    reader.ReadNull();
                    return null;
                case CborReaderState.SimpleValue:
                    reader.ReadSimpleValue();
                    return null;
                default:
                    throw Bad("unexpected " + state.ToString());
            }
        }

        private string Key(CborReader reader)
        {
            switch (reader.PeekState())
            {
                case CborReaderState.TextString:
                case CborReaderState.StartIndefiniteLengthTextString:
                    return reader.ReadTextString();
                case CborReaderState.UnsignedInteger:
                    return reader.ReadUInt64().ToString();
                case CborReaderState.NegativeInteger:
                    return reader.ReadInt64().ToString();
                default:
                    throw Bad("a map key that is neither text nor a number");
            }
        }

        private object Tagged(CborReader reader, int depth)
        {
            ulong tag = (ulong)reader.ReadTag();
            CborReaderState state = reader.PeekState();
            if (tag == 0 && state == CborReaderState.TextString)
            {
                string text = reader.ReadTextString();
                return DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime date) ? date : (object)text;
            }
            if (tag == 1 && (state == CborReaderState.UnsignedInteger || state == CborReaderState.NegativeInteger))
                return new DateTime(UnixEpochTicks + reader.ReadInt64() * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            if (tag == 1 && (state == CborReaderState.HalfPrecisionFloat || state == CborReaderState.SinglePrecisionFloat
                             || state == CborReaderState.DoublePrecisionFloat))
                return new DateTime(UnixEpochTicks + (long)(reader.ReadDouble() * TimeSpan.TicksPerSecond), DateTimeKind.Utc);
            if (tag == 37 && state == CborReaderState.ByteString)
            {
                byte[] u = reader.ReadByteString();
                if (u.Length == 16)
                    return new Guid((u[0] << 24) | (u[1] << 16) | (u[2] << 8) | u[3],
                                    (short)((u[4] << 8) | u[5]), (short)((u[6] << 8) | u[7]),
                                    u[8], u[9], u[10], u[11], u[12], u[13], u[14], u[15]);
                return u;
            }
            // A tag whose meaning is not known here: its value.
            return Read(reader, depth + 1);
        }

        // 1970-01-01T00:00:00Z.
        private const long UnixEpochTicks = 621355968000000000L;

        // At least n bytes from _start, unless the input ends first.
        private bool Need(int n)
        {
            while (_end - _start < n && !_final) More(false);
            return _end - _start >= n;
        }

        // Keeps what is not consumed, then reads behind it: one piece, or — for
        // an element cut by the end of what came — until the buffer is full,
        // which grows when the element fills it, so a big element is decoded
        // a few times rather than once per piece.
        private void More(bool fill)
        {
            if (_start > 0)
            {
                Array.Copy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length)
            {
                var bigger = new byte[_buffer.Length * 2];
                Array.Copy(_buffer, bigger, _end);
                _buffer = bigger;
            }
            while (_end < _buffer.Length)
            {
                int n = _input.Read(_buffer, _end, _buffer.Length - _end);
                if (n == 0)
                {
                    _final = true;
                    return;
                }
                _end += n;
                if (!fill) return;
            }
        }
    }
}
