// System.Type.BaseType and IsSubclassOf — answered from the MethodTable, as the
// rest of Type.Statics.cs.
//
// IsSubclassOf: ported from dotnet/runtime release/8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Type.cs — verbatim.
// BaseType: the shape NativeAOT's RuntimeType gives without metadata
//   (src/coreclr/nativeaot/.../RuntimeTypeInfo / EETypePtr.BaseType): the
//   MethodTable's base for classes and structs, System.Array for arrays, null
//   for interfaces, pointers and byrefs (and for System.Object).

namespace System
{
    public abstract partial class Type
    {
        public unsafe virtual Type? BaseType
        {
            get
            {
                SharpOS.Std.NoRuntime.GcMethodTable* mt = Table;
                if (mt->IsInterface) return null;
                if (mt->Kind == SharpOS.Std.NoRuntime.GcEETypeKind.Parameterized)
                    return mt->IsArray ? typeof(Array) : null;
                SharpOS.Std.NoRuntime.GcMethodTable* baseType = mt->GetBaseType();
                return baseType == null ? null : new RuntimeType((IntPtr)baseType);
            }
        }

        public virtual bool IsSubclassOf(Type c)
        {
            Type? p = this;
            if (p == c)
                return false;
            while (p != null)
            {
                if (p == c)
                    return true;
                p = p.BaseType;
            }
            return false;
        }
    }
}
