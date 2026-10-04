using System.Collections.Generic;
using SharpOS.Std.Exchange;

namespace OS.Kernel.Pipes
{
    // The type check when two ends meet (pipe spec Р22). The writer declared
    // its schema and root key, the reader its own description of the type it
    // expects. Equal keys: equal layouts, nothing to say. Otherwise the error
    // names the type and the first field that differs, with name, type and
    // offset from both sides. No conversion is attempted.
    internal static class TypeCheck
    {
        /// <summary>Null when the reader may read what the writer sends; otherwise why not.</summary>
        public static string Compare(byte[] writerSchema, ulong writerKey, byte[] readerSchema, ulong readerKey)
        {
            // A reader without a type reads by the description; a side that has
            // not declared yet is checked when it does.
            if (readerKey == 0 || writerKey == 0 || writerKey == readerKey)
                return null;

            TypeKeys.Description writer = Find(writerSchema, writerKey);
            TypeKeys.Description reader = Find(readerSchema, readerKey);
            if (writer == null || reader == null)
                return "type check: a description is missing (writer key 0x" + Hex(writerKey)
                       + ", reader key 0x" + Hex(readerKey) + ")";

            if (writer.Name != reader.Name)
                return "different types: writer sends " + writer.Name + ", reader expects " + reader.Name;

            int count = writer.Fields.Length > reader.Fields.Length ? writer.Fields.Length : reader.Fields.Length;
            for (int i = 0; i < count; i++)
            {
                string w = i < writer.Fields.Length ? Field(writer.Fields[i]) : "(none)";
                string r = i < reader.Fields.Length ? Field(reader.Fields[i]) : "(none)";
                if (w != r)
                    return "type " + writer.Name + ": field #" + i.ToString() + " differs: writer " + w + ", reader " + r;
            }

            if (writer.BaseSize != reader.BaseSize || writer.ComponentSize != reader.ComponentSize)
                return "type " + writer.Name + ": size differs: writer " + writer.BaseSize.ToString()
                       + ", reader " + reader.BaseSize.ToString();

            return "type " + writer.Name + ": layouts differ outside the fields (GC series)";
        }

        private static TypeKeys.Description Find(byte[] schema, ulong key)
        {
            if (schema == null) return null;
            Dictionary<ulong, TypeKeys.Description> map = RegionSchema.Parse(schema, out _);
            return map != null && map.TryGetValue(key, out TypeKeys.Description d) ? d : null;
        }

        private static string Field(TypeKeys.Field f) => f.Name + ":" + f.Type + "@" + f.Offset.ToString();

        private static string Hex(ulong v) => $"{v:x}";
    }
}
