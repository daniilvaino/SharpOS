// Small marker attributes that BCL types expect to exist. They carry no
// behaviour for our purposes; ILC/JIT uses them as hints in certain
// versioning / layout decisions. We declare them empty.

using System;

namespace System.Runtime.CompilerServices
{
    // Required by the compiler for every extension method (`this T` first
    // parameter). Emitted implicitly on both the enclosing class and each
    // method.
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Method, Inherited = false)]
    public sealed class ExtensionAttribute : Attribute
    {
        public ExtensionAttribute() { }
    }

    // Tells the compiler what name to use for an indexer property in
    // metadata (default is "Item"; StringBuilder uses "Chars"). Purely a
    // reflection-level hint — no runtime behavior.
    [AttributeUsage(AttributeTargets.Property, Inherited = true)]
    public sealed class IndexerNameAttribute : Attribute
    {
        public IndexerNameAttribute(string indexerName) { }
    }
}

namespace System.Runtime.InteropServices
{
    // Required by the compiler whenever a parameter uses the `in` modifier —
    // the compiler emits `[In]` on the parameter implicitly.
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class InAttribute : Attribute
    {
        public InAttribute() { }
    }
}

namespace System.Runtime.Versioning
{
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum |
        AttributeTargets.Interface | AttributeTargets.Delegate |
        AttributeTargets.Method | AttributeTargets.Constructor |
        AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event,
        Inherited = false)]
    internal sealed class NonVersionableAttribute : Attribute
    {
        public NonVersionableAttribute() { }
    }
}

namespace System
{
    [AttributeUsage(AttributeTargets.All, Inherited = true, AllowMultiple = false)]
    public sealed class CLSCompliantAttribute : Attribute
    {
        public CLSCompliantAttribute(bool isCompliant) { IsCompliant = isCompliant; }
        public bool IsCompliant { get; }
    }

    // BCL throws this from Unsafe.* intrinsic bodies (never actually
    // executes — ILC replaces with IL). We need the type to exist so the
    // throws compile.
    public class PlatformNotSupportedException : NotSupportedException
    {
        public PlatformNotSupportedException() { }
        public PlatformNotSupportedException(string message) : base(message) { }
        public PlatformNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
    }
}
