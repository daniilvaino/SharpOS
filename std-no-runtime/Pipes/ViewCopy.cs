using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpOS.Std.Exchange;
using SharpOS.Std.NoRuntime;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// Copies out of an untranslated region into this image's heap: as
    /// Expandos (ToExpando), or laid out by field name into this image's own
    /// [Message] classes (Into). Shared references and cycles are kept.
    /// </summary>
    internal static unsafe class ViewCopy
    {
        // ---- ToExpando ----

        public static object ToHeapValue(View v) => new Copier().Value(v);

        private sealed class Copier
        {
            // Source object -> its copy. Holds the copies alive too: a copy is
            // reachable from here while its own fields are filled.
            private readonly Dictionary<ulong, object> _done = new Dictionary<ulong, object>();

            public object Value(View v)
            {
                switch (v.Kind)
                {
                    case ViewKind.Null: return null;
                    case ViewKind.Bool: return (bool)v;
                    case ViewKind.Char: return (char)v;
                    case ViewKind.Float: return v.ValueKind == FieldKind.Single ? (object)(float)v : (double)v;
                    case ViewKind.Integer: return Boxed(v);
                    case ViewKind.String:
                        if (_done.TryGetValue(v.Address, out object text)) return text;
                        string copy = (string)v;
                        _done[v.Address] = copy;
                        return copy;
                    case ViewKind.Array: return Array(v);
                    default: return Object(v);
                }
            }

            // An enum comes out as its number.
            private static object Boxed(View v)
            {
                switch (v.ValueKind)
                {
                    case FieldKind.SByte: return (sbyte)v;
                    case FieldKind.Byte: return (byte)v;
                    case FieldKind.Int16: return (short)v;
                    case FieldKind.UInt16: return (ushort)v;
                    case FieldKind.Int32: return (int)v;
                    case FieldKind.UInt32: return (uint)v;
                    case FieldKind.UInt64: return (ulong)v;
                    default: return (long)v;
                }
            }

            private object Object(View v)
            {
                if (!v.IsInPlaceStruct && _done.TryGetValue(v.Address, out object seen)) return seen;

                // A struct this image has under the same key and with no
                // references in it comes out as itself: a DateTime stays a DateTime.
                if (v.Shape.IsValueType && HasNoReferences(v.Shape)
                    && TypeKeys.TryTable(v.Shape.Key, out ulong table))
                    return BoxCopy(v, table);

                var e = new Expando();
                if (!v.IsInPlaceStruct) _done[v.Address] = e;
                foreach (ViewField f in v.Fields)
                    e[f.Name] = Value(f.Value);
                return e;
            }

            private object Array(View v)
            {
                if (_done.TryGetValue(v.Address, out object seen)) return seen;
                FieldShape elements = v.Shape.Elements;
                int length = v.Length;
                object result;
                switch (elements.Kind)
                {
                    case FieldKind.Bool: { var a = new bool[length]; for (int i = 0; i < length; i++) a[i] = (bool)v[i]; result = a; break; }
                    case FieldKind.Char: { var a = new char[length]; for (int i = 0; i < length; i++) a[i] = (char)v[i]; result = a; break; }
                    case FieldKind.SByte: { var a = new sbyte[length]; for (int i = 0; i < length; i++) a[i] = (sbyte)v[i]; result = a; break; }
                    case FieldKind.Byte: { var a = new byte[length]; for (int i = 0; i < length; i++) a[i] = (byte)v[i]; result = a; break; }
                    case FieldKind.Int16: { var a = new short[length]; for (int i = 0; i < length; i++) a[i] = (short)v[i]; result = a; break; }
                    case FieldKind.UInt16: { var a = new ushort[length]; for (int i = 0; i < length; i++) a[i] = (ushort)v[i]; result = a; break; }
                    case FieldKind.Int32: { var a = new int[length]; for (int i = 0; i < length; i++) a[i] = (int)v[i]; result = a; break; }
                    case FieldKind.UInt32: { var a = new uint[length]; for (int i = 0; i < length; i++) a[i] = (uint)v[i]; result = a; break; }
                    case FieldKind.Int64: { var a = new long[length]; for (int i = 0; i < length; i++) a[i] = (long)v[i]; result = a; break; }
                    case FieldKind.UInt64: { var a = new ulong[length]; for (int i = 0; i < length; i++) a[i] = (ulong)v[i]; result = a; break; }
                    case FieldKind.Single: { var a = new float[length]; for (int i = 0; i < length; i++) a[i] = (float)v[i]; result = a; break; }
                    case FieldKind.Double: { var a = new double[length]; for (int i = 0; i < length; i++) a[i] = (double)v[i]; result = a; break; }
                    default:
                        if (elements.TypeName == "System.String")
                        {
                            var strings = new string[length];
                            _done[v.Address] = strings;
                            for (int i = 0; i < length; i++) strings[i] = (string)Value(v[i]);
                            return strings;
                        }
                        var objects = new object[length];
                        _done[v.Address] = objects;
                        for (int i = 0; i < length; i++) objects[i] = Value(v[i]);
                        return objects;
                }
                _done[v.Address] = result;
                return result;
            }
        }

        private static bool HasNoReferences(TypeShape t)
        {
            foreach (FieldShape f in t.Fields)
                if (f.Kind == FieldKind.Reference || (f.Kind == FieldKind.Struct && !HasNoReferences(f.Struct)))
                    return false;
            return true;
        }

        // A struct's bytes into a new box of this image's table; the view's
        // address is where its fields' offsets count from (the box start).
        private static object BoxCopy(View v, ulong table)
        {
            var mt = (GcMethodTable*)table;
            void* raw = GcHeap.AllocateObject(mt->BaseSize, mt);
            if (raw == null) throw new OutOfMemoryException();
            MemoryPrimitives.Memcpy((byte*)raw + 8, (void*)(v.Address + 8), mt->BaseSize - 16);
            nint address = (nint)raw;
            return Unsafe.As<nint, object>(ref address);
        }

        // ---- Into<T> ----

        public static T Into<T>(View v) where T : class
        {
            ulong key = MessageCatalog.KeyOf(typeof(T));
            TypeKeys.Description target = key == 0 ? null : TypeKeys.DescriptionOf(key);
            if (target == null)
                throw new InvalidOperationException("Into: the target type is not in this image's catalog");
            return Unsafe.As<T>(new Mapper().Reference(v, target.Name));
        }

        /// <summary>A step of a plan: one target field from one source field.</summary>
        private sealed class Step
        {
            public FieldShape Source;          // null: the target keeps its default
            public int TargetOffset;
            public FieldKind TargetKind;
            public string TargetType;          // for a reference or a struct
        }

        /// <summary>How a source type's fields become a target type's, built once per pair.</summary>
        private sealed class Plan
        {
            public TypeKeys.Description Target;
            public ulong TargetTable;
            public Step[] Steps;
        }

        private static readonly Dictionary<string, Plan> s_plans = new Dictionary<string, Plan>();
        private static Dictionary<string, TypeKeys.Description> s_ownByName;
        private static int s_ownCount;

        private static TypeKeys.Description Own(string name)
        {
            MessageCatalog.Ensure();
            List<TypeKeys.Description> all = TypeKeys.Declared;
            if (s_ownByName == null || s_ownCount != all.Count)
            {
                var map = new Dictionary<string, TypeKeys.Description>();
                foreach (TypeKeys.Description d in all) map[d.Name] = d;
                s_ownByName = map;
                s_ownCount = all.Count;
            }
            return s_ownByName.TryGetValue(name, out TypeKeys.Description found) ? found : null;
        }

        private static FieldKind OwnKind(string type)
        {
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
            TypeKeys.Description d = Own(type);
            return d != null && d.IsValueType ? FieldKind.Struct : FieldKind.Reference;
        }

        // Widening without loss: what C# converts implicitly, less the
        // conversions to float and double that round.
        private static bool Widens(FieldKind from, FieldKind to)
        {
            if (from == to) return true;
            switch (from)
            {
                case FieldKind.SByte: return to == FieldKind.Int16 || to == FieldKind.Int32 || to == FieldKind.Int64 || to == FieldKind.Single || to == FieldKind.Double;
                case FieldKind.Byte: return to == FieldKind.Int16 || to == FieldKind.UInt16 || to == FieldKind.Int32 || to == FieldKind.UInt32
                                            || to == FieldKind.Int64 || to == FieldKind.UInt64 || to == FieldKind.Single || to == FieldKind.Double;
                case FieldKind.Int16: return to == FieldKind.Int32 || to == FieldKind.Int64 || to == FieldKind.Single || to == FieldKind.Double;
                case FieldKind.UInt16:
                case FieldKind.Char: return to == FieldKind.Int32 || to == FieldKind.UInt32 || to == FieldKind.Int64 || to == FieldKind.UInt64
                                            || to == FieldKind.Single || to == FieldKind.Double || (from == FieldKind.Char && to == FieldKind.UInt16);
                case FieldKind.Int32: return to == FieldKind.Int64 || to == FieldKind.Double;
                case FieldKind.UInt32: return to == FieldKind.Int64 || to == FieldKind.UInt64 || to == FieldKind.Double;
                case FieldKind.Single: return to == FieldKind.Double;
                default: return false;
            }
        }

        private static Plan PlanFor(TypeShape source, TypeKeys.Description target)
        {
            string id = source.Name + "\u0001" + source.Key.ToString() + "\u0001" + target.Name;
            lock (s_plans)
                if (s_plans.TryGetValue(id, out Plan known)) return known;

            if (!TypeKeys.TryTable(target.Key, out ulong table))
                throw new InvalidOperationException("Into: " + target.Name + " has no table here");
            var steps = new List<Step>();
            foreach (TypeKeys.Field tf in target.Fields)
            {
                if (tf.Name == "[]") continue;
                var step = new Step
                {
                    Source = source.Field(tf.Name),
                    TargetOffset = tf.Offset,
                    TargetKind = OwnKind(tf.Type),
                    TargetType = tf.Type,
                };
                if (step.Source != null) Check(source, target, tf, step);
                steps.Add(step);
            }
            var plan = new Plan { Target = target, TargetTable = table, Steps = steps.ToArray() };
            lock (s_plans) s_plans[id] = plan;
            return plan;
        }

        // Compatible, or an exception naming the type and the field.
        private static void Check(TypeShape source, TypeKeys.Description target, TypeKeys.Field tf, Step step)
        {
            FieldShape sf = step.Source;
            bool ok;
            if (step.TargetKind < FieldKind.Reference)
                ok = sf.Kind < FieldKind.Reference && Widens(sf.Kind, step.TargetKind);
            else if (step.TargetKind == FieldKind.Struct)
                ok = sf.Kind == FieldKind.Struct;
            else if (step.TargetType == "System.Object")
                ok = true;
            else if (step.TargetType == "System.String")
                ok = sf.Kind == FieldKind.Reference && (sf.TypeName == "System.String" || sf.TypeName == "System.Object");
            else if (step.TargetType.EndsWith("[]"))
                ok = sf.Kind == FieldKind.Reference && (sf.TypeName.EndsWith("[]") || sf.TypeName == "System.Object");
            else
                ok = sf.Kind == FieldKind.Reference && sf.TypeName != "System.String" && !sf.TypeName.EndsWith("[]");
            if (!ok)
                throw new InvalidCastException("Into<" + target.Name + ">: field '" + tf.Name + "' is " + tf.Type
                                               + " here and " + sf.TypeName + " in " + source.Name);
        }

        private sealed class Mapper
        {
            // Source object -> its copy; keeps shared references shared and
            // cycles closed, and the copies alive while they are filled.
            private readonly Dictionary<ulong, object> _done = new Dictionary<ulong, object>();
            private Copier _copier;

            // A source value into a reference of the target's declared type.
            public object Reference(View v, string targetType)
            {
                if (v.Kind == ViewKind.Null) return null;
                if (targetType == "System.Object") return (_copier ??= new Copier()).Value(v);
                if (targetType == "System.String")
                {
                    if (v.Kind != ViewKind.String) throw Mismatch(targetType, v);
                    if (_done.TryGetValue(v.Address, out object s)) return s;
                    string text = (string)v;
                    _done[v.Address] = text;
                    return text;
                }
                if (targetType.EndsWith("[]")) return Array(v, targetType);
                if (v.Kind != ViewKind.Object || v.IsInPlaceStruct) throw Mismatch(targetType, v);
                if (_done.TryGetValue(v.Address, out object seen)) return seen;

                TypeKeys.Description target = Own(targetType);
                if (target == null) throw new InvalidOperationException("Into: " + targetType + " is not in this image's catalog");
                Plan plan = PlanFor(v.Shape, target);
                object copy = Allocate(plan.TargetTable, 0, false);
                _done[v.Address] = copy;
                Fill(plan, v, Address(copy));
                return copy;
            }

            private void Fill(Plan plan, View source, ulong target)
            {
                foreach (Step step in plan.Steps)
                {
                    if (step.Source == null) continue;
                    ulong to = target + (ulong)step.TargetOffset;
                    View value = source.FieldAt(step.Source);
                    if (step.TargetKind < FieldKind.Reference)
                        StoreValue(to, step.TargetKind, value);
                    else if (step.TargetKind == FieldKind.Struct)
                        StoreStruct(to, step.TargetType, value);
                    else
                        Unsafe.AsRef<object>((void*)to) = Reference(value, step.TargetType);
                }
            }

            private object Array(View v, string targetType)
            {
                if (v.Kind != ViewKind.Array) throw Mismatch(targetType, v);
                if (_done.TryGetValue(v.Address, out object seen)) return seen;
                TypeKeys.Description arrayType = Own(targetType);
                if (arrayType == null || arrayType.Fields.Length != 1 || !TypeKeys.TryTable(arrayType.Key, out ulong table))
                    throw new InvalidOperationException("Into: " + targetType + " is not in this image's catalog");
                // The array's own description: where its elements start and what
                // they are (an enum array's elements are its underlying numbers).
                TypeKeys.Field elements = arrayType.Fields[0];
                FieldKind kind = OwnKind(elements.Type);
                if (kind < FieldKind.Reference && !(v.Shape.Elements.Kind < FieldKind.Reference && Widens(v.Shape.Elements.Kind, kind)))
                    throw Mismatch(targetType, v);

                int length = v.Length;
                object copy = Allocate(table, length, true);
                _done[v.Address] = copy;
                ulong data = Address(copy) + (ulong)elements.Offset;
                uint component = ObjectLayout.ComponentSizeOf(table);
                for (int i = 0; i < length; i++)
                {
                    ulong to = data + (ulong)i * component;
                    View element = v[i];
                    if (kind < FieldKind.Reference) StoreValue(to, kind, element);
                    else if (kind == FieldKind.Struct) StoreStruct(to, elements.Type, element);
                    else Unsafe.AsRef<object>((void*)to) = Reference(element, elements.Type);
                }
                return copy;
            }

            private void StoreStruct(ulong to, string targetType, View value)
            {
                TypeKeys.Description target = Own(targetType);
                if (target == null || value.Kind != ViewKind.Object || !value.Shape.IsValueType)
                    throw Mismatch(targetType, value);
                // The same struct under the same key, with no references: its bytes.
                if (target.Key == value.Shape.Key && HasNoReferences(value.Shape))
                {
                    MemoryPrimitives.Memcpy((void*)to, (void*)(value.Address + 8), target.BaseSize - 16);
                    return;
                }
                // By name otherwise; a struct's offsets were measured in its box,
                // so its fields start 8 before the value.
                Fill(PlanFor(value.Shape, target), value, to - 8);
            }
        }

        private static void StoreValue(ulong to, FieldKind kind, View value)
        {
            switch (kind)
            {
                case FieldKind.Bool: *(bool*)to = (bool)value; return;
                case FieldKind.Char: *(char*)to = (char)value; return;
                case FieldKind.SByte: *(sbyte*)to = (sbyte)value; return;
                case FieldKind.Byte: *(byte*)to = (byte)value; return;
                case FieldKind.Int16: *(short*)to = (short)value; return;
                case FieldKind.UInt16: *(ushort*)to = (ushort)value; return;
                case FieldKind.Int32: *(int*)to = (int)value; return;
                case FieldKind.UInt32: *(uint*)to = (uint)value; return;
                case FieldKind.Int64: *(long*)to = (long)value; return;
                case FieldKind.UInt64: *(ulong*)to = (ulong)value; return;
                case FieldKind.Single: *(float*)to = (float)value; return;
                case FieldKind.Double: *(double*)to = (double)value; return;
            }
        }

        private static object Allocate(ulong table, int length, bool array)
        {
            var mt = (GcMethodTable*)table;
            void* raw = array
                ? GcHeap.AllocateArray((uint)((mt->BaseSize + (ulong)length * mt->ComponentSize + 7) & ~7UL), mt, length)
                : GcHeap.AllocateObject(mt->BaseSize, mt);
            if (raw == null) throw new OutOfMemoryException();
            nint address = (nint)raw;
            return Unsafe.As<nint, object>(ref address);
        }

        private static ulong Address(object o) => Unsafe.As<object, ulong>(ref o);

        private static Exception Mismatch(string targetType, View v)
            => new InvalidCastException("Into: a " + v.Kind.ToString() + " (" + (v.TypeName ?? "null") + ") cannot become " + targetType);
    }
}
