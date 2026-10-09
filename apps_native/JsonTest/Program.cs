using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime;
using System.Text;
using System.Text.Json;
using SharpOS.AppSdk;

namespace JsonCheckApp
{
    // JSONTEST.EXE — System.Text.Json's Utf8JsonReader / Utf8JsonWriter
    // (vendor/SystemTextJson) on the app tier: one document read whole and in
    // 7-byte chunks with the reader state carried between them, the writer in
    // compact and indented form, escaping, numbers at the edges of their types,
    // and a malformed document. The exit code is the number of checks passed.
    internal static unsafe class AppEntry
    {
        private static int s_passed;
        private static int s_failed;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Main();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static void Check(string name, string expected, string actual)
        {
            bool ok = expected == actual;
            Check(name, ok);
            if (!ok)
            {
                Console.WriteLine("        expected: " + expected);
                Console.WriteLine("        actual:   " + actual);
            }
        }

        // Escapes: Ж (Ж) next to a raw Ж, a quote, a backslash, an escaped
        // solidus, U+1F600 escaped as a surrogate pair and raw as four bytes.
        private static ReadOnlySpan<byte> Sample => """
            {
              "name": "ЖЖ \"q\" \\ \/ 😀 😀",
              "big": 12345678901234567890,
              "neg": -42,
              "e": 1.5e300,
              "nz": -0.0,
              "pi": 3.14159,
              "t": true, "f": false, "n": null,
              "arr": [1, [2, {"x": "y"}], []],
              "obj": {"inner": {"deep": [true]}},
              "b64": "SGVsbG8="
            }
            """u8;

        private const string ExpectedName = "ЖЖ \"q\" \\ / \U0001F600 \U0001F600";

        private static int Main()
        {
            Console.WriteLine("[jsoncheck] begin");

            byte[] sample = Sample.ToArray();

            ReadWhole(sample);
            ReadStreaming(sample);
            WriteCompact();
            WriteIndented();
            RoundTrip();
            Malformed();
            Options();
            StdSurface();
            Document(sample);
            Nodes();

            Console.WriteLine("[jsoncheck] done: passed " + s_passed.ToString() + ", failed " + s_failed.ToString());
            return s_passed;
        }

        private static void ReadWhole(byte[] sample)
        {
            var reader = new Utf8JsonReader(sample);
            int tokens = 0;
            int maxDepth = 0;
            string name = null;
            bool bigNotInt64 = false;
            ulong big = 0;
            double bigAsDouble = 0;
            int neg = 0;
            double e = 0, nz = 1, pi = 0;
            bool t = false, f = true, n = false;
            string b64 = null;
            bool propertyMatch = false;

            string property = null;
            while (reader.Read())
            {
                tokens++;
                if (reader.CurrentDepth > maxDepth) maxDepth = reader.CurrentDepth;

                if (reader.TokenType == JsonTokenType.PropertyName)
                {
                    property = reader.GetString();
                    if (property == "name") propertyMatch = reader.ValueTextEquals("name"u8) && reader.ValueTextEquals("name");
                    continue;
                }

                if (reader.CurrentDepth != 1) continue;
                switch (property)
                {
                    case "name": name = reader.GetString(); break;
                    case "big":
                        bigNotInt64 = !reader.TryGetInt64(out _);
                        big = reader.GetUInt64();
                        bigAsDouble = reader.GetDouble();
                        break;
                    case "neg": neg = reader.GetInt32(); break;
                    case "e": e = reader.GetDouble(); break;
                    case "nz": nz = reader.GetDouble(); break;
                    case "pi": pi = reader.GetDouble(); break;
                    case "t": t = reader.GetBoolean(); break;
                    case "f": f = reader.GetBoolean(); break;
                    case "n": n = reader.TokenType == JsonTokenType.Null; break;
                    case "b64": b64 = Encoding.UTF8.GetString(reader.GetBytesFromBase64()); break;
                }
            }

            Check("read: token count", tokens == 45);
            Check("read: max depth", maxDepth == 4);
            Check("read: bytes consumed", reader.BytesConsumed == sample.Length);
            Check("read: escapes, Ж, surrogate pair", ExpectedName, name);
            Check("read: ValueTextEquals on property name", propertyMatch);
            Check("read: 12345678901234567890 is not Int64", bigNotInt64);
            Check("read: 12345678901234567890 as UInt64", big == 12345678901234567890UL);
            Check("read: 12345678901234567890 as double", bigAsDouble == 1.2345678901234567E+19);
            Check("read: -42", neg == -42);
            Check("read: 1.5e300", e == 1.5e300);
            Check("read: -0.0 keeps its sign", nz == 0 && BitConverter.DoubleToInt64Bits(nz) < 0);
            Check("read: 3.14159", pi == 3.14159);
            Check("read: true/false/null", t && !f && n);
            Check("read: base64", "Hello", b64);
        }

        // Every token as text, for comparing two passes over the same document.
        private static string Describe(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName: return "P:" + reader.GetString();
                case JsonTokenType.String: return "S:" + reader.GetString();
                case JsonTokenType.Number: return "N:" + Encoding.UTF8.GetString(reader.ValueSpan.ToArray());
                case JsonTokenType.StartObject: return "{";
                case JsonTokenType.EndObject: return "}";
                case JsonTokenType.StartArray: return "[";
                case JsonTokenType.EndArray: return "]";
                case JsonTokenType.True: return "true";
                case JsonTokenType.False: return "false";
                case JsonTokenType.Null: return "null";
                default: return "?";
            }
        }

        private static List<string> ReadAll(byte[] json)
        {
            var list = new List<string>();
            var reader = new Utf8JsonReader(json);
            while (reader.Read()) list.Add(Describe(ref reader) + "@" + reader.CurrentDepth.ToString());
            return list;
        }

        // The reader fed in chunks: a token that does not fit stays unconsumed,
        // is moved to the front of the buffer and read again with the next
        // chunk appended, the reader state carried from one instance to the next.
        private static List<string> ReadChunked(byte[] json, int chunk, out int resumes)
        {
            var list = new List<string>();
            var state = new JsonReaderState();
            byte[] buffer = new byte[json.Length + chunk];
            int leftover = 0;
            int position = 0;
            resumes = 0;

            while (true)
            {
                int take = Math.Min(chunk, json.Length - position);
                for (int i = 0; i < take; i++) buffer[leftover + i] = json[position + i];
                position += take;
                int length = leftover + take;
                bool final = position == json.Length;

                var reader = new Utf8JsonReader(new ReadOnlySpan<byte>(buffer, 0, length), final, state);
                while (reader.Read()) list.Add(Describe(ref reader) + "@" + reader.CurrentDepth.ToString());

                int consumed = (int)reader.BytesConsumed;
                leftover = length - consumed;
                for (int i = 0; i < leftover; i++) buffer[i] = buffer[consumed + i];
                if (leftover > 0) resumes++;
                state = reader.CurrentState;

                if (final) break;
            }

            return list;
        }

        private static void ReadStreaming(byte[] sample)
        {
            List<string> whole = ReadAll(sample);
            List<string> chunked = ReadChunked(sample, 7, out int resumes);

            bool same = whole.Count == chunked.Count;
            for (int i = 0; same && i < whole.Count; i++) same = whole[i] == chunked[i];
            Check("stream: 7-byte chunks give the same tokens", same);
            if (!same)
            {
                Console.WriteLine("        whole " + whole.Count.ToString() + " tokens, chunked " + chunked.Count.ToString());
            }
            Check("stream: tokens did straddle chunks", resumes > 0);

            List<string> single = ReadChunked(sample, 1, out _);
            bool sameSingle = whole.Count == single.Count;
            for (int i = 0; sameSingle && i < whole.Count; i++) sameSingle = whole[i] == single[i];
            Check("stream: 1-byte chunks give the same tokens", sameSingle);

            // A number at the very end of a non-final block is not a number yet.
            var partial = new Utf8JsonReader("[12"u8, isFinalBlock: false, new JsonReaderState());
            bool first = partial.Read();
            bool second = partial.Read();
            Check("stream: trailing number waits for more data", first && !second && partial.BytesConsumed == 1);
        }

        private static string Utf8(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());

        private static void WriteDocument(Utf8JsonWriter w)
        {
            w.WriteStartObject();
            w.WriteString("name", "Ж<\"\n\U0001F600");
            w.WriteNumber("i", -42);
            w.WriteNumber("min", long.MinValue);
            w.WriteNumber("u", 12345678901234567890UL);
            w.WriteNumber("d", 1.5e300);
            w.WriteNumber("nz", -0.0);
            w.WriteNumber("pi", 3.14159);
            w.WriteBoolean("t", true);
            w.WriteNull("n");
            w.WriteStartArray("arr");
            w.WriteNumberValue(1);
            w.WriteStringValue("x");
            w.WriteStartObject();
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBase64String("b", "Hello"u8);
            w.WriteEndObject();
        }

        private const string Compact =
            "{\"name\":\"\\u0416\\u003C\\u0022\\n\\uD83D\\uDE00\",\"i\":-42,\"min\":-9223372036854775808," +
            "\"u\":12345678901234567890,\"d\":1.5E+300,\"nz\":-0,\"pi\":3.14159,\"t\":true,\"n\":null," +
            "\"arr\":[1,\"x\",{}],\"b\":\"SGVsbG8=\"}";

        private static void WriteCompact()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                WriteDocument(w);
                w.Flush();
                Check("write: depth back to 0", w.CurrentDepth == 0);
            }
            Check("write: compact", Compact, Utf8(buffer));
        }

        private static void WriteIndented()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
            {
                w.WriteStartObject();
                w.WriteString("a", "b");
                w.WriteStartArray("list");
                w.WriteNumberValue(1);
                w.WriteNumberValue(2u);
                w.WriteStartObject();
                w.WriteBoolean("ok", false);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteStartObject("empty");
                w.WriteEndObject();
                w.WriteEndObject();
            }

            const string expected =
                "{\n" +
                "  \"a\": \"b\",\n" +
                "  \"list\": [\n" +
                "    1,\n" +
                "    2,\n" +
                "    {\n" +
                "      \"ok\": false\n" +
                "    }\n" +
                "  ],\n" +
                "  \"empty\": {}\n" +
                "}";
            Check("write: indented", expected, Utf8(buffer));
        }

        private static void RoundTrip()
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                WriteDocument(w);
            }

            var reader = new Utf8JsonReader(buffer.WrittenSpan);
            string name = null;
            long min = 0;
            double d = 0;
            byte[] bytes = null;
            string property = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName) { property = reader.GetString(); continue; }
                if (reader.CurrentDepth != 1) continue;
                if (property == "name") name = reader.GetString();
                else if (property == "min") min = reader.GetInt64();
                else if (property == "d") d = reader.GetDouble();
                else if (property == "b") bytes = reader.GetBytesFromBase64();
            }

            Check("roundtrip: escaped string", "Ж<\"\n\U0001F600", name);
            Check("roundtrip: long.MinValue", min == long.MinValue);
            Check("roundtrip: 1.5e300", d == 1.5e300);
            Check("roundtrip: base64", bytes != null && Encoding.UTF8.GetString(bytes) == "Hello");
        }

        private static void Malformed()
        {
            bool threw = false;
            string message = "";
            try
            {
                var reader = new Utf8JsonReader("{\"a\": }"u8);
                while (reader.Read()) { }
            }
            catch (JsonException ex)
            {
                threw = true;
                message = ex.Message;
            }
            Check("malformed: throws JsonException", threw);
            Check("malformed: message names the position", message.IndexOf("LineNumber: 0 | BytePositionInLine:") >= 0);
            if (threw) Console.WriteLine("        " + message);

            bool wrongType = false;
            try
            {
                var reader = new Utf8JsonReader("\"text\""u8);
                reader.Read();
                reader.GetInt32();
            }
            catch (InvalidOperationException ex)
            {
                wrongType = ex.Message.IndexOf("'String'") >= 0;
                Console.WriteLine("        " + ex.Message);
            }
            Check("malformed: GetInt32 on a string names the token type", wrongType);

            bool writerThrew = false;
            try
            {
                var buffer = new ArrayBufferWriter<byte>();
                using var w = new Utf8JsonWriter(buffer);
                w.WriteStartObject();
                w.WriteNumberValue(1);
            }
            catch (InvalidOperationException)
            {
                writerThrew = true;
            }
            Check("malformed: writer refuses a value without a property name", writerThrew);
        }

        private static void Options()
        {
            var options = new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            var reader = new Utf8JsonReader("/* c */ [1, // two\n 2,]"u8, options);
            int sum = 0;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.Number) sum += reader.GetInt32();
            }
            Check("options: comments skipped, trailing comma allowed", sum == 3);

            var deep = new Utf8JsonReader("[[[1]]]"u8, new JsonReaderOptions { MaxDepth = 2 });
            bool tooDeep = false;
            try
            {
                while (deep.Read()) { }
            }
            catch (JsonException)
            {
                tooDeep = true;
            }
            Check("options: MaxDepth enforced", tooDeep);

            var skip = new Utf8JsonReader("{\"a\": {\"b\": [1, 2, {}]}, \"c\": 7}"u8);
            skip.Read();
            skip.Read();  // "a"
            skip.Skip();  // over the whole value of "a"
            skip.Read();  // "c"
            bool atC = skip.TokenType == JsonTokenType.PropertyName && skip.ValueTextEquals("c");
            skip.Read();
            Check("options: Skip jumps over a nested value", atC && skip.GetInt32() == 7);
        }

        // The std pieces the port brought along, used directly.
        private static void StdSurface()
        {
            Check("std: double.Parse(\"1.5e300\")", double.Parse("1.5e300") == 1.5e300);
            Check("std: double.Parse(\"-0.0\") keeps its sign", BitConverter.DoubleToInt64Bits(double.Parse("-0.0")) < 0);
            Check("std: double.TryParse rejects \"1.5x\"", !double.TryParse("1.5x", out _));
            Check("std: float.Parse(\"3.5\")", float.Parse("3.5") == 3.5f);

            Span<byte> hex = stackalloc byte[8];
            bool formatted = System.Buffers.Text.Utf8Formatter.TryFormat(0xBEEFu, hex, out int hexLength, new StandardFormat('X', 6));
            Check("std: Utf8Formatter X6", formatted && Encoding.UTF8.GetString(hex.Slice(0, hexLength).ToArray()) == "00BEEF");

            bool parsed = System.Buffers.Text.Utf8Parser.TryParse("7fffFFFF"u8, out uint hexValue, out int hexConsumed, 'x');
            Check("std: Utf8Parser x", parsed && hexValue == 0x7FFFFFFFu && hexConsumed == 8);

            bool invalidUtf8 = false;
            try
            {
                var reader = new Utf8JsonReader(new byte[] { (byte)'"', 0xFF, (byte)'"' });
                reader.Read();
                reader.GetString();
            }
            catch (InvalidOperationException)
            {
                invalidUtf8 = true;
            }
            Check("std: invalid UTF-8 in a string token -> InvalidOperationException", invalidUtf8);

            var buffer = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStringValue("a\uD800b");
            }
            Check("write: lone surrogate escapes as \\uFFFD", "\"a\\uFFFDb\"", Utf8(buffer));

            var floats = new ArrayBufferWriter<byte>();
            using (var w = new Utf8JsonWriter(floats))
            {
                w.WriteStartArray();
                w.WriteNumberValue(0.1f);
                w.WriteNumberValue(-2.5e-10f);
                w.WriteEndArray();
            }
            Check("write: float shortest round-trip", "[0.1,-2.5E-10]", Utf8(floats));

            var floatReader = new Utf8JsonReader(floats.WrittenSpan);
            floatReader.Read();
            floatReader.Read();
            Check("read: GetSingle", floatReader.GetSingle() == 0.1f);
        }

        // JsonDocument / JsonElement (step198).
        private static void Document(byte[] sample)
        {
            using JsonDocument doc = JsonDocument.Parse(sample);
            JsonElement root = doc.RootElement;
            Check("doc: root is an object", root.ValueKind == JsonValueKind.Object);
            Check("doc: string property", ExpectedName, root.GetProperty("name").GetString());
            Check("doc: Int64", root.GetProperty("neg").GetInt64() == -42);
            Check("doc: UInt64 too big for Int64", root.GetProperty("big").GetUInt64() == 12345678901234567890UL && !root.GetProperty("big").TryGetInt64(out _));
            Check("doc: decimal", root.GetProperty("pi").GetDecimal() == 3.14159m);
            Check("doc: TryGetProperty misses", !root.TryGetProperty("nothing", out _));

            int items = 0;
            foreach (JsonElement item in root.GetProperty("arr").EnumerateArray()) items++;
            Check("doc: array length and enumeration", items == 3 && root.GetProperty("arr").GetArrayLength() == 3);

            var names = new List<string>();
            foreach (JsonProperty p in root.GetProperty("obj").GetProperty("inner").EnumerateObject()) names.Add(p.Name);
            Check("doc: object enumeration", names.Count == 1 && names[0] == "deep");

            Check("doc: raw text", "[2, {\"x\": \"y\"}]", root.GetProperty("arr")[1].GetRawText());

            using JsonDocument a = JsonDocument.Parse("{\"a\":1,\"b\":[1.0,2]}");
            using JsonDocument b = JsonDocument.Parse("{\"b\":[1,2.0],\"a\":1e0}");
            Check("doc: DeepEquals ignores order and number form", JsonElement.DeepEquals(a.RootElement, b.RootElement));

            bool threw = false;
            try { JsonDocument.Parse("{\"a\":1,\"a\":2}", new JsonDocumentOptions { AllowDuplicateProperties = false }); }
            catch (JsonException) { threw = true; }
            Check("doc: duplicate property rejected when asked", threw);
        }

        // System.Text.Json.Nodes (step198).
        private static void Nodes()
        {
            System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse("{\"Level\":3,\"Text\":\"hi\",\"Tags\":[\"a\",\"b\"]}");
            Check("node: parse and index", (int)node["Level"] == 3 && (string)node["Text"] == "hi");
            Check("node: array", node["Tags"].AsArray().Count == 2 && (string)node["Tags"][1] == "b");
            Check("node: path", "$.Tags[1]", node["Tags"][1].GetPath());

            node["Level"] = 5;
            node["Extra"] = new System.Text.Json.Nodes.JsonArray(1, 2.5, "x", true);
            Check("node: modify and write", "{\"Level\":5,\"Text\":\"hi\",\"Tags\":[\"a\",\"b\"],\"Extra\":[1,2.5,\"x\",true]}", node.ToJsonString());

            var built = new System.Text.Json.Nodes.JsonObject
            {
                ["n"] = 1.25m,
                ["s"] = "q\"uote",
                ["o"] = new System.Text.Json.Nodes.JsonObject { ["k"] = null },
            };
            Check("node: build object", "{\"n\":1.25,\"s\":\"q\\u0022uote\",\"o\":{\"k\":null}}", built.ToJsonString());
            Check("node: GetValue<decimal>", built["n"].GetValue<decimal>() == 1.25m);
            Check("node: value kind", built["s"].GetValueKind() == JsonValueKind.String && built["n"].GetValueKind() == JsonValueKind.Number);

            System.Text.Json.Nodes.JsonNode copy = node.DeepClone();
            Check("node: DeepClone + DeepEquals", System.Text.Json.Nodes.JsonNode.DeepEquals(node, copy));
            copy["Level"] = 6;
            Check("node: clone is independent", !System.Text.Json.Nodes.JsonNode.DeepEquals(node, copy) && (int)node["Level"] == 5);

            Check("node: JsonValue.Create<long>", System.Text.Json.Nodes.JsonValue.Create<long>(7).ToJsonString() == "7");

            bool threw = false;
            try { node["Text"].GetValue<int>(); }
            catch (InvalidOperationException) { threw = true; }
            Check("node: wrong-type GetValue throws InvalidOperationException", threw);
        }
    }
}
