using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --to cbor: any objects, read through views, into CBOR as byte[]
    // messages (step206). One document: the self-described tag (55799, so the
    // file says what it is in its first bytes), an array of indefinite length,
    // a map per message, the break — written as the messages come, a message
    // per element. --to cborseq (ToCborseq): a CBOR Sequence (RFC 8742), the
    // maps one after another and nothing else.
    //
    // Unlike JSON, CBOR keeps what JSON turns into strings: byte[] is a byte
    // string, DateTime a date (tag 0, "O"), Guid a UUID (tag 37), float and
    // double their own widths, NaN and infinities as they are. An enum is its
    // member's name, TimeSpan a string ("c"). A cycle is an error naming the
    // type and the field (exit code 1).
    internal unsafe class ToCbor : IConverter
    {
        public string Direction => "to";
        public virtual string Format => "cbor";
        public string Options => "";

        protected virtual bool Sequence => false;

        private readonly CborWriter _cbor = new CborWriter(CborConformanceMode.Lax);
        private readonly HashSet<ulong> _path = new HashSet<ulong>();

        public int Run(string[] options)
        {
            if (options.Length > 0)
            {
                Console.Error.WriteLine("CONVERT --to " + Format + ": unknown option " + options[0]);
                return 2;
            }
            using PipeWriter<byte[]> output = Pipe.Write<byte[]>();
            if (!Sequence) output.Copy(new byte[] { 0xD9, 0xD9, 0xF7, 0x9F });
            try
            {
                foreach (View v in Pipe.Read())
                {
                    _cbor.Reset();
                    _path.Clear();
                    Value(v, v.TypeName ?? "message", "(root)");
                    output.Copy(_cbor.Encode());
                }
            }
            catch (InvalidOperationException e) when (e is not ObjectDisposedException)
            {
                Console.Error.WriteLine("CONVERT --to " + Format + ": " + e.Message);
                return 1;
            }
            if (!Sequence) output.Copy(new byte[] { 0xFF });
            return 0;
        }

        private void Value(View v, string owner, string field)
        {
            switch (v.Kind)
            {
                case ViewKind.Null:
                    _cbor.WriteNull();
                    return;
                case ViewKind.Bool:
                    _cbor.WriteBoolean((bool)v);
                    return;
                case ViewKind.Char:
                    _cbor.WriteTextString(((char)v).ToString());
                    return;
                case ViewKind.String:
                    _cbor.WriteTextString((string)v);
                    return;
                case ViewKind.Integer:
                {
                    TypeShape e = v.EnumShape;
                    string member = e?.EnumName(v.IntegerValue);
                    if (member != null) _cbor.WriteTextString(member);
                    else if (v.ValueKind == FieldKind.UInt64) _cbor.WriteUInt64((ulong)v.IntegerValue);
                    else _cbor.WriteInt64(v.IntegerValue);
                    return;
                }
                case ViewKind.Float:
                    if (v.ValueKind == FieldKind.Single) _cbor.WriteSingle((float)v.FloatValue);
                    else _cbor.WriteDouble(v.FloatValue);
                    return;
                case ViewKind.Array:
                    Array(v, owner, field);
                    return;
                default:
                    Object(v, owner, field);
                    return;
            }
        }

        private void Array(View v, string owner, string field)
        {
            if (v.TryGetBytes(out byte* bytes, out int count))
            {
                _cbor.WriteByteString(new ReadOnlySpan<byte>(bytes, count));
                return;
            }
            Enter(v, owner, field);
            int length = v.Length;
            _cbor.WriteStartArray(length);
            for (int i = 0; i < length; i++)
                Value(v[i], owner, field + "[" + i.ToString() + "]");
            _cbor.WriteEndArray();
            Leave(v);
        }

        private void Object(View v, string owner, string field)
        {
            string type = v.TypeName;
            byte* at = (byte*)v.Address + 8;
            if (type == "System.DateTime")
            {
                _cbor.WriteTag(CborTag.DateTimeString);
                _cbor.WriteTextString((*(DateTime*)at).ToString("O"));
                return;
            }
            if (type == "System.TimeSpan") { _cbor.WriteTextString((*(TimeSpan*)at).ToString()); return; }
            if (type == "System.Guid")
            {
                // RFC 4122 order: the first three fields big-endian.
                byte* uuid = stackalloc byte[16];
                uint a = *(uint*)at;
                ushort b = *(ushort*)(at + 4), c = *(ushort*)(at + 6);
                uuid[0] = (byte)(a >> 24); uuid[1] = (byte)(a >> 16); uuid[2] = (byte)(a >> 8); uuid[3] = (byte)a;
                uuid[4] = (byte)(b >> 8); uuid[5] = (byte)b;
                uuid[6] = (byte)(c >> 8); uuid[7] = (byte)c;
                for (int i = 8; i < 16; i++) uuid[i] = at[i];
                _cbor.WriteTag((CborTag)37);
                _cbor.WriteByteString(new ReadOnlySpan<byte>(uuid, 16));
                return;
            }

            Enter(v, owner, field);
            string name = v.IsExpandoObject ? "Expando" : type;
            int fields = 0;
            foreach (ViewField f in v.Fields) fields++;
            _cbor.WriteStartMap(fields);
            foreach (ViewField f in v.Fields)
            {
                _cbor.WriteTextString(f.Name);
                Value(f.Value, name, f.Name);
            }
            _cbor.WriteEndMap();
            Leave(v);
        }

        // An object met again on its own path: a cycle a tree cannot hold.
        private void Enter(View v, string owner, string field)
        {
            if (v.IsInPlaceStruct) return;
            if (!_path.Add(v.Address))
                throw new InvalidOperationException(owner + "." + field + ": a cycle — " + v.TypeName + " refers back to itself");
        }

        private void Leave(View v)
        {
            if (!v.IsInPlaceStruct) _path.Remove(v.Address);
        }
    }
}
