using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>What a <see cref="View"/> shows.</summary>
    public enum ViewKind : byte
    {
        Null,
        Bool,
        Char,
        Integer,
        Float,
        String,
        Object,
        Array,
    }

    /// <summary>
    /// A region a view reads: the block, its pipe's shapes, and whether it is
    /// still there. One per received message; a view checks it on every use.
    /// </summary>
    public sealed unsafe class ViewScope
    {
        internal byte* Block;
        internal ulong Length;
        internal readonly RegionShapes Shapes;
        internal bool Alive = true;
        internal string GoneBecause;

        // Bumped when a loop reuses the scope for its next message (step195):
        // a view keeps the generation it was made in, and one of an earlier
        // message refuses like a view of an ended scope.
        internal int Generation;

        internal ViewScope(byte* block, ulong length, RegionShapes shapes)
        {
            Block = block;
            Length = length;
            Shapes = shapes;
        }

        internal void Reset(byte* block, ulong length)
        {
            Block = block;
            Length = length;
            Alive = true;
            GoneBecause = null;
            Generation++;
        }

        internal void End(string because)
        {
            Alive = false;
            GoneBecause = because;
        }

        /// <summary>The root object of the region.</summary>
        internal View Root => View.OfObject(this, (ulong)Block + Region.HeaderSize);
    }

    /// <summary>
    /// An object, array, string or value inside a region that was not translated
    /// — a message of a type this image may not have (pipe spec Р33–Р35).
    /// Reading is by name and index; values come out as values, strings as
    /// copies, nested objects as views again.
    /// </summary>
    /// <remarks>
    /// Only data is visible: no methods, no casts to a class. A class and an
    /// Expando read the same way. Writing is of values only — numbers, bool,
    /// char, enums — and of null into a reference; a heap object cannot be put
    /// into a region (that would rebuild the block): take ToExpando or Into,
    /// change, send a copy.
    ///
    /// A view is valid while its region is: after Dispose, Move or the next step
    /// of the loop that produced it, every use throws ObjectDisposedException.
    /// It also carries a plain value — what an implicit conversion from a number
    /// makes — so that <c>v["Seen"] = true</c> reads as written.
    /// </remarks>
    public readonly unsafe partial struct View : IEnumerable<View>
    {
        // _at: an object's table word; for a value, its address; for a struct in
        // place, the address its fields' offsets count from (value - 8).
        private readonly ViewScope _scope;
        private readonly ulong _at;
        private readonly TypeShape _type;
        private readonly FieldKind _value;
        private readonly byte _shape;      // 0 null, 1 value, 2 object, 3 struct in place, 4 detached value
        private readonly TypeShape _enum;
        private readonly ulong _bits;      // a detached value
        private readonly string _text;     // a detached string (for comparisons and null writes)
        private readonly int _generation;  // the scope's, when the view was made

        private const byte Null = 0, Value = 1, Obj = 2, InPlace = 3, Detached = 4;

        private View(ViewScope scope, ulong at, TypeShape type, FieldKind value, byte shape, TypeShape enumType,
                     ulong bits = 0, string text = null)
        {
            _scope = scope;
            _at = at;
            _type = type;
            _value = value;
            _shape = shape;
            _enum = enumType;
            _bits = bits;
            _text = text;
            _generation = scope?.Generation ?? 0;
        }

        // ---- construction ----

        internal static View OfObject(ViewScope scope, ulong objectAt)
        {
            TypeShape t = scope.Shapes[*(ulong*)objectAt];
            if (t == null)
                throw new InvalidOperationException("a record of the region has no description");
            // A box of a number shows as the number.
            if (t.BoxedValue != null)
                return new View(scope, objectAt + (ulong)t.BoxedValue.Offset, null, t.BoxedValue.Kind, Value, t.BoxedValue.Enum);
            return new View(scope, objectAt, t, FieldKind.Reference, Obj, null);
        }

        private static View OfSlot(ViewScope scope, ulong at, FieldShape f)
        {
            switch (f.Kind)
            {
                case FieldKind.Reference:
                    ulong offset = *(ulong*)at;
                    return offset == 0 ? new View(scope, 0, null, FieldKind.Reference, Null, null)
                                       : OfObject(scope, (ulong)scope.Block + offset);
                case FieldKind.Struct:
                    return new View(scope, at - 8, f.Struct, FieldKind.Struct, InPlace, null);
                default:
                    return new View(scope, at, null, f.Kind, Value, f.Enum);
            }
        }

        private void Check()
        {
            if (_scope != null && (!_scope.Alive || _scope.Generation != _generation))
                throw new ObjectDisposedException("View", _scope.Generation != _generation
                    ? "the region is gone: the loop moved past it"
                    : _scope.GoneBecause ?? "the region is gone");
        }

        // ---- what it is ----

        public ViewKind Kind
        {
            get
            {
                Check();
                switch (_shape)
                {
                    case Null: return ViewKind.Null;
                    case Obj:
                        return _type.IsString ? ViewKind.String : _type.IsArray ? ViewKind.Array : ViewKind.Object;
                    case InPlace: return ViewKind.Object;
                    case Detached:
                        if (_text != null) return ViewKind.String;
                        return _value == FieldKind.Reference ? ViewKind.Null : KindOf(_value);
                    default: return KindOf(_value);
                }
            }
        }

        private static ViewKind KindOf(FieldKind k)
            => k == FieldKind.Bool ? ViewKind.Bool
             : k == FieldKind.Char ? ViewKind.Char
             : k == FieldKind.Single || k == FieldKind.Double ? ViewKind.Float
             : ViewKind.Integer;

        public bool IsNull => Kind == ViewKind.Null;

        /// <summary>The type's full name as the writer declared it; for a value, its number's type (or its enum's).</summary>
        public string TypeName
        {
            get
            {
                Check();
                if (_shape == Obj || _shape == InPlace) return _type.Name;
                if (_shape == Value && _enum != null) return _enum.Name;
                if (_shape == Null) return null;
                return _text != null ? "System.String" : PrimitiveName(_value);
            }
        }

        /// <summary>Elements of an array, characters of a string; 0 otherwise.</summary>
        public int Length
        {
            get
            {
                Check();
                if (_shape == Obj && (_type.IsArray || _type.IsString)) return *(int*)(_at + 8);
                if (_shape == Obj && _type.IsExpando) return ExpandoCount();
                if (_text != null) return _text.Length;
                return 0;
            }
        }

        /// <summary>Whether the object has a field (or an Expando an entry) of this name.</summary>
        public bool Has(string name)
        {
            Check();
            if (_shape == Obj && _type.IsExpando) return ExpandoIndex(name) >= 0;
            return (_shape == Obj || _shape == InPlace) && _type.Field(name) != null;
        }

        // ---- reading ----

        /// <summary>A field by name. Missing name: KeyNotFoundException.</summary>
        public View this[string name]
        {
            get
            {
                Check();
                if (_shape == Obj && _type.IsExpando)
                {
                    int i = ExpandoIndex(name);
                    if (i < 0) throw new KeyNotFoundException("no field '" + name + "' in the Expando");
                    return ExpandoValue(i);
                }
                if (_shape != Obj && _shape != InPlace)
                    throw new InvalidOperationException("a " + Kind.ToString() + " has no fields");
                FieldShape f = _type.Field(name);
                if (f == null) throw new KeyNotFoundException("no field '" + name + "' in " + _type.Name);
                return OfSlot(_scope, _at + (ulong)f.Offset, f);
            }
            set
            {
                Check();
                if (_shape == Obj && _type.IsExpando)
                {
                    int i = ExpandoIndex(name);
                    if (i < 0) throw new KeyNotFoundException("no field '" + name + "' in the Expando: a view cannot add one");
                    View current = ExpandoValue(i);
                    if (current._shape != Value)
                        throw new InvalidOperationException("field '" + name + "' of the Expando holds an object: a view writes values only");
                    WriteValue(current._at, current._value, value, name);
                    return;
                }
                if (_shape != Obj && _shape != InPlace)
                    throw new InvalidOperationException("a " + Kind.ToString() + " has no fields");
                FieldShape f = _type.Field(name);
                if (f == null) throw new KeyNotFoundException("no field '" + name + "' in " + _type.Name);
                Write(_at + (ulong)f.Offset, f.Kind, value, name);
            }
        }

        /// <summary>An element of an array, or a character of a string.</summary>
        public View this[int index]
        {
            get
            {
                Check();
                if (_text != null) return (char)_text[index];
                if (_shape != Obj || !(_type.IsArray || _type.IsString))
                    throw new InvalidOperationException("a " + Kind.ToString() + " has no elements");
                ulong slot = ElementAt(index);
                return OfSlot(_scope, slot, _type.Elements);
            }
            set
            {
                Check();
                if (_shape != Obj || !_type.IsArray)
                    throw new InvalidOperationException("a " + Kind.ToString() + " has no elements to write");
                Write(ElementAt(index), _type.Elements.Kind, value, "[" + index.ToString() + "]");
            }
        }

        private ulong ElementAt(int index)
        {
            int length = *(int*)(_at + 8);
            if ((uint)index >= (uint)length) throw new IndexOutOfRangeException();
            return _at + (ulong)_type.Elements.Offset + (ulong)index * _type.ComponentSize;
        }

        /// <summary>The fields, in declaration order (an Expando's entries in theirs).</summary>
        public IEnumerable<ViewField> Fields
        {
            get
            {
                Check();
                var list = new List<ViewField>();
                if (_shape == Obj && _type.IsExpando)
                {
                    int count = ExpandoCount();
                    for (int i = 0; i < count; i++)
                        list.Add(new ViewField(ExpandoName(i), ExpandoValue(i)));
                }
                else if (_shape == Obj || _shape == InPlace)
                {
                    foreach (FieldShape f in _type.Fields)
                        list.Add(new ViewField(f.Name, OfSlot(_scope, _at + (ulong)f.Offset, f)));
                }
                return list;
            }
        }

        /// <summary>An array's elements, a string's characters; nothing for anything else.</summary>
        public IEnumerator<View> GetEnumerator()
        {
            int length = Length;
            for (int i = 0; i < length; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ---- an Expando, read by its own fields ----

        // Its own fields, read directly: this view's indexer looks up entries.
        private int ExpandoCount() => *(int*)(_at + (ulong)_type.Field("_count").Offset);

        private View ExpandoArray(string field) => OfSlot(_scope, _at + (ulong)_type.Field(field).Offset, _type.Field(field));

        private int ExpandoIndex(string name)
        {
            int count = ExpandoCount();
            View names = ExpandoArray("_names");
            for (int i = 0; i < count; i++)
                if (names[i].TextEquals(name)) return i;
            return -1;
        }

        private string ExpandoName(int i) => (string)ExpandoArray("_names")[i];

        private View ExpandoValue(int i) => ExpandoArray("_values")[i];

        // ---- values out ----

        private long Integer()
        {
            ulong a = _shape == Detached ? 0 : _at;
            if (_shape == Detached)
                return _value == FieldKind.Single || _value == FieldKind.Double ? (long)Float() : (long)_bits;
            switch (_value)
            {
                case FieldKind.Bool: return *(byte*)a;
                case FieldKind.Char: return *(char*)a;
                case FieldKind.SByte: return *(sbyte*)a;
                case FieldKind.Byte: return *(byte*)a;
                case FieldKind.Int16: return *(short*)a;
                case FieldKind.UInt16: return *(ushort*)a;
                case FieldKind.Int32: return *(int*)a;
                case FieldKind.UInt32: return *(uint*)a;
                case FieldKind.Int64: return *(long*)a;
                case FieldKind.UInt64: return (long)*(ulong*)a;
                case FieldKind.Single: return (long)*(float*)a;
                case FieldKind.Double: return (long)*(double*)a;
            }
            throw new InvalidCastException("not a number: " + Kind.ToString());
        }

        private double Float()
        {
            if (_shape == Detached)
            {
                if (_value == FieldKind.Double) return BitConverter.Int64BitsToDouble((long)_bits);
                if (_value == FieldKind.Single) return BitConverter.Int32BitsToSingle((int)_bits);
                return _value == FieldKind.UInt64 ? (double)_bits : (double)(long)_bits;
            }
            if (_value == FieldKind.Single) return *(float*)_at;
            if (_value == FieldKind.Double) return *(double*)_at;
            if (_value == FieldKind.UInt64) return *(ulong*)_at;
            return Integer();
        }

        private bool IsNumber => (_shape == Value || (_shape == Detached && _text == null && _value != FieldKind.Reference))
                                 && _value != FieldKind.Bool;
        private bool IsFloat => _value == FieldKind.Single || _value == FieldKind.Double;

        private void RequireNumber()
        {
            Check();
            if (!IsNumber) throw new InvalidCastException("not a number: " + Kind.ToString());
        }

        private bool TextEquals(string s)
        {
            if (_text != null) return _text == s;
            if (_shape == Null) return s == null;
            if (_shape != Obj || !_type.IsString || s == null) return false;
            int length = *(int*)(_at + 8);
            if (length != s.Length) return false;
            char* chars = (char*)(_at + 12);
            for (int i = 0; i < length; i++)
                if (chars[i] != s[i]) return false;
            return true;
        }

        private string CopyText()
        {
            if (_text != null) return _text;
            if (_shape == Null) return null;
            if (_shape != Obj || !_type.IsString)
                throw new InvalidCastException("not a string: " + Kind.ToString());
            return new string(new ReadOnlySpan<char>((void*)(_at + 12), *(int*)(_at + 8)));
        }

        // ---- writing ----

        private void Write(ulong at, FieldKind kind, View value, string what)
        {
            if (kind == FieldKind.Reference)
            {
                value.Check();
                if (value._shape == Null || (value._shape == Detached && value._text == null && value._value == FieldKind.Reference))
                {
                    *(ulong*)at = 0;
                    return;
                }
                throw new InvalidOperationException("'" + what + "' holds a reference: a view writes only null there — an object of the heap cannot go into a region");
            }
            if (kind == FieldKind.Struct)
                throw new InvalidOperationException("'" + what + "' is a struct: write its fields one by one");
            WriteValue(at, kind, value, what);
        }

        private static void WriteValue(ulong at, FieldKind kind, View value, string what)
        {
            value.Check();
            if (value._text != null || value._shape == Obj || value._shape == InPlace || value._shape == Null
                || (value._shape == Detached && value._value == FieldKind.Reference))
                throw new InvalidOperationException("'" + what + "' holds a " + PrimitiveName(kind) + ": cannot write a " + value.Kind.ToString());

            if (kind == FieldKind.Bool)
            {
                if (value._value != FieldKind.Bool) throw Mismatch(what, kind, value);
                *(byte*)at = value.Integer() != 0 ? (byte)1 : (byte)0;
                return;
            }
            if (value._value == FieldKind.Bool) throw Mismatch(what, kind, value);

            if (kind == FieldKind.Single || kind == FieldKind.Double)
            {
                double d = value.Float();
                if (kind == FieldKind.Single) *(float*)at = (float)d;
                else *(double*)at = d;
                return;
            }
            if (value.IsFloat) throw Mismatch(what, kind, value);

            long v = value.Integer();
            bool unsignedBig = value._value == FieldKind.UInt64 && v < 0;
            switch (kind)
            {
                case FieldKind.Char: if (unsignedBig || v < 0 || v > char.MaxValue) throw Range(what, kind, v); *(char*)at = (char)v; return;
                case FieldKind.SByte: if (unsignedBig || v < sbyte.MinValue || v > sbyte.MaxValue) throw Range(what, kind, v); *(sbyte*)at = (sbyte)v; return;
                case FieldKind.Byte: if (unsignedBig || v < 0 || v > byte.MaxValue) throw Range(what, kind, v); *(byte*)at = (byte)v; return;
                case FieldKind.Int16: if (unsignedBig || v < short.MinValue || v > short.MaxValue) throw Range(what, kind, v); *(short*)at = (short)v; return;
                case FieldKind.UInt16: if (unsignedBig || v < 0 || v > ushort.MaxValue) throw Range(what, kind, v); *(ushort*)at = (ushort)v; return;
                case FieldKind.Int32: if (unsignedBig || v < int.MinValue || v > int.MaxValue) throw Range(what, kind, v); *(int*)at = (int)v; return;
                case FieldKind.UInt32: if (unsignedBig || v < 0 || v > uint.MaxValue) throw Range(what, kind, v); *(uint*)at = (uint)v; return;
                case FieldKind.Int64: if (unsignedBig) throw Range(what, kind, v); *(long*)at = v; return;
                case FieldKind.UInt64: if (v < 0 && !unsignedBig) throw Range(what, kind, v); *(ulong*)at = (ulong)v; return;
            }
            throw Mismatch(what, kind, value);
        }

        private static Exception Mismatch(string what, FieldKind kind, View value)
            => new InvalidCastException("'" + what + "' holds a " + PrimitiveName(kind) + ": cannot write a " + PrimitiveName(value._value));

        private static Exception Range(string what, FieldKind kind, long v)
            => new OverflowException("'" + what + "' holds a " + PrimitiveName(kind) + ": " + v.ToString() + " does not fit");

        internal static string PrimitiveName(FieldKind k)
        {
            switch (k)
            {
                case FieldKind.Bool: return "System.Boolean";
                case FieldKind.Char: return "System.Char";
                case FieldKind.SByte: return "System.SByte";
                case FieldKind.Byte: return "System.Byte";
                case FieldKind.Int16: return "System.Int16";
                case FieldKind.UInt16: return "System.UInt16";
                case FieldKind.Int32: return "System.Int32";
                case FieldKind.UInt32: return "System.UInt32";
                case FieldKind.Int64: return "System.Int64";
                case FieldKind.UInt64: return "System.UInt64";
                case FieldKind.Single: return "System.Single";
                case FieldKind.Double: return "System.Double";
                default: return "reference";
            }
        }

        // ---- detached values: what a view carries to be written or compared ----

        private static View Of(FieldKind kind, ulong bits) => new View(null, 0, null, kind, Detached, null, bits);

        public static implicit operator View(bool v) => Of(FieldKind.Bool, v ? 1UL : 0UL);
        public static implicit operator View(char v) => Of(FieldKind.Char, v);
        public static implicit operator View(sbyte v) => Of(FieldKind.SByte, (ulong)(long)v);
        public static implicit operator View(byte v) => Of(FieldKind.Byte, v);
        public static implicit operator View(short v) => Of(FieldKind.Int16, (ulong)(long)v);
        public static implicit operator View(ushort v) => Of(FieldKind.UInt16, v);
        public static implicit operator View(int v) => Of(FieldKind.Int32, (ulong)(long)v);
        public static implicit operator View(uint v) => Of(FieldKind.UInt32, v);
        public static implicit operator View(long v) => Of(FieldKind.Int64, (ulong)v);
        public static implicit operator View(ulong v) => Of(FieldKind.UInt64, v);
        public static implicit operator View(float v) => Of(FieldKind.Single, (ulong)(uint)BitConverter.SingleToInt32Bits(v));
        public static implicit operator View(double v) => Of(FieldKind.Double, (ulong)BitConverter.DoubleToInt64Bits(v));

        /// <summary>A string to compare with; null to write into a reference.</summary>
        public static implicit operator View(string v)
            => v == null ? new View(null, 0, null, FieldKind.Reference, Detached, null)
                         : new View(null, 0, null, FieldKind.Reference, Detached, null, 0, v);

        // ---- values in ----

        public static explicit operator bool(View v)
        {
            v.Check();
            if (v._value != FieldKind.Bool || !(v._shape == Value || v._shape == Detached))
                throw new InvalidCastException("not a bool: " + v.Kind.ToString());
            return v.Integer() != 0;
        }

        public static explicit operator char(View v) { v.RequireNumberOrChar(); return (char)v.Integer(); }
        public static explicit operator sbyte(View v) { v.RequireNumber(); return (sbyte)v.Integer(); }
        public static explicit operator byte(View v) { v.RequireNumber(); return (byte)v.Integer(); }
        public static explicit operator short(View v) { v.RequireNumber(); return (short)v.Integer(); }
        public static explicit operator ushort(View v) { v.RequireNumber(); return (ushort)v.Integer(); }
        public static explicit operator int(View v) { v.RequireNumber(); return (int)v.Integer(); }
        public static explicit operator uint(View v) { v.RequireNumber(); return (uint)v.Integer(); }
        public static explicit operator long(View v) { v.RequireNumber(); return v.Integer(); }
        public static explicit operator ulong(View v) { v.RequireNumber(); return (ulong)v.Integer(); }
        public static explicit operator float(View v) { v.RequireNumber(); return (float)v.Float(); }
        public static explicit operator double(View v) { v.RequireNumber(); return v.Float(); }

        /// <summary>The string, copied into this image's heap; null for a null reference.</summary>
        public static explicit operator string(View v)
        {
            v.Check();
            return v.CopyText();
        }

        private void RequireNumberOrChar()
        {
            Check();
            if (!IsNumber) throw new InvalidCastException("not a char: " + Kind.ToString());
        }

        /// <summary>A deep copy into this image's heap: an object becomes an Expando.</summary>
        public static implicit operator Expando(View v) => v.ToExpando();

        /// <summary>A deep copy into this image's heap: objects become Expandos, arrays of objects object[].</summary>
        public Expando ToExpando()
        {
            Check();
            object copy = ViewCopy.ToHeapValue(this);
            if (copy == null) return null;
            if (copy is Expando e) return e;
            throw new InvalidCastException("a " + Kind.ToString() + " is not an object: it has no Expando");
        }

        /// <summary>This object laid out by name into <typeparamref name="T"/>, in this image's heap.</summary>
        public T Into<T>() where T : class
        {
            Check();
            return ViewCopy.Into<T>(this);
        }

        // ---- comparisons ----

        public static bool operator ==(View a, long b) => a.IsNumber && (a.IsFloat ? a.Float() == b : a.Integer() == b);
        public static bool operator !=(View a, long b) => !(a == b);
        public static bool operator ==(long b, View a) => a == b;
        public static bool operator !=(long b, View a) => !(a == b);
        public static bool operator ==(View a, double b) => a.IsNumber && a.Float() == b;
        public static bool operator !=(View a, double b) => !(a == b);
        public static bool operator ==(double b, View a) => a == b;
        public static bool operator !=(double b, View a) => !(a == b);
        public static bool operator ==(View a, bool b) { a.Check(); return a._value == FieldKind.Bool && (a._shape == Value || a._shape == Detached) && (a.Integer() != 0) == b; }
        public static bool operator !=(View a, bool b) => !(a == b);
        public static bool operator ==(View a, string b) { a.Check(); return a.TextEquals(b); }
        public static bool operator !=(View a, string b) => !(a == b);
        public static bool operator ==(string b, View a) => a == b;
        public static bool operator !=(string b, View a) => !(a == b);

        public static bool operator ==(View a, View b)
        {
            a.Check();
            b.Check();
            if (a.IsNumber && b.IsNumber)
                return a.IsFloat || b.IsFloat ? a.Float() == b.Float() : a.Integer() == b.Integer();
            ViewKind ka = a.Kind, kb = b.Kind;
            if (ka == ViewKind.Null || kb == ViewKind.Null) return ka == kb;
            if (ka == ViewKind.Bool && kb == ViewKind.Bool) return (a.Integer() != 0) == (b.Integer() != 0);
            if (ka == ViewKind.String && kb == ViewKind.String) return a.CopyText() == b.CopyText();
            return a._scope == b._scope && a._generation == b._generation && a._at == b._at && a._shape == b._shape;
        }

        public static bool operator !=(View a, View b) => !(a == b);

        public static bool operator <(View a, long b) { a.RequireNumber(); return a.IsFloat ? a.Float() < b : a.Integer() < b; }
        public static bool operator >(View a, long b) { a.RequireNumber(); return a.IsFloat ? a.Float() > b : a.Integer() > b; }
        public static bool operator <=(View a, long b) { a.RequireNumber(); return a.IsFloat ? a.Float() <= b : a.Integer() <= b; }
        public static bool operator >=(View a, long b) { a.RequireNumber(); return a.IsFloat ? a.Float() >= b : a.Integer() >= b; }
        public static bool operator <(long b, View a) => a > b;
        public static bool operator >(long b, View a) => a < b;
        public static bool operator <=(long b, View a) => a >= b;
        public static bool operator >=(long b, View a) => a <= b;
        public static bool operator <(View a, double b) { a.RequireNumber(); return a.Float() < b; }
        public static bool operator >(View a, double b) { a.RequireNumber(); return a.Float() > b; }
        public static bool operator <=(View a, double b) { a.RequireNumber(); return a.Float() <= b; }
        public static bool operator >=(View a, double b) { a.RequireNumber(); return a.Float() >= b; }
        public static bool operator <(View a, View b) { a.RequireNumber(); b.RequireNumber(); return a.Float() < b.Float(); }
        public static bool operator >(View a, View b) { a.RequireNumber(); b.RequireNumber(); return a.Float() > b.Float(); }
        public static bool operator <=(View a, View b) { a.RequireNumber(); b.RequireNumber(); return a.Float() <= b.Float(); }
        public static bool operator >=(View a, View b) { a.RequireNumber(); b.RequireNumber(); return a.Float() >= b.Float(); }

        public static bool operator true(View v) => (bool)v;
        public static bool operator false(View v) => !(bool)v;
        public static bool operator !(View v) => !(bool)v;

        public override bool Equals(object obj) => obj is View other && this == other;

        public override int GetHashCode() => (int)_at ^ (int)_bits ^ _shape;

        // ---- text ----

        /// <summary>A value as text, a string as itself, an object briefly by its description.</summary>
        public override string ToString()
        {
            Check();
            var text = new StringBuilder();
            Append(text, 2);
            return text.ToString();
        }

        private void Append(StringBuilder text, int depth)
        {
            switch (Kind)
            {
                case ViewKind.Null: text.Append("null"); return;
                case ViewKind.Bool: text.Append(Integer() != 0 ? "true" : "false"); return;
                case ViewKind.Char: text.Append((char)Integer()); return;
                case ViewKind.Float: text.Append(Float().ToString()); return;
                case ViewKind.Integer:
                    string member = _enum?.EnumName(Integer());
                    if (member != null) text.Append(member);
                    else if (_value == FieldKind.UInt64) text.Append(((ulong)Integer()).ToString());
                    else text.Append(Integer().ToString());
                    return;
                case ViewKind.String: text.Append(CopyText()); return;
                case ViewKind.Array:
                    text.Append(_type.Name.Substring(0, _type.Name.Length - 2)).Append('[').Append(Length.ToString()).Append(']');
                    if (depth <= 0) return;
                    text.Append(" { ");
                    for (int i = 0; i < Length && i < 8; i++)
                    {
                        if (i > 0) text.Append(", ");
                        this[i].AppendNested(text, depth - 1);
                    }
                    if (Length > 8) text.Append(", …");
                    text.Append(" }");
                    return;
                default:
                    text.Append(_type.IsExpando ? "Expando" : _type.Name);
                    if (depth <= 0) { text.Append(" { … }"); return; }
                    text.Append(" { ");
                    bool first = true;
                    foreach (ViewField f in Fields)
                    {
                        if (!first) text.Append(", ");
                        first = false;
                        text.Append(f.Name).Append(" = ");
                        f.Value.AppendNested(text, depth - 1);
                    }
                    text.Append(" }");
                    return;
            }
        }

        private void AppendNested(StringBuilder text, int depth)
        {
            if (Kind == ViewKind.String)
            {
                text.Append('"').Append(CopyText()).Append('"');
                return;
            }
            Append(text, depth);
        }

        /// <summary>A field by its shape, as a plan found it: no lookup by name.</summary>
        internal View FieldAt(FieldShape f)
        {
            Check();
            return OfSlot(_scope, _at + (ulong)f.Offset, f);
        }

        internal ViewScope Scope => _scope;
        internal ulong Address => _at;
        internal TypeShape Shape => _type;
        internal FieldKind ValueKind => _value;
        internal bool IsInPlaceStruct => _shape == InPlace;
        internal bool IsValue => _shape == Value || (_shape == Detached && _text == null && _value != FieldKind.Reference);
        internal long IntegerValue => Integer();
        internal double FloatValue => Float();
        internal TypeShape EnumShape => _enum;
    }

    /// <summary>A field of a viewed object: its name and its value.</summary>
    public readonly struct ViewField
    {
        public readonly string Name;
        public readonly View Value;

        public ViewField(string name, View value)
        {
            Name = name;
            Value = value;
        }
    }
}
