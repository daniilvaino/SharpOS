// System.Type — the members that need no metadata (step198), shared by both
// tiers' MinimalRuntime (where Type is its MethodTable pointer).
//
// Name / FullName / ToString: there are no type names in the image. NativeAOT
// itself answers this way when reflection metadata is absent
// (RuntimeTypeHandle.LastResortToString in its CoreLib): "EETypeRva:0x…" —
// the MethodTable's offset in its module — or "EETypePointer:0x…" when the
// module base is not known. Unique per type and stable for an image, which is
// what code keying dictionaries on GetType().Name needs; it does not pretend
// to be the C# name.
//
// GetTypeCode: by MethodTable identity against the primitive types; an enum
// is not seen through to its underlying type (that needs metadata) and gives
// Object.

namespace System
{
    public abstract partial class Type
    {
        public virtual string Name => LastResortName;

        public virtual string FullName => LastResortName;

        public override string ToString() => LastResortName;

        /// <summary>The image base names count from; set by the app tier at start (AppRuntime), 0 in the kernel.</summary>
        internal static ulong ModuleBase;

        private string LastResortName
        {
            get
            {
                ulong table = (ulong)(long)_handle;
                ulong module = ModuleBase;
                if (module != 0 && table > module && table - module < 0x1_0000_0000UL)
                    return "EETypeRva:0x" + ((uint)(table - module)).ToString("x");
                return "EETypePointer:0x" + table.ToString("x");
            }
        }

        public static TypeCode GetTypeCode(Type type)
        {
            if (type == null) return TypeCode.Empty;
            if (type == typeof(bool)) return TypeCode.Boolean;
            if (type == typeof(char)) return TypeCode.Char;
            if (type == typeof(sbyte)) return TypeCode.SByte;
            if (type == typeof(byte)) return TypeCode.Byte;
            if (type == typeof(short)) return TypeCode.Int16;
            if (type == typeof(ushort)) return TypeCode.UInt16;
            if (type == typeof(int)) return TypeCode.Int32;
            if (type == typeof(uint)) return TypeCode.UInt32;
            if (type == typeof(long)) return TypeCode.Int64;
            if (type == typeof(ulong)) return TypeCode.UInt64;
            if (type == typeof(float)) return TypeCode.Single;
            if (type == typeof(double)) return TypeCode.Double;
            if (type == typeof(decimal)) return TypeCode.Decimal;
            if (type == typeof(DateTime)) return TypeCode.DateTime;
            if (type == typeof(string)) return TypeCode.String;
            return TypeCode.Object;
        }

        // Shape queries the MethodTable answers by itself, as NativeAOT's
        // EETypePtr does (release/8.0, MIT): IsEnum is "base type is Enum"
        // (or a generic enum definition), generic queries read the
        // definition and the argument list.
        private unsafe SharpOS.Std.NoRuntime.GcMethodTable* Table => (SharpOS.Std.NoRuntime.GcMethodTable*)_handle;

        public unsafe bool IsValueType => Table->IsValueType;

        public unsafe bool IsInterface => Table->IsInterface;

        public unsafe bool IsArray => Table->IsArray;

        public unsafe bool IsEnum
        {
            get
            {
                SharpOS.Std.NoRuntime.GcMethodTable* mt = Table;
                if (mt->Kind == SharpOS.Std.NoRuntime.GcEETypeKind.Parameterized) return false;
                if (mt->IsGenericTypeDefinition)
                    return mt->ElementType < SharpOS.Std.NoRuntime.GcEETypeElementType.ValueType && mt->ElementType != SharpOS.Std.NoRuntime.GcEETypeElementType.Unknown;
                return (IntPtr)mt->GetBaseType() == typeof(Enum)._handle;
            }
        }

        public unsafe bool IsGenericType => Table->IsGeneric || Table->IsGenericTypeDefinition;

        public unsafe bool IsGenericTypeDefinition => Table->IsGenericTypeDefinition;

        /// <summary>Element type of an array, pointer or byref; null otherwise.</summary>
        public unsafe Type? GetElementType()
        {
            if (Table->Kind != SharpOS.Std.NoRuntime.GcEETypeKind.Parameterized) return null;
            return new RuntimeType((IntPtr)Table->RelatedType);
        }

        /// <summary>
        /// Whether a value of type <paramref name="c"/> can be assigned to this
        /// type: the runtime's cast rules (TypeCast.AreTypesAssignable — base
        /// types, interfaces, arrays, boxing to a base or interface).
        /// </summary>
        public unsafe bool IsAssignableFrom(Type? c)
        {
            if ((object?)c == null) return false;
            if (c._handle == _handle) return true;
            return SharpOS.Std.NoRuntime.GcRuntimeExports.AreTypesAssignable(
                (SharpOS.Std.NoRuntime.GcMethodTable*)c._handle, Table, boxedSource: true, allowSizeEquivalence: false);
        }

        public unsafe Type GetGenericTypeDefinition()
        {
            if (Table->IsGenericTypeDefinition) return this;
            if (!Table->IsGeneric) throw new InvalidOperationException("This operation is only valid on generic types.");
            return new RuntimeType((IntPtr)Table->GetGenericDefinition());
        }

        public unsafe Type[] GetGenericArguments()
        {
            if (!Table->IsGeneric) return new Type[0];
            int arity = Table->GetGenericDefinition()->GenericParameterCount;
            var args = new Type[arity];
            for (int i = 0; i < arity; i++)
                args[i] = new RuntimeType((IntPtr)Table->GetGenericArgument(i, arity));
            return args;
        }
    }

    // dotnet/runtime System.Private.CoreLib/src/System/Nullable.cs (MIT): the
    // non-generic half.
    public static class Nullable
    {
        public static Type? GetUnderlyingType(Type nullableType)
        {
            ArgumentNullException.ThrowIfNull(nullableType);

            if (nullableType.IsGenericType && !nullableType.IsGenericTypeDefinition)
            {
                // Instantiated generic type only
                Type genericType = nullableType.GetGenericTypeDefinition();
                if (ReferenceEquals(genericType, typeof(Nullable<>)) || genericType == typeof(Nullable<>))
                {
                    return nullableType.GetGenericArguments()[0];
                }
            }
            return null;
        }
    }
}
