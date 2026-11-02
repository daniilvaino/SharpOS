// System.Runtime.CompilerServices.CollectionBuilderAttribute — what C# 12
// collection expressions look for on a type with a builder method. Ported from
// dotnet/runtime (.NET 10, the fork dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/CollectionBuilderAttribute.cs
// Public, as CoreLib's. Step232: ImmutableStack<T> carries it.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public sealed class CollectionBuilderAttribute : Attribute
    {
        public CollectionBuilderAttribute(Type builderType, string methodName)
        {
            BuilderType = builderType;
            MethodName = methodName;
        }

        public Type BuilderType { get; }

        public string MethodName { get; }
    }
}
