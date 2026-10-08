using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SharpOS.Std.NoRuntime;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // --from json: bytes (or text) of JSON into Expando messages (step197).
    //
    // A message is each element of a top-level array, or each value of a
    // sequence of them (JSON Lines, or objects one after another). An object
    // is an Expando, an array object[], a string string, an integer long
    // (double when it does not fit), a fraction double, true/false bool,
    // null nothing. An element that is not an object is an error.
    //
    // The parse streams: the input is read in 64 KiB pieces, the reader's
    // state carried from one to the next (Utf8JsonReader, isFinalBlock), and
    // only the element being built is held — a file of any size passes in the
    // memory of its largest element. An error says the line and column and
    // ends the program with 1.
    internal sealed class FromJson : IConverter
    {
        public string Direction => "from";
        public string Format => "json";
        public string Options => "";

        private const int Piece = 64 * 1024;

        private PipeWriter<Expando> _output;
        private int _arrayMode = -1;   // -1 not known yet, 1 a top-level array, 0 a sequence of values
        private long _elements;

        // What is being built: an Expando with the name of its next entry, or a list.
        private sealed class Frame
        {
            public Expando Object;
            public List<object> List;
            public string Name;
        }

        private readonly List<Frame> _stack = new List<Frame>();

        public int Run(string[] options)
        {
            using Stream input = Pipe.ReadBytes();
            using (_output = Pipe.Write<Expando>())
            {
                try
                {
                    Parse(input);
                }
                catch (JsonException e)
                {
                    Console.WriteLine("CONVERT --from json: line " + ((e.LineNumber ?? 0) + 1).ToString()
                                      + ", column " + ((e.BytePositionInLine ?? 0) + 1).ToString() + ": " + Plain(e.Message));
                    return 1;
                }
                catch (FormatException e)
                {
                    Console.WriteLine("CONVERT --from json: " + e.Message);
                    return 1;
                }
            }
            return 0;
        }

        // STJ's messages end with "LineNumber: n | BytePositionInLine: m."; the line says it already.
        private static string Plain(string message)
        {
            int cut = message.IndexOf(" LineNumber:");
            return cut > 0 ? message.Substring(0, cut) : message;
        }

        private void Parse(Stream input)
        {
            byte[] buffer = new byte[Piece];
            int start = 0, end = 0;
            bool final = false;
            var state = new JsonReaderState(new JsonReaderOptions { AllowMultipleValues = true, MaxDepth = 64 });
            while (true)
            {
                // Keep what is not consumed, then read more behind it.
                if (start > 0)
                {
                    Array.Copy(buffer, start, buffer, 0, end - start);
                    end -= start;
                    start = 0;
                }
                if (!final)
                {
                    if (end == buffer.Length)
                    {
                        // A token longer than the buffer: the buffer grows.
                        var bigger = new byte[buffer.Length * 2];
                        Array.Copy(buffer, bigger, end);
                        buffer = bigger;
                    }
                    int n = input.Read(buffer, end, buffer.Length - end);
                    if (n == 0) final = true;
                    end += n;
                }

                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, start, end - start), final, state);
                while (reader.Read()) Token(ref reader);
                start += (int)reader.BytesConsumed;
                state = reader.CurrentState;
                if (final) break;
            }
            if (_stack.Count != 0 || _arrayMode == 1)
                throw new FormatException("the input ended inside " + (_stack.Count != 0 ? "an element" : "the top-level array"));
        }

        private void Token(ref Utf8JsonReader reader)
        {
            JsonTokenType token = reader.TokenType;
            if (_arrayMode < 0)
            {
                _arrayMode = token == JsonTokenType.StartArray ? 1 : 0;
                if (_arrayMode == 1) return;
            }
            if (_stack.Count == 0)
            {
                if (_arrayMode == 1 && token == JsonTokenType.EndArray)
                {
                    _arrayMode = 2;    // the array closed; nothing may follow
                    return;
                }
                if (_arrayMode == 2)
                    throw NotObject(ref reader, "a value after the top-level array");
                if (token != JsonTokenType.StartObject)
                    throw NotObject(ref reader, "element " + (_elements + 1).ToString() + " is not an object");
            }

            switch (token)
            {
                case JsonTokenType.StartObject:
                    _stack.Add(new Frame { Object = new Expando() });
                    return;
                case JsonTokenType.StartArray:
                    _stack.Add(new Frame { List = new List<object>() });
                    return;
                case JsonTokenType.PropertyName:
                    _stack[_stack.Count - 1].Name = reader.GetString();
                    return;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                {
                    Frame done = _stack[_stack.Count - 1];
                    _stack.RemoveAt(_stack.Count - 1);
                    if (_stack.Count == 0)
                    {
                        _elements++;
                        _output.Copy(done.Object);
                        return;
                    }
                    Add(done.Object != null ? done.Object : (object)done.List.ToArray());
                    return;
                }
                case JsonTokenType.String:
                    Add(reader.GetString());
                    return;
                case JsonTokenType.Number:
                    Add(reader.TryGetInt64(out long whole) ? (object)whole : reader.GetDouble());
                    return;
                case JsonTokenType.True:
                    Add(BoolBox.Of(true));
                    return;
                case JsonTokenType.False:
                    Add(BoolBox.Of(false));
                    return;
                case JsonTokenType.Null:
                    Add(null);
                    return;
            }
        }

        private void Add(object value)
        {
            Frame into = _stack[_stack.Count - 1];
            if (into.Object != null) into.Object[into.Name] = value;
            else into.List.Add(value);
        }

        private static JsonException NotObject(ref Utf8JsonReader reader, string what)
        {
            JsonReaderState at = reader.CurrentState;
            return new JsonException(what + ": a message is an object", null, at._lineNumber, at._bytePositionInLine);
        }
    }
}
