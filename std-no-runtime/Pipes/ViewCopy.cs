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

        // One copier and one mapper kept for the next copy (step196): their
        // tables are cleared, not made again. Taken under an atomic flag; a
        // copy that finds it taken (another thread, or a copy inside a copy)
        // makes its own.
        private static Copier s_copier;
        private static int s_copierBusy;
        private static Mapper s_mapper;
        private static int s_mapperBusy;

        public static object ToHeapValue(View v)
        {
            bool mine = System.Threading.Interlocked.CompareExchange(ref s_copierBusy, 1, 0) == 0;
            Copier c = mine ? (s_copier ??= new Copier()) : new Copier();
            try
            {
                return c.Value(v);
            }
            finally
            {
                if (mine)
                {
                    c.Reset();
                    s_copierBusy = 0;
                }
            }
        }

        private sealed class Copier
        {
            // Source object -> its copy. Holds the copies alive too: a copy is
            // reachable from here while its own fields are filled.
            private readonly Dictionary<ulong, object> _done = new Dictionary<ulong, object>();

            public void Reset()
            {
                if (_done.Count > 0) _done.Clear();
            }

            public object Value(View v)
            {
                switch (v.Kind)
                {
                    case ViewKind.Null: return null;
                    case ViewKind.Bool: return BoolBox.Of((bool)v);
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

                if (v.Shape.IsExpando)
                {
                    var bag = new Expando();
                    _done[v.Address] = bag;
                    foreach (ViewField f in v.Fields)
                        bag[f.Name] = Value(f.Value);
                    return bag;
                }
                // A type's fields: by their shapes, into an Expando of the
                // right size, names shared with the description.
                FieldShape[] fields = v.Shape.Fields;
                var e = new Expando(fields.Length);
                if (!v.IsInPlaceStruct) _done[v.Address] = e;
                for (int i = 0; i < fields.Length; i++)
                    e.AppendFresh(fields[i].Name, Value(v.FieldAt(fields[i])));
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
            RefTarget target = IntoTarget<T>.Value;
            if (target == null)
            {
                ulong key = MessageCatalog.KeyOf(typeof(T));
                TypeKeys.Description d = key == 0 ? null : TypeKeys.DescriptionOf(key);
                if (d == null)
                    throw new InvalidOperationException("Into: the target type is not in this image's catalog");
                IntoTarget<T>.Value = target = TargetOf(d.Name);
            }
            bool mine = System.Threading.Interlocked.CompareExchange(ref s_mapperBusy, 1, 0) == 0;
            Mapper m = mine ? (s_mapper ??= new Mapper()) : new Mapper();
            try
            {
                return Unsafe.As<T>(m.Reference(v, target));
            }
            finally
            {
                if (mine)
                {
                    m.Reset();
                    s_mapperBusy = 0;
                }
            }
        }

        // Into<T>'s target, found once per T.
        private static class IntoTarget<T>
        {
            public static RefTarget Value;
        }

        /// <summary>What a reference of a target type takes, resolved once (step196): not by its name per copy.</summary>
        private enum RefKind : byte { Object, String, Array, Class }

        private sealed class RefTarget
        {
            public RefKind Kind;
            public string Name;
            public TypeKeys.Description Own;   // a class or an array of this image; null when it has none
            public RefTarget Element;          // an array's elements, when they are references: made at first use
        }

        private static RefTarget TargetOf(string type)
        {
            var t = new RefTarget { Name = type };
            if (type == "System.Object") t.Kind = RefKind.Object;
            else if (type == "System.String") t.Kind = RefKind.String;
            else
            {
                t.Kind = type.EndsWith("[]") ? RefKind.Array : RefKind.Class;
                t.Own = Own(type);
            }
            return t;
        }

        /// <summary>A step of a plan: one target field from one source field.</summary>
        private sealed class Step
        {
            public FieldShape Source;          // null: the target keeps its default
            public int TargetOffset;
            public FieldKind TargetKind;
            public string TargetType;          // for a reference or a struct
            public RefTarget Ref;              // for a reference
        }

        /// <summary>How a source type's fields become a target type's, built once per pair.</summary>
        private sealed class Plan
        {
            public TypeKeys.Description Target;
            public ulong TargetTable;
            public Step[] Steps;               // only the fields the source has
            public Plan Next;                  // the source type's other plans
        }

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

        // A source type's plans hang on its shape (step196): found by reference,
        // with no key to build. A list replaced whole: a race makes a plan twice.
        private static Plan PlanFor(TypeShape source, TypeKeys.Description target)
        {
            for (Plan known = Unsafe.As<Plan>(source.IntoPlans); known != null; known = known.Next)
                if (ReferenceEquals(known.Target, target)) return known;

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
                if (step.Source == null) continue;     // the target keeps its default
                Check(source, target, tf, step);
                if (step.TargetKind == FieldKind.Reference) step.Ref = TargetOf(tf.Type);
                steps.Add(step);
            }
            var plan = new Plan { Target = target, TargetTable = table, Steps = steps.ToArray() };
            plan.Next = Unsafe.As<Plan>(source.IntoPlans);
            source.IntoPlans = plan;
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

            public void Reset()
            {
                if (_done.Count > 0) _done.Clear();
                _copier?.Reset();
            }

            // A source value into a reference of the target's declared type.
            public object Reference(View v, RefTarget target)
            {
                ViewKind kind = v.Kind;
                if (kind == ViewKind.Null) return null;
                string targetType = target.Name;
                switch (target.Kind)
                {
                    case RefKind.Object:
                        return (_copier ??= new Copier()).Value(v);
                    case RefKind.String:
                    {
                        if (kind != ViewKind.String) throw Mismatch(targetType, v);
                        if (_done.TryGetValue(v.Address, out object s)) return s;
                        string text = (string)v;
                        _done[v.Address] = text;
                        return text;
                    }
                    case RefKind.Array:
                        return Array(v, target);
                }
                if (kind != ViewKind.Object || v.IsInPlaceStruct) throw Mismatch(targetType, v);
                if (_done.TryGetValue(v.Address, out object seen)) return seen;

                if (target.Own == null) throw new InvalidOperationException("Into: " + targetType + " is not in this image's catalog");
                Plan plan = PlanFor(v.Shape, target.Own);
                object copy = Allocate(plan.TargetTable, 0, false);
                _done[v.Address] = copy;
                Fill(plan, v, Address(copy));
                return copy;
            }

            private void Fill(Plan plan, View source, ulong target)
            {
                Step[] steps = plan.Steps;
                for (int i = 0; i < steps.Length; i++)
                {
                    Step step = steps[i];
                    ulong to = target + (ulong)step.TargetOffset;
                    View value = source.FieldAt(step.Source);
                    if (step.TargetKind < FieldKind.Reference)
                        StoreValue(to, step.TargetKind, value);
                    else if (step.TargetKind == FieldKind.Struct)
                        StoreStruct(to, step.TargetType, value);
                    else
                        Unsafe.AsRef<object>((void*)to) = Reference(value, step.Ref);
                }
            }

            private object Array(View v, RefTarget target)
            {
                string targetType = target.Name;
                if (v.Kind != ViewKind.Array) throw Mismatch(targetType, v);
                if (_done.TryGetValue(v.Address, out object seen)) return seen;
                TypeKeys.Description arrayType = target.Own;
                if (arrayType == null || arrayType.Fields.Length != 1 || !TypeKeys.TryTable(arrayType.Key, out ulong table))
                    throw new InvalidOperationException("Into: " + targetType + " is not in this image's catalog");
                // The array's own description: where its elements start and what
                // they are (an enum array's elements are its underlying numbers).
                TypeKeys.Field elements = arrayType.Fields[0];
                FieldKind kind = OwnKind(elements.Type);
                RefTarget elementTarget = kind == FieldKind.Reference ? (target.Element ??= TargetOf(elements.Type)) : null;
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
                    else Unsafe.AsRef<object>((void*)to) = Reference(element, elementTarget);
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
