using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --to json [--lines]: any objects, read through views, into JSON text as
    // string messages (step197). Without --lines the lines make one JSON
    // array, an object per line:
    //
    //     [
    //     {"Level":3,"Text":"..."},
    //     {"Level":4,"Text":"..."}
    //     ]
    //
    // with --lines, JSON Lines: an object per line and nothing else. An enum
    // is its member's name; DateTime ("O"), TimeSpan ("c") and Guid ("D") are
    // strings; byte[] is base64. A cycle, NaN or an infinity is an error naming
    // the type and the field (exit code 1).
    internal sealed unsafe class ToJson : IConverter
    {
        public string Direction => "to";
        public string Format => "json";
        public string Options => "[--lines]";

        private readonly ArrayBufferWriter<byte> _buffer = new ArrayBufferWriter<byte>(4096);
        private Utf8JsonWriter _json;
        private readonly HashSet<ulong> _path = new HashSet<ulong>();

        public int Run(string[] options)
        {
            bool lines = options.Length > 0 && options[0] == "--lines";
            if (options.Length > (lines ? 1 : 0))
            {
                Console.WriteLine("CONVERT --to json: unknown option " + options[lines ? 1 : 0] + "; known: --lines");
                return 2;
            }
            _json = new Utf8JsonWriter(_buffer);

            using PipeWriter<string> output = Pipe.Write<string>();
            if (!lines) output.Copy("[");
            string held = null;     // the previous object: its comma waits for the next
            try
            {
                foreach (View v in Pipe.Read())
                {
                    string text = Serialize(v);
                    if (lines)
                    {
                        output.Copy(text);
                        continue;
                    }
                    if (held != null) output.Copy(held + ",");
                    held = text;
                }
            }
            catch (InvalidOperationException e) when (e is not ObjectDisposedException)
            {
                Console.WriteLine("CONVERT --to json: " + e.Message);
                return 1;
            }
            if (!lines)
            {
                if (held != null) output.Copy(held);
                output.Copy("]");
            }
            return 0;
        }

        private string Serialize(View v)
        {
            _buffer.Clear();
            _json.Reset(_buffer);
            _path.Clear();
            Value(v, v.TypeName ?? "message", "(root)");
            _json.Flush();
            return Encoding.UTF8.GetString(_buffer.WrittenSpan);
        }

        private void Value(View v, string owner, string field)
        {
            switch (v.Kind)
            {
                case ViewKind.Null:
                    _json.WriteNullValue();
                    return;
                case ViewKind.Bool:
                    _json.WriteBooleanValue((bool)v);
                    return;
                case ViewKind.Char:
                    _json.WriteStringValue(((char)v).ToString());
                    return;
                case ViewKind.String:
                    _json.WriteStringValue((string)v);
                    return;
                case ViewKind.Integer:
                {
                    TypeShape e = v.EnumShape;
                    string member = e?.EnumName(v.IntegerValue);
                    if (member != null) _json.WriteStringValue(member);
                    else if (v.ValueKind == FieldKind.UInt64) _json.WriteNumberValue((ulong)v.IntegerValue);
                    else _json.WriteNumberValue(v.IntegerValue);
                    return;
                }
                case ViewKind.Float:
                {
                    double d = v.FloatValue;
                    if (double.IsNaN(d) || double.IsInfinity(d))
                        throw new InvalidOperationException(owner + "." + field + ": " + (double.IsNaN(d) ? "NaN" : "an infinity") + " has no JSON form");
                    if (v.ValueKind == FieldKind.Single) _json.WriteNumberValue((float)d);
                    else _json.WriteNumberValue(d);
                    return;
                }
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
                _json.WriteBase64StringValue(new ReadOnlySpan<byte>(bytes, count));
                return;
            }
            Enter(v, owner, field);
            _json.WriteStartArray();
            int length = v.Length;
            for (int i = 0; i < length; i++)
                Value(v[i], owner, field + "[" + i.ToString() + "]");
            _json.WriteEndArray();
            Leave(v);
        }

        private void Object(View v, string owner, string field)
        {
            // The structs that are values in JSON: strings of their usual forms.
            string type = v.TypeName;
            if (type == "System.DateTime") { _json.WriteStringValue((*(DateTime*)(v.Address + 8)).ToString("O")); return; }
            if (type == "System.TimeSpan") { _json.WriteStringValue((*(TimeSpan*)(v.Address + 8)).ToString()); return; }
            if (type == "System.Guid") { _json.WriteStringValue((*(Guid*)(v.Address + 8)).ToString()); return; }

            Enter(v, owner, field);
            string name = v.IsExpandoObject ? "Expando" : type;
            _json.WriteStartObject();
            foreach (ViewField f in v.Fields)
            {
                _json.WritePropertyName(f.Name);
                Value(f.Value, name, f.Name);
            }
            _json.WriteEndObject();
            Leave(v);
        }

        // An object met again on its own path: a cycle JSON cannot hold.
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
