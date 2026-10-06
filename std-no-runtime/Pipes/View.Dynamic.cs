using System;
using System.Collections;
using Microsoft.CSharp.RuntimeBinder;
using SharpOS.Std.Dynamic;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    // `dynamic` over a view: it reads like the writer's object. A number, bool,
    // char or string comes out as a value (a string as a copy), an enum as its
    // number; an object, array or struct comes out as a view — valid while the
    // region is, like the one it came from. Writes take values only, as through
    // the indexer. A call site keeps the field found for a type of the pipe's
    // description (IDynamicShape) and does not look the name up again.
    public readonly unsafe partial struct View : IDynamicObject, IDynamicShape
    {
        /// <summary>What `dynamic` gets for this view: a value, a copied string, null, or the view itself.</summary>
        internal object ToDynamic()
        {
            Check();
            switch (_shape)
            {
                case Null: return null;
                // (object): with `this` as the other branch the conditional is a View, and the
                // implicit View(string) would wrap the copy back into a detached view.
                case Obj: return _type.IsString ? (object)CopyText() : this;
                case InPlace: return this;
            }
            if (_text != null) return _text;
            switch (_value)
            {
                case FieldKind.Bool: return Integer() != 0;
                case FieldKind.Char: return (char)Integer();
                case FieldKind.SByte: return (sbyte)Integer();
                case FieldKind.Byte: return (byte)Integer();
                case FieldKind.Int16: return (short)Integer();
                case FieldKind.UInt16: return (ushort)Integer();
                case FieldKind.Int32: return (int)Integer();
                case FieldKind.UInt32: return (uint)Integer();
                case FieldKind.Int64: return Integer();
                case FieldKind.UInt64: return (ulong)Integer();
                case FieldKind.Single: return (float)Float();
                case FieldKind.Double: return Float();
            }
            return null;   // a detached null
        }

        /// <summary>A value as a view can write it.</summary>
        private static View FromValue(object value, string what)
        {
            switch (value)
            {
                case null: return new View(null, 0, null, FieldKind.Reference, Detached, null);
                case View v: return v;
                case bool b: return b;
                case char c: return c;
                case sbyte sb: return sb;
                case byte by: return by;
                case short s: return s;
                case ushort us: return us;
                case int i: return i;
                case uint ui: return ui;
                case long l: return l;
                case ulong ul: return ul;
                case float f: return f;
                case double d: return d;
                case string text: return text;
            }
            throw new InvalidOperationException("'" + what + "': a view writes numbers, bool, char and null only — an object of the heap cannot go into a region");
        }

        private bool HasFields => (_shape == Obj && !_type.IsArray && !_type.IsString) || _shape == InPlace;

        private RuntimeBinderException NoMember(string name)
            => new RuntimeBinderException("'" + (_type != null ? (_type.IsExpando ? "SharpOS.Std.Pipes.Expando" : _type.Name) : Kind.ToString())
                                          + "' does not contain a definition for '" + name + "'");

        bool IDynamicObject.TryGetMember(string name, out object value)
        {
            Check();
            value = null;
            if (_shape == Obj && _type.IsExpando)
            {
                int i = ExpandoIndex(name);
                if (i < 0) throw NoMember(name);
                value = ExpandoValue(i).ToDynamic();
                return true;
            }
            if (HasFields)
            {
                FieldShape f = _type.Field(name);
                if (f == null) throw NoMember(name);
                value = OfSlot(_scope, _at + (ulong)f.Offset, f).ToDynamic();
                return true;
            }
            if (name == "Length" && (Kind == ViewKind.Array || Kind == ViewKind.String))
            {
                value = Length;
                return true;
            }
            return false;
        }

        bool IDynamicObject.TrySetMember(string name, object value)
        {
            Check();
            if (_shape == Obj && _type.IsExpando)
            {
                if (ExpandoIndex(name) < 0) throw NoMember(name);
                this[name] = FromValue(value, name);
                return true;
            }
            if (!HasFields) return false;
            if (_type.Field(name) == null) throw NoMember(name);
            this[name] = FromValue(value, name);
            return true;
        }

        bool IDynamicObject.TryGetIndex(object[] indexes, out object value)
        {
            Check();
            value = null;
            if (indexes.Length != 1) return false;
            if (indexes[0] is string name && _shape == Obj && _type.IsExpando)
                return ((IDynamicObject)this).TryGetMember(name, out value);
            if (Kind != ViewKind.Array && Kind != ViewKind.String) return false;
            value = this[Index(indexes[0])].ToDynamic();
            return true;
        }

        bool IDynamicObject.TrySetIndex(object[] indexes, object value)
        {
            Check();
            if (indexes.Length != 1) return false;
            if (indexes[0] is string name && _shape == Obj && _type.IsExpando)
                return ((IDynamicObject)this).TrySetMember(name, value);
            if (Kind != ViewKind.Array) return false;
            int i = Index(indexes[0]);
            this[i] = FromValue(value, "[" + i.ToString() + "]");
            return true;
        }

        private static int Index(object index)
            => (int)DynamicRuntime.ConvertValue(index, typeof(int), isExplicit: false);

        bool IDynamicObject.TryInvokeMember(string name, object[] args, out object result)
        {
            result = null;
            if (name == "ToString" && args.Length == 0)
            {
                result = ToString();
                return true;
            }
            return false;   // the view's own methods (Has, Into, …)
        }

        bool IDynamicObject.TryConvert(Type type, bool isExplicit, out object result)
        {
            Check();
            result = null;
            if (type == typeof(View) || type == typeof(object)) { result = this; return true; }
            if (type == typeof(Expando))
            {
                if (!HasFields && !(_shape == Obj && _type.IsExpando)) return false;
                result = ToExpando();
                return true;
            }
            if (type == typeof(IEnumerable))
            {
                if (Kind != ViewKind.Array && Kind != ViewKind.String) return false;
                result = new DynamicElements(this);
                return true;
            }
            if (_shape == Obj && _type.IsString || _text != null || _shape == Null)
            {
                if (type != typeof(string)) return false;
                result = _shape == Null ? null : CopyText();
                return true;
            }
            if (!IsValue) return false;
            result = DynamicRuntime.ConvertValue(ToDynamic(), type, isExplicit);
            return true;
        }

        // An Expando in a region has entries, not fields: no shape, asked by name.
        // An array or a string has one member, Length: a shape too.
        object IDynamicShape.Shape
            => (HasFields && !(_shape == Obj && _type.IsExpando)) || (_shape == Obj && (_type.IsArray || _type.IsString)) ? _type : null;

        private static object s_lengthCell;
        private static object LengthCell => s_lengthCell ??= new object();

        bool IDynamicShape.TryCell(string name, out object cell)
        {
            if (_type.IsArray || _type.IsString)
            {
                cell = name == "Length" ? LengthCell : null;
                return cell != null;
            }
            FieldShape f = _type.Field(name);
            cell = f;
            return f != null;
        }

        object IDynamicShape.GetCell(object cell)
        {
            if (ReferenceEquals(cell, LengthCell)) return Length;
            return FieldAt((FieldShape)cell).ToDynamic();
        }

        void IDynamicShape.SetCell(object cell, object value)
        {
            Check();
            if (ReferenceEquals(cell, LengthCell))
                throw new RuntimeBinderException("Property or indexer '" + _type.Name + ".Length' cannot be assigned to -- it is read only");
            var f = (FieldShape)cell;
            Write(_at + (ulong)f.Offset, f.Kind, FromValue(value, f.Name), f.Name);
        }

        // `foreach (var x in v.Tags)`: the elements as `dynamic` sees them.
        private sealed class DynamicElements : IEnumerable, IEnumerator
        {
            private readonly View _array;
            private int _index = -1;

            internal DynamicElements(View array) => _array = array;

            public IEnumerator GetEnumerator() => new DynamicElements(_array);

            public object Current => _array[_index].ToDynamic();

            public bool MoveNext() => ++_index < _array.Length;

            public void Reset() => _index = -1;
        }
    }
}
