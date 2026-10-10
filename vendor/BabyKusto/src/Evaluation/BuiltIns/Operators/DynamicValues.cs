// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// SharpOS cut: the value inside a dynamic, for the operators with dynamic
// operands (DynamicComparison.cs, DynamicArithmetic.cs) — a file of SharpOS's.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace BabyKusto.Core.Evaluation.BuiltIns.Impl
{
    internal static class DynamicValues
    {
        // A node's scalar — a long for a whole number, a double, a string, a
        // bool — an object or an array as its node, JSON null as null; a value
        // of a typed column as it is, whole numbers as longs.
        public static object? Unwrap(object? value)
        {
            switch (value)
            {
                case null:
                    return null;
                case JsonValue node:
                    object inner = node.GetValue<object>();
                    if (inner is JsonElement element)
                    {
                        switch (element.ValueKind)
                        {
                            case JsonValueKind.Number: return element.TryGetInt64(out long whole) ? (object)whole : element.GetDouble();
                            case JsonValueKind.String: return element.GetString();
                            case JsonValueKind.True: return true;
                            case JsonValueKind.False: return false;
                            case JsonValueKind.Null: return null;
                            default: return node;
                        }
                    }
                    return Normal(inner);
                default:
                    return Normal(value);
            }
        }

        private static object Normal(object value)
        {
            switch (value)
            {
                case int i: return (long)i;
                case uint u: return (long)u;
                case short s: return (long)s;
                case ushort us: return (long)us;
                case byte b: return (long)b;
                case sbyte sb: return (long)sb;
                case ulong ul: return ul <= long.MaxValue ? (object)(long)ul : (double)ul;
                case float f: return (double)f;
                case char c: return c.ToString();
                default: return value;
            }
        }
    }
}
