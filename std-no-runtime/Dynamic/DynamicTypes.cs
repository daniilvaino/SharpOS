// What the binder knows of a type without reflection: the type table the
// compiler laid out (element type, base, interfaces, value or not) and the
// C# rules for the predefined types — numeric promotion, the implicit and
// explicit numeric conversions, enums, constants (C# spec §10.2, §10.3, §12.4.7).

using System;
using System.Runtime.CompilerServices;
using Microsoft.CSharp.RuntimeBinder;
using SharpOS.Std.Exchange;
using SharpOS.Std.NoRuntime;

namespace SharpOS.Std.Dynamic
{
    internal static unsafe class DynamicTypes
    {
        // ---- the type of a value ----

        internal static ulong TableOf(object o) => o == null ? 0 : ObjectLayout.TableOf(Unsafe.As<object, ulong>(ref o));

        internal static Type Of(object o) => o == null ? null : new RuntimeType((IntPtr)TableOf(o));

        internal static Type FromTable(ulong table) => table == 0 ? null : new RuntimeType((IntPtr)table);

        internal static GcMethodTable* Table(Type t) => (GcMethodTable*)t._handle;

        internal static GcEETypeElementType Element(Type t) => Table(t)->ElementType;

        /// <summary>A value's element type from its table: no Type made (step196).</summary>
        internal static GcEETypeElementType ElementTypeOf(object o) => ((GcMethodTable*)TableOf(o))->ElementType;

        internal static bool IsValueType(Type t) => t != null && Table(t)->IsValueType;

        internal static bool IsInterface(Type t) => t != null && Table(t)->IsInterface;

        internal static bool IsArray(Type t) => t != null && Table(t)->IsSzArray;

        internal static bool IsNullable(Type t) => t != null && Table(t)->ElementType == GcEETypeElementType.Nullable;

        internal static Type ElementOf(Type array) => FromTable((ulong)Table(array)->RelatedType);

        internal static Type BaseOf(Type t)
        {
            if (t == null || Table(t)->IsArray) return null;
            return FromTable((ulong)Table(t)->GetBaseType());
        }

        internal static bool IsEnum(Type t)
        {
            if (t == null) return false;
            GcEETypeElementType et = Table(t)->ElementType;
            if (et < GcEETypeElementType.SByte || et > GcEETypeElementType.UInt64) return false;
            return Table(t)->GetBaseType() == Table(typeof(Enum));
        }

        /// <summary>A predefined numeric type (char included), not an enum.</summary>
        internal static bool IsNumeric(Type t)
        {
            if (t == null) return false;
            GcEETypeElementType et = Table(t)->ElementType;
            return et >= GcEETypeElementType.Char && et <= GcEETypeElementType.Double
                && et != GcEETypeElementType.IntPtr && et != GcEETypeElementType.UIntPtr && !IsEnum(t);
        }

        internal static bool IsBool(Type t) => t == typeof(bool);

        internal static bool IsIntegral(GcEETypeElementType et)
            => et >= GcEETypeElementType.Char && et <= GcEETypeElementType.UInt64;

        internal static bool IsSigned(GcEETypeElementType et)
            => et == GcEETypeElementType.SByte || et == GcEETypeElementType.Int16
            || et == GcEETypeElementType.Int32 || et == GcEETypeElementType.Int64;

        /// <summary>Whether a value of <paramref name="from"/> is a <paramref name="to"/>: identity, reference or boxing conversion.</summary>
        internal static bool IsAssignable(Type from, Type to)
        {
            if (from == to) return true;
            if (to == typeof(object)) return true;
            return GcRuntimeExports.AreTypesAssignable(Table(from), Table(to), boxedSource: true, allowSizeEquivalence: false);
        }

        /// <summary>Strictly derived (class chain only).</summary>
        internal static bool IsSubclass(Type derived, Type @base)
        {
            if (derived == @base) return false;
            for (Type t = BaseOf(derived); t != null; t = BaseOf(t))
                if (t == @base) return true;
            return false;
        }

        internal static string Describe(Type t)
        {
            if (t == null) return "<null>";
            GcMethodTable* mt = Table(t);
            if (mt->IsSzArray) return DynamicMembers.NameOf(ElementOf(t)) + "[]";
            return "type@" + ((ulong)mt).ToString("x");
        }

        // ---- raw values ----

        // A boxed value's payload starts right after the table word.
        private static byte* Payload(object o) => (byte*)Unsafe.As<object, ulong>(ref o) + 8;

        /// <summary>An integral (or char, bool, enum) box as a long; ulong keeps its bits.</summary>
        internal static long Integer(object boxed, GcEETypeElementType et)
        {
            byte* p = Payload(boxed);
            switch (et)
            {
                case GcEETypeElementType.Boolean: return *p;
                case GcEETypeElementType.Char: return *(char*)p;
                case GcEETypeElementType.SByte: return *(sbyte*)p;
                case GcEETypeElementType.Byte: return *p;
                case GcEETypeElementType.Int16: return *(short*)p;
                case GcEETypeElementType.UInt16: return *(ushort*)p;
                case GcEETypeElementType.Int32: return *(int*)p;
                case GcEETypeElementType.UInt32: return *(uint*)p;
                case GcEETypeElementType.Int64: return *(long*)p;
                case GcEETypeElementType.UInt64: return (long)*(ulong*)p;
            }
            throw new InvalidCastException();
        }

        internal static double Float(object boxed, GcEETypeElementType et)
        {
            if (et == GcEETypeElementType.Single) return *(float*)Payload(boxed);
            if (et == GcEETypeElementType.Double) return *(double*)Payload(boxed);
            if (et == GcEETypeElementType.UInt64) return (ulong)Integer(boxed, et);
            return Integer(boxed, et);
        }

        /// <summary>A box of any value type from its bytes (enums included).</summary>
        internal static object Box(Type t, void* data)
            => GcRuntimeExports.RhBox((Internal.Runtime.MethodTable*)Table(t), ref *(byte*)data);

        /// <summary>A long as a box of an integral type or enum <paramref name="t"/> (truncating).</summary>
        internal static object BoxInteger(Type t, long v)
        {
            switch (Element(t))
            {
                case GcEETypeElementType.Char: if (t == typeof(char)) return (char)v; break;
                case GcEETypeElementType.Boolean: if (t == typeof(bool)) return v != 0; break;
                case GcEETypeElementType.SByte: if (t == typeof(sbyte)) return (sbyte)v; break;
                case GcEETypeElementType.Byte: if (t == typeof(byte)) return (byte)v; break;
                case GcEETypeElementType.Int16: if (t == typeof(short)) return (short)v; break;
                case GcEETypeElementType.UInt16: if (t == typeof(ushort)) return (ushort)v; break;
                case GcEETypeElementType.Int32: if (t == typeof(int)) return (int)v; break;
                case GcEETypeElementType.UInt32: if (t == typeof(uint)) return (uint)v; break;
                case GcEETypeElementType.Int64: if (t == typeof(long)) return v; break;
                case GcEETypeElementType.UInt64: if (t == typeof(ulong)) return (ulong)v; break;
            }
            return Box(t, &v);   // an enum: little-endian, the low bytes are the value
        }

        // ---- numeric conversions (C# §10.2.3, §10.3.2) ----

        // Bit per target element type: what each source converts to implicitly.
        private static uint ImplicitTargets(GcEETypeElementType from)
        {
            const uint I16 = 1u << (int)GcEETypeElementType.Int16, U16 = 1u << (int)GcEETypeElementType.UInt16,
                       I32 = 1u << (int)GcEETypeElementType.Int32, U32 = 1u << (int)GcEETypeElementType.UInt32,
                       I64 = 1u << (int)GcEETypeElementType.Int64, U64 = 1u << (int)GcEETypeElementType.UInt64,
                       F32 = 1u << (int)GcEETypeElementType.Single, F64 = 1u << (int)GcEETypeElementType.Double;
            switch (from)
            {
                case GcEETypeElementType.SByte: return I16 | I32 | I64 | F32 | F64;
                case GcEETypeElementType.Byte: return I16 | U16 | I32 | U32 | I64 | U64 | F32 | F64;
                case GcEETypeElementType.Int16: return I32 | I64 | F32 | F64;
                case GcEETypeElementType.UInt16: return I32 | U32 | I64 | U64 | F32 | F64;
                case GcEETypeElementType.Int32: return I64 | F32 | F64;
                case GcEETypeElementType.UInt32: return I64 | U64 | F32 | F64;
                case GcEETypeElementType.Int64: return F32 | F64;
                case GcEETypeElementType.UInt64: return F32 | F64;
                case GcEETypeElementType.Char: return U16 | I32 | U32 | I64 | U64 | F32 | F64;
                case GcEETypeElementType.Single: return F64;
            }
            return 0;
        }

        internal static bool ImplicitNumeric(Type from, Type to)
            => IsNumeric(from) && IsNumeric(to) && (from == to || (ImplicitTargets(Element(from)) & (1u << (int)Element(to))) != 0);

        /// <summary>An int (or long) constant that fits the target: the implicit constant expression conversion (§10.2.11).</summary>
        internal static bool ConstantFits(object value, Type from, Type to)
        {
            if (value == null || !IsNumeric(to)) return false;
            if (from == typeof(int))
            {
                int v = (int)value;
                switch (Element(to))
                {
                    case GcEETypeElementType.SByte: return v >= sbyte.MinValue && v <= sbyte.MaxValue;
                    case GcEETypeElementType.Byte: return v >= 0 && v <= byte.MaxValue;
                    case GcEETypeElementType.Int16: return v >= short.MinValue && v <= short.MaxValue;
                    case GcEETypeElementType.UInt16: return v >= 0 && v <= ushort.MaxValue;
                    case GcEETypeElementType.UInt32: return v >= 0;
                    case GcEETypeElementType.UInt64: return v >= 0;
                }
                return false;
            }
            if (from == typeof(long)) return Element(to) == GcEETypeElementType.UInt64 && (long)value >= 0;
            return false;
        }

        /// <summary>A numeric (or enum, char) value as <paramref name="to"/>, explicitly; checked throws on overflow.</summary>
        internal static object ConvertNumeric(object value, Type to, bool isChecked)
        {
            GcEETypeElementType from = Element(Of(value));
            GcEETypeElementType et = Element(to);
            if (from == GcEETypeElementType.Single || from == GcEETypeElementType.Double)
            {
                double d = Float(value, from);
                if (et == GcEETypeElementType.Double) return d;
                if (et == GcEETypeElementType.Single) return (float)d;
                if (isChecked)
                {
                    // Truncation toward zero must land inside the target (§10.3.2).
                    if (d != d || d <= MinOf(et) - 1 || d >= MaxOf(et) + 1) throw new OverflowException("Arithmetic operation resulted in an overflow.");
                }
                if (et == GcEETypeElementType.UInt64) return BoxInteger(to, (long)(ulong)d);
                return BoxInteger(to, (long)d);
            }
            long v = Integer(value, from);
            bool fromUnsigned64 = from == GcEETypeElementType.UInt64;
            if (et == GcEETypeElementType.Double) return fromUnsigned64 ? (double)(ulong)v : (double)v;
            if (et == GcEETypeElementType.Single) return fromUnsigned64 ? (float)(ulong)v : (float)v;
            if (isChecked && !FitsIntegral(v, fromUnsigned64, et))
                throw new OverflowException("Arithmetic operation resulted in an overflow.");
            return BoxInteger(to, v);
        }

        private static double MinOf(GcEETypeElementType et)
        {
            switch (et)
            {
                case GcEETypeElementType.SByte: return sbyte.MinValue;
                case GcEETypeElementType.Int16: return short.MinValue;
                case GcEETypeElementType.Int32: return int.MinValue;
                case GcEETypeElementType.Int64: return -9223372036854775808.0;
            }
            return 0;
        }

        private static double MaxOf(GcEETypeElementType et)
        {
            switch (et)
            {
                case GcEETypeElementType.SByte: return sbyte.MaxValue;
                case GcEETypeElementType.Byte: return byte.MaxValue;
                case GcEETypeElementType.Int16: return short.MaxValue;
                case GcEETypeElementType.UInt16:
                case GcEETypeElementType.Char: return ushort.MaxValue;
                case GcEETypeElementType.Int32: return int.MaxValue;
                case GcEETypeElementType.UInt32: return uint.MaxValue;
                case GcEETypeElementType.Int64: return 9223372036854775807.0;
                case GcEETypeElementType.UInt64: return 18446744073709551615.0;
            }
            return 0;
        }

        private static bool FitsIntegral(long v, bool unsigned64, GcEETypeElementType et)
        {
            if (unsigned64 && v < 0) return et == GcEETypeElementType.UInt64;   // above long.MaxValue
            switch (et)
            {
                case GcEETypeElementType.SByte: return v >= sbyte.MinValue && v <= sbyte.MaxValue;
                case GcEETypeElementType.Byte: return v >= 0 && v <= byte.MaxValue;
                case GcEETypeElementType.Int16: return v >= short.MinValue && v <= short.MaxValue;
                case GcEETypeElementType.UInt16:
                case GcEETypeElementType.Char: return v >= 0 && v <= ushort.MaxValue;
                case GcEETypeElementType.Int32: return v >= int.MinValue && v <= int.MaxValue;
                case GcEETypeElementType.UInt32: return v >= 0 && v <= uint.MaxValue;
                case GcEETypeElementType.Int64: return true;
                case GcEETypeElementType.UInt64: return v >= 0;
            }
            return false;
        }

        // ---- binary numeric promotion (§12.4.7.3) ----

        /// <summary>The type both operands are promoted to, or null when C# has no such operator (ulong with a signed type).</summary>
        internal static Type Promote(Type a, Type b)
        {
            GcEETypeElementType x = Element(a), y = Element(b);
            if (x == GcEETypeElementType.Double || y == GcEETypeElementType.Double) return typeof(double);
            if (x == GcEETypeElementType.Single || y == GcEETypeElementType.Single) return typeof(float);
            if (x == GcEETypeElementType.UInt64 || y == GcEETypeElementType.UInt64)
                return IsSigned(x) || IsSigned(y) ? null : typeof(ulong);
            if (x == GcEETypeElementType.Int64 || y == GcEETypeElementType.Int64) return typeof(long);
            if (x == GcEETypeElementType.UInt32 || y == GcEETypeElementType.UInt32)
                return IsSigned(x) || IsSigned(y) ? typeof(long) : typeof(uint);
            return typeof(int);
        }

        /// <summary>Unary promotion: what +, -, ~ work on (§12.4.7.2).</summary>
        internal static Type PromoteUnary(Type t)
        {
            switch (Element(t))
            {
                case GcEETypeElementType.SByte:
                case GcEETypeElementType.Byte:
                case GcEETypeElementType.Int16:
                case GcEETypeElementType.UInt16:
                case GcEETypeElementType.Char: return typeof(int);
            }
            return t;
        }

        // ---- C# names for messages ----

        internal static RuntimeBinderException Error(string message) => new RuntimeBinderException(message);
    }

    /// <summary>The bytes of a boxed struct, to call and assign through as C# does on the box.</summary>
    public static class DynamicBox
    {
        private sealed class RawBox { public byte Data; }

        public static ref T Ref<T>(object box) where T : struct
            => ref Unsafe.As<byte, T>(ref Unsafe.As<RawBox>(box).Data);

        /// <summary>T? as `dynamic` holds it: the value boxed, or null.</summary>
        public static object OfNullable<T>(T? value) where T : struct => value.HasValue ? (object)value.GetValueOrDefault() : null;

        public static T? ToNullable<T>(object value) where T : struct => value == null ? (T?)null : (T)value;
    }
}
