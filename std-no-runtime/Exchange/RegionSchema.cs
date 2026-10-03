using System;
using System.Collections.Generic;
using System.Text;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// A channel's schema: one description per type (key, name, sizes, fields
    /// with types and offsets), and a printer that walks a region by it.
    /// </summary>
    /// <remarks>
    /// The reader declares nothing. It matches the key in each table word with a
    /// description and goes field by field, so a program that has never seen the
    /// producer's types can still show what came. The schema travels once per
    /// channel, beside the regions, not inside each of them.
    ///
    /// Printing reads the region as it arrived — keys and offsets — and writes
    /// nothing, so it works before translation and on a region nobody will
    /// translate.
    /// </remarks>
    public static unsafe class RegionSchema
    {
        /// <summary>Serializes the descriptions of every type this image declared.</summary>
        public static byte[] Build(List<TypeKeys.Description> types)
        {
            var buffer = new List<byte>();
            U32(buffer, (uint)types.Count);
            foreach (TypeKeys.Description d in types)
            {
                U64(buffer, d.Key);
                Str(buffer, d.Name);
                U32(buffer, d.BaseSize);
                U16(buffer, d.ComponentSize);
                buffer.Add(d.IsValueType ? (byte)1 : (byte)0);
                U16(buffer, d.Fields.Length);
                foreach (TypeKeys.Field f in d.Fields)
                {
                    Str(buffer, f.Name);
                    Str(buffer, f.Type);
                    U32(buffer, (uint)f.Offset);
                }
            }
            return buffer.ToArray();
        }

        /// <summary>Parses a schema; null and a complaint when it is malformed.</summary>
        public static Dictionary<ulong, TypeKeys.Description> Parse(byte[] schema, out string complaint)
        {
            complaint = null;
            var map = new Dictionary<ulong, TypeKeys.Description>();
            var reader = new Reader { Data = schema };
            uint count = reader.U32();
            for (uint t = 0; t < count && reader.Ok; t++)
            {
                var d = new TypeKeys.Description
                {
                    Key = reader.U64(),
                    Name = reader.Str(),
                    BaseSize = reader.U32(),
                    ComponentSize = (ushort)reader.U16(),
                    IsValueType = reader.U8() != 0,
                };
                var fields = new TypeKeys.Field[reader.Ok ? reader.U16() : 0];
                for (int f = 0; f < fields.Length && reader.Ok; f++)
                {
                    string name = reader.Str();
                    string type = reader.Str();
                    fields[f] = new TypeKeys.Field(name, type, (int)reader.U32());
                }
                d.Fields = fields;
                if (reader.Ok)
                    map[d.Key] = d;
            }
            if (!reader.Ok)
            {
                complaint = "schema truncated";
                return null;
            }
            return map;
        }

        /// <summary>
        /// Prints the graph in a region, one line per object, by the schema alone.
        /// Returns the number of objects printed; -1 and a complaint when a key
        /// has no description or a record runs past the region.
        /// </summary>
        public static int Print(byte* at, ulong size, byte[] schema, Action<string> say, out string complaint)
        {
            Dictionary<ulong, TypeKeys.Description> types = Parse(schema, out complaint);
            if (types == null)
                return -1;
            var byName = new Dictionary<string, TypeKeys.Description>();
            foreach (TypeKeys.Description d in types.Values)
                byName[d.Name] = d;

            // Table-word offset → record number, so references print as #n.
            var indexByWord = new Dictionary<ulong, int>();
            var records = new List<ulong>();
            var descriptions = new List<TypeKeys.Description>();
            for (ulong cursor = 0; cursor < size;)
            {
                ulong objectAt = (ulong)at + cursor + Region.HeaderSize;
                ulong key = *(ulong*)objectAt;
                if (!types.TryGetValue(key, out TypeKeys.Description d))
                {
                    complaint = $"key 0x{key:x} has no description (record at {cursor})";
                    return -1;
                }
                uint length = d.ComponentSize != 0 ? *(uint*)(objectAt + 8) : 0;
                ulong total = d.BaseSize + (ulong)length * d.ComponentSize;
                ulong next = cursor + Region.HeaderSize + (((total + 7) & ~7UL) - 8);
                if (next > size || next <= cursor)
                {
                    complaint = $"record at {cursor} runs past the region";
                    return -1;
                }
                indexByWord[cursor + Region.HeaderSize] = records.Count;
                records.Add(objectAt);
                descriptions.Add(d);
                cursor = next;
            }

            for (int i = 0; i < records.Count; i++)
            {
                ulong objectAt = records[i];
                TypeKeys.Description d = descriptions[i];
                uint length = d.ComponentSize != 0 ? *(uint*)(objectAt + 8) : 0;

                var line = new StringBuilder();
                line.Append('#').Append(i.ToString()).Append(' ').Append(d.Name);
                if (d.ComponentSize != 0)
                    line.Append('[').Append(length.ToString()).Append(']');

                if (d.Name == "String")
                {
                    line.Append(" \"");
                    char* text = (char*)(objectAt + 12);
                    for (uint c = 0; c < length && c < 40; c++)
                        line.Append(text[c]);
                    line.Append(length > 40 ? "…\"" : "\"");
                    say(line.ToString());
                    continue;
                }

                line.Append(" {");
                bool first = true;
                foreach (TypeKeys.Field f in d.Fields)
                {
                    if (f.Name == "[]")
                        continue;
                    line.Append(first ? " " : ", ");
                    first = false;
                    line.Append(f.Name).Append('=');
                    line.Append(Value(f.Type, objectAt + (ulong)f.Offset, byName, indexByWord));
                }
                foreach (TypeKeys.Field f in d.Fields)
                {
                    if (f.Name != "[]")
                        continue;
                    for (uint e = 0; e < length && e < 8; e++)
                    {
                        line.Append(first ? " " : ", ");
                        first = false;
                        line.Append('[').Append(e.ToString()).Append("]=");
                        line.Append(Value(f.Type, objectAt + (ulong)f.Offset + e * d.ComponentSize, byName, indexByWord));
                    }
                    if (length > 8)
                        line.Append(", … ").Append((length - 8).ToString()).Append(" more");
                }
                line.Append(" }");
                say(line.ToString());
            }
            return records.Count;
        }

        // A primitive's text; a struct inline, by its own description (its
        // field offsets were measured in a box, so they are 8 past the start of
        // the value); anything else a reference, printed as #n.
        private static string Value(string type, ulong at, Dictionary<string, TypeKeys.Description> byName,
                                    Dictionary<ulong, int> indexByWord)
        {
            switch (type)
            {
                case "Boolean": return *(byte*)at != 0 ? "true" : "false";
                case "Byte": return (*(byte*)at).ToString();
                case "Char": return "'" + (*(char*)at).ToString() + "'";
                case "Int16": return (*(short*)at).ToString();
                case "Int32": return (*(int*)at).ToString();
                case "UInt32": return (*(uint*)at).ToString();
                case "Int64": return (*(long*)at).ToString();
                case "UInt64": return (*(ulong*)at).ToString();
                case "Single": return (*(float*)at).ToString();
                case "Double": return (*(double*)at).ToString();
            }

            if (byName.TryGetValue(type, out TypeKeys.Description value) && value.IsValueType)
            {
                var inline = new StringBuilder("{");
                for (int i = 0; i < value.Fields.Length; i++)
                {
                    TypeKeys.Field f = value.Fields[i];
                    inline.Append(i == 0 ? " " : ", ").Append(f.Name).Append('=');
                    inline.Append(Value(f.Type, at + (ulong)(f.Offset - 8), byName, indexByWord));
                }
                return inline.Append(" }").ToString();
            }

            ulong word = *(ulong*)at;
            if (word == 0)
                return "null";
            return indexByWord.TryGetValue(word, out int index) ? "#" + index.ToString() : $"outside 0x{word:x}";
        }

        private static void U16(List<byte> b, int v) { b.Add((byte)v); b.Add((byte)(v >> 8)); }
        private static void U32(List<byte> b, uint v) { for (int i = 0; i < 4; i++) b.Add((byte)(v >> (8 * i))); }
        private static void U64(List<byte> b, ulong v) { for (int i = 0; i < 8; i++) b.Add((byte)(v >> (8 * i))); }

        private static void Str(List<byte> b, string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            U16(b, bytes.Length);
            for (int i = 0; i < bytes.Length; i++) b.Add(bytes[i]);
        }

        private sealed class Reader
        {
            public byte[] Data;
            public int At;
            public bool Ok = true;

            private bool Has(int n)
            {
                if (Ok && At + n <= Data.Length) return true;
                Ok = false;
                return false;
            }

            public int U8()
            {
                if (!Has(1)) return 0;
                return Data[At++];
            }

            public int U16()
            {
                if (!Has(2)) return 0;
                int v = Data[At] | (Data[At + 1] << 8);
                At += 2;
                return v;
            }

            public uint U32()
            {
                if (!Has(4)) return 0;
                uint v = 0;
                for (int i = 0; i < 4; i++) v |= (uint)Data[At++] << (8 * i);
                return v;
            }

            public ulong U64()
            {
                if (!Has(8)) return 0;
                ulong v = 0;
                for (int i = 0; i < 8; i++) v |= (ulong)Data[At++] << (8 * i);
                return v;
            }

            public string Str()
            {
                int n = U16();
                if (!Has(n)) return "";
                string s = Encoding.UTF8.GetString(Data, At, n);
                At += n;
                return s;
            }
        }
    }
}
