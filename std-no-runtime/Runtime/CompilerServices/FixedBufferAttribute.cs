// Ported from dotnet/runtime release/8.0
// src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/
// FixedBufferAttribute.cs and UnsafeValueTypeAttribute.cs (MIT). No cuts.
//
// The compiler marks a `fixed` buffer field with these. Within one assembly it
// did without them; another assembly reads the field as the buffer only by the
// attribute — an app's `node->ProductName` over the SDK's AppUsbNode, since
// CoreLib became its own assembly.

namespace System.Runtime.CompilerServices
{
    /// <summary>Indicates that a field should be treated as containing a fixed number of elements of the specified primitive type.</summary>
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public sealed class FixedBufferAttribute : Attribute
    {
        public FixedBufferAttribute(Type elementType, int length)
        {
            ElementType = elementType;
            Length = length;
        }

        public Type ElementType { get; }
        public int Length { get; }
    }

    /// <summary>Specifies that a type contains an unmanaged array that might potentially overflow.</summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public sealed class UnsafeValueTypeAttribute : Attribute
    {
    }
}
