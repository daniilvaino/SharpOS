using System;
using System.Collections.Generic;

namespace SharpOS.Std.Exchange
{
    /// <summary>What a field holds, as a reader of an untranslated region needs to know it.</summary>
    public enum FieldKind : byte
    {
        Bool, Char, SByte, Byte, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double,
        /// <summary>An offset of another record in the block, or zero.</summary>
        Reference,
        /// <summary>A struct laid out in place.</summary>
        Struct,
    }

    /// <summary>A field of a <see cref="TypeShape"/>: where it is and what it holds.</summary>
    public sealed class FieldShape
    {
        public string Name;
        public string TypeName;
        /// <summary>From the object's table word; for a struct's fields, from the start of its box.</summary>
        public int Offset;
        public FieldKind Kind;
        /// <summary>The struct laid out in place, for <see cref="FieldKind.Struct"/>.</summary>
        public TypeShape Struct;
        /// <summary>The enum the field is declared as, when it is one: its members' names.</summary>
        public TypeShape Enum;

        public int Size => RegionShapes.SizeOf(Kind);
    }

    /// <summary>
    /// One type of a pipe's schema, indexed for reading in place: fields by
    /// name, kinds resolved once.
    /// </summary>
    public sealed class TypeShape
    {
        public ulong Key;
        public string Name;
        public uint BaseSize;
        public ushort ComponentSize;
        public bool IsValueType;
        public FieldShape[] Fields;
        /// <summary>For an array: its "[]" field — the elements, from the data start.</summary>
        public FieldShape Elements;
        public bool IsString;
        public bool IsExpando;
        /// <summary>A box of a primitive or an enum: its one "value" field.</summary>
        public FieldShape BoxedValue;
        public string[] EnumNames;
        public long[] EnumValues;
        internal Dictionary<string, FieldShape> ByName;

        public bool IsArray => Elements != null && !IsString;

        public FieldShape Field(string name)
            => ByName != null && ByName.TryGetValue(name, out FieldShape f) ? f : null;

        /// <summary>The member name for a value of this enum; null when none matches.</summary>
        public string EnumName(long value)
        {
            if (EnumNames == null) return null;
            for (int i = 0; i < EnumNames.Length; i++)
                if (EnumValues[i] == value) return EnumNames[i];
            return null;
        }
    }

    /// <summary>
    /// A pipe's schema, parsed once and indexed: every type by key and by name,
    /// every field's kind resolved. Also checks a block against it: what a
    /// reader of another image's region does once, at receive, so that reading
    /// afterwards needs no bounds checks.
    /// </summary>
    public sealed unsafe class RegionShapes
    {
        private readonly Dictionary<ulong, TypeShape> _byKey = new Dictionary<ulong, TypeShape>();
        private readonly Dictionary<string, TypeShape> _byName = new Dictionary<string, TypeShape>();

        public byte[] Schema { get; }

        private RegionShapes(byte[] schema) => Schema = schema;

        /// <summary>Indexes a schema; null and a complaint when it is malformed or implausible.</summary>
        public static RegionShapes Parse(byte[] schema, out string complaint)
        {
            Dictionary<ulong, TypeKeys.Description> descriptions = RegionSchema.Parse(schema ?? new byte[4], out complaint);
            if (descriptions == null) return null;

            var shapes = new RegionShapes(schema);
            foreach (TypeKeys.Description d in descriptions.Values)
            {
                var t = new TypeShape
                {
                    Key = d.Key,
                    Name = d.Name,
                    BaseSize = d.BaseSize,
                    ComponentSize = d.ComponentSize,
                    IsValueType = d.IsValueType,
                    EnumNames = d.EnumNames,
                    EnumValues = d.EnumValues,
                    IsString = d.Name == "System.String",
                    IsExpando = d.Name == "SharpOS.Std.Pipes.Expando",
                };
                shapes._byKey[d.Key] = t;
                shapes._byName[d.Name] = t;
            }

            // Fields after every type is known: a field's kind may name a struct.
            foreach (TypeKeys.Description d in descriptions.Values)
            {
                TypeShape t = shapes._byKey[d.Key];
                var fields = new List<FieldShape>();
                t.ByName = new Dictionary<string, FieldShape>();
                foreach (TypeKeys.Field f in d.Fields)
                {
                    var shape = new FieldShape { Name = f.Name, TypeName = f.Type, Offset = f.Offset };
                    shape.Kind = shapes.KindOf(f.Type, out shape.Struct);
                    if (f.Enum != null) shapes._byName.TryGetValue(f.Enum, out shape.Enum);
                    if (f.Name == "[]")
                    {
                        if (!t.IsString) t.Elements = shape;
                        continue;
                    }
                    if (f.Name == "value" && d.Fields.Length == 1 && shape.Kind < FieldKind.Reference)
                        t.BoxedValue = shape;
                    fields.Add(shape);
                    t.ByName[f.Name] = shape;
                }
                if (t.IsString)
                    t.Elements = new FieldShape { Name = "[]", TypeName = "System.Char", Offset = 12, Kind = FieldKind.Char };
                t.Fields = fields.ToArray();
                // An enum's box shows its members; the value field takes them too.
                if (t.BoxedValue != null && t.EnumNames != null) t.BoxedValue.Enum = t;

                if (!Plausible(t, out complaint))
                    return null;
            }
            return shapes;
        }

        public TypeShape this[ulong key] => _byKey.TryGetValue(key, out TypeShape t) ? t : null;

        public TypeShape ByName(string name) => _byName.TryGetValue(name, out TypeShape t) ? t : null;

        private FieldKind KindOf(string type, out TypeShape structShape)
        {
            structShape = null;
            switch (type)
            {
                case "System.Boolean": return FieldKind.Bool;
                case "System.Char": return FieldKind.Char;
                case "System.SByte": return FieldKind.SByte;
                case "System.Byte": return FieldKind.Byte;
                case "System.Int16": return FieldKind.Int16;
                case "System.UInt16": return FieldKind.UInt16;
                case "System.Int32": return FieldKind.Int32;
                case "System.UInt32": return FieldKind.UInt32;
                case "System.Int64": return FieldKind.Int64;
                case "System.UInt64": return FieldKind.UInt64;
                case "System.Single": return FieldKind.Single;
                case "System.Double": return FieldKind.Double;
            }
            if (_byName.TryGetValue(type, out TypeShape t) && t.IsValueType && t.BoxedValue == null && !IsPrimitiveName(t.Name))
            {
                structShape = t;
                return FieldKind.Struct;
            }
            return FieldKind.Reference;
        }

        private static bool IsPrimitiveName(string name)
            => name == "System.Boolean" || name == "System.Char" || name == "System.SByte" || name == "System.Byte"
               || name == "System.Int16" || name == "System.UInt16" || name == "System.Int32" || name == "System.UInt32"
               || name == "System.Int64" || name == "System.UInt64" || name == "System.Single" || name == "System.Double";

        public static int SizeOf(FieldKind kind)
        {
            switch (kind)
            {
                case FieldKind.Bool: case FieldKind.SByte: case FieldKind.Byte: return 1;
                case FieldKind.Char: case FieldKind.Int16: case FieldKind.UInt16: return 2;
                case FieldKind.Int32: case FieldKind.UInt32: case FieldKind.Single: return 4;
                default: return 8;
            }
        }

        // A schema comes from another image: a field must lie inside its object.
        private static bool Plausible(TypeShape t, out string complaint)
        {
            complaint = null;
            if (t.BaseSize < 16 || t.BaseSize > 64u * 1024 * 1024)
            {
                complaint = $"type {t.Name}: implausible size {t.BaseSize}";
                return false;
            }
            foreach (FieldShape f in t.Fields)
            {
                int size = f.Kind == FieldKind.Struct ? (int)f.Struct.BaseSize - 16 : f.Size;
                if (f.Offset < 8 || f.Offset + size > t.BaseSize)
                {
                    complaint = $"type {t.Name}: field {f.Name} lies outside the object";
                    return false;
                }
            }
            if (t.Elements != null && (t.ComponentSize == 0 || t.Elements.Offset < 12 || t.Elements.Offset > t.BaseSize))
            {
                complaint = $"type {t.Name}: elements outside the object";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Checks a region against this schema, once, before anyone reads it:
        /// every record has a described key and lies inside the block, and every
        /// reference is zero or the start of a record of the block. False and a
        /// complaint otherwise; the block is not changed either way.
        /// </summary>
        public bool Validate(byte* at, ulong size, out string complaint)
        {
            complaint = null;
            if (size < Region.HeaderSize + 16)
            {
                complaint = "block too small";
                return false;
            }

            var starts = new HashSet<ulong>();
            var records = new List<ulong>();
            for (ulong cursor = 0; cursor < size;)
            {
                if (size - cursor < Region.HeaderSize + 16)
                {
                    complaint = $"truncated record at {cursor}";
                    return false;
                }
                ulong objectAt = (ulong)at + cursor + Region.HeaderSize;
                TypeShape t = this[*(ulong*)objectAt];
                if (t == null)
                {
                    complaint = $"record at {cursor}: key 0x{*(ulong*)objectAt:x} is not in the pipe's description";
                    return false;
                }
                ulong total = t.BaseSize;
                if (t.ComponentSize != 0)
                    total += (ulong)*(uint*)(objectAt + 8) * t.ComponentSize;
                ulong next = cursor + Region.HeaderSize + (((total + 7) & ~7UL) - 8);
                if (next > size || next <= cursor)
                {
                    complaint = $"record at {cursor} ({t.Name}) runs past the block";
                    return false;
                }
                starts.Add(cursor + Region.HeaderSize);
                records.Add(objectAt);
                cursor = next;
            }

            foreach (ulong objectAt in records)
            {
                TypeShape t = this[*(ulong*)objectAt];
                if (!CheckFields(t.Fields, objectAt, starts, out complaint))
                    return false;
                if (t.IsArray && (t.Elements.Kind == FieldKind.Reference || t.Elements.Kind == FieldKind.Struct))
                {
                    uint length = *(uint*)(objectAt + 8);
                    for (uint i = 0; i < length; i++)
                    {
                        ulong element = objectAt + (ulong)t.Elements.Offset + (ulong)i * t.ComponentSize;
                        if (t.Elements.Kind == FieldKind.Reference
                            ? !CheckReference(element, starts, out complaint)
                            : !CheckFields(t.Elements.Struct.Fields, element - 8, starts, out complaint))
                            return false;
                    }
                }
            }
            return true;
        }

        private bool CheckFields(FieldShape[] fields, ulong objectAt, HashSet<ulong> starts, out string complaint)
        {
            complaint = null;
            foreach (FieldShape f in fields)
            {
                if (f.Kind == FieldKind.Reference && !CheckReference(objectAt + (ulong)f.Offset, starts, out complaint))
                    return false;
                // A struct's offsets were measured in its box: its fields start 8 past the value.
                if (f.Kind == FieldKind.Struct && !CheckFields(f.Struct.Fields, objectAt + (ulong)f.Offset - 8, starts, out complaint))
                    return false;
            }
            return true;
        }

        private static bool CheckReference(ulong slot, HashSet<ulong> starts, out string complaint)
        {
            complaint = null;
            ulong target = *(ulong*)slot;
            if (target == 0 || starts.Contains(target)) return true;
            complaint = $"reference 0x{target:x} is not a record of the block";
            return false;
        }
    }
}
