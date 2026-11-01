// Type as BCL has it: a MemberInfo, with the shape queries BCL code asks
// (step224, System.Text.Json's converter factories). Answers come from the
// MethodTable where it holds them and are honest about where it does not:
//
//   IsClass, IsConstructedGenericType, ContainsGenericParameters — from the
//   type's kind and generic flags;
//   GetInterfaces — the MethodTable's interface map, every interface the type
//   implements (BCL: the same set, no order promised);
//   IsAbstract — true for interfaces; a class's abstractness is not in the
//   MethodTable, so false (SharpOS cut, recorded in the limits doc);
//   IsSealed — true for value types; false for classes (the same reason);
//   IsByRefLike — false (same);
//   Name, DeclaringType — Type.Statics.cs (no names in the image);
//   GetMethods/GetMember/GetCustomAttributes — empty: no metadata;
//   MakeGenericType, GetType(string) — NotSupported / null: neither a run-time
//   instantiation nor a name lookup exists without reflection metadata.

using System.Reflection;

namespace System
{
    public abstract partial class Type : MemberInfo
    {
        public static readonly Type[] EmptyTypes = Array.Empty<Type>();

        public override MemberTypes MemberType => MemberTypes.TypeInfo;

        public override Type? ReflectedType => null;

        public override object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();

        public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        {
            ArgumentNullException.ThrowIfNull(attributeType);
            return Array.Empty<object>();
        }

        public override bool IsDefined(Type attributeType, bool inherit)
        {
            ArgumentNullException.ThrowIfNull(attributeType);
            return false;
        }

        public bool IsClass => !IsValueType && !IsInterface;

        public bool IsAbstract => IsInterface;

        public bool IsSealed => IsValueType;

        public bool IsByRefLike => false;

        public bool IsConstructedGenericType => IsGenericType && !IsGenericTypeDefinition;

        public bool ContainsGenericParameters => IsGenericTypeDefinition;

        public unsafe Type[] GetInterfaces()
        {
            var table = (SharpOS.Std.NoRuntime.GcMethodTable*)_handle;
            int count = table->NumInterfaces;
            if (count == 0) return EmptyTypes;
            SharpOS.Std.NoRuntime.EEInterfaceInfo* map = table->GetInterfaceMap();
            var result = new Type[count];
            for (int i = 0; i < count; i++)
            {
                RuntimeTypeHandle handle = default;
                handle._value = (IntPtr)map[i].GetInterfaceEEType();
                result[i] = GetTypeFromHandle(handle);
            }
            return result;
        }

        public MethodInfo[] GetMethods() => Array.Empty<MethodInfo>();

        public MethodInfo[] GetMethods(BindingFlags bindingAttr) => Array.Empty<MethodInfo>();

        public MemberInfo[] GetMember(string name) => GetMember(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        public MemberInfo[] GetMember(string name, BindingFlags bindingAttr)
        {
            ArgumentNullException.ThrowIfNull(name);
            return Array.Empty<MemberInfo>();
        }

        public MemberInfo[] GetMember(string name, MemberTypes type, BindingFlags bindingAttr)
        {
            ArgumentNullException.ThrowIfNull(name);
            return Array.Empty<MemberInfo>();
        }

        public Type MakeGenericType(params Type[] typeArguments)
        {
            ArgumentNullException.ThrowIfNull(typeArguments);
            throw new NotSupportedException(
                "Type.MakeGenericType is not available on SharpOS: a generic instantiation exists only if the compiler built it, and there is no reflection metadata to find it by.");
        }

        public static Type? GetType(string typeName) => GetType(typeName, throwOnError: false);

        public static Type? GetType(string typeName, bool throwOnError) => GetType(typeName, throwOnError, ignoreCase: false);

        public static Type? GetType(string typeName, bool throwOnError, bool ignoreCase)
        {
            ArgumentNullException.ThrowIfNull(typeName);
            _ = ignoreCase;
            if (throwOnError)
                throw new TypeLoadException("Could not load type '" + typeName + "': there are no type names in the image.");
            return null;
        }
    }
}
