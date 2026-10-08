using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DataApps
{
    // JSON text as plain values, to compare two texts by what they say, not
    // by how they spell it (test 2): an object is a list of name/value pairs
    // in order, an array a list, a number a long when it is one, else a
    // double; strings decoded.
    internal static class JsonValues
    {
        internal sealed class Obj
        {
            public readonly List<string> Names = new List<string>();
            public readonly List<object> Values = new List<object>();
        }

        public static List<object> ParseAll(byte[] json)
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions { AllowMultipleValues = true });
            var all = new List<object>();
            while (reader.Read()) all.Add(Value(ref reader));
            return all;
        }

        private static object Value(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                {
                    var o = new Obj();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        o.Names.Add(reader.GetString());
                        reader.Read();
                        o.Values.Add(Value(ref reader));
                    }
                    return o;
                }
                case JsonTokenType.StartArray:
                {
                    var a = new List<object>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) a.Add(Value(ref reader));
                    return a;
                }
                case JsonTokenType.String: return reader.GetString();
                case JsonTokenType.Number: return reader.TryGetInt64(out long l) ? (object)l : reader.GetDouble();
                case JsonTokenType.True: return true;
                case JsonTokenType.False: return false;
                default: return null;
            }
        }

        /// <summary>Null when equal, else where they differ.</summary>
        public static string Differ(object a, object b, string at)
        {
            if (a == null || b == null) return a == null && b == null ? null : at + ": null against a value";
            if (a is Obj oa && b is Obj ob)
            {
                if (oa.Names.Count != ob.Names.Count) return at + ": " + oa.Names.Count.ToString() + " names against " + ob.Names.Count.ToString();
                for (int i = 0; i < oa.Names.Count; i++)
                {
                    if (oa.Names[i] != ob.Names[i]) return at + ": name '" + oa.Names[i] + "' against '" + ob.Names[i] + "'";
                    string d = Differ(oa.Values[i], ob.Values[i], at + "." + oa.Names[i]);
                    if (d != null) return d;
                }
                return null;
            }
            if (a is List<object> la && b is List<object> lb)
            {
                if (la.Count != lb.Count) return at + ": " + la.Count.ToString() + " elements against " + lb.Count.ToString();
                for (int i = 0; i < la.Count; i++)
                {
                    string d = Differ(la[i], lb[i], at + "[" + i.ToString() + "]");
                    if (d != null) return d;
                }
                return null;
            }
            if (a is string sa && b is string sb) return sa == sb ? null : at + ": \"" + sa + "\" against \"" + sb + "\"";
            if (a is bool ba && b is bool bb) return ba == bb ? null : at + ": bool";
            if (a is long xa && b is long xb) return xa == xb ? null : at + ": " + xa.ToString() + " against " + xb.ToString();
            if ((a is long || a is double) && (b is long || b is double))
            {
                double da = a is long al ? al : (double)a;
                double db = b is long bl ? bl : (double)b;
                return da == db ? null : at + ": " + da.ToString() + " against " + db.ToString();
            }
            return at + ": different kinds";
        }
    }
}
