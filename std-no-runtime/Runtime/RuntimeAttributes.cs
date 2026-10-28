// Small marker attributes that BCL types expect to exist. They carry no
// behaviour for our purposes; ILC/JIT uses them as hints in certain
// versioning / layout decisions. We declare them empty.

using System;

// The image does not claim CLS compliance; it is said so that the
// [CLSCompliant(false)] ported BCL code carries on its members is not each a
// CS3021 ("the assembly does not have a CLSCompliant attribute").
[assembly: CLSCompliant(false)]

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

    // Marker type the C# compiler uses as a modreq on volatile field types.
    // No members needed — its existence in this namespace is what the
    // compiler checks. Without it, `volatile int x;` gives CS0518.
    public static class IsVolatile { }

    // C# 9 `[ModuleInitializer]` recognition by Roslyn. ILC handles the
    // actual dispatch (module init runs before any user code touches the
    // module). Empty attribute body suffices — Roslyn only checks for the
    // type's existence by name. Needed to write NativeAotProbe.Probe_ModuleInit.
    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    public sealed class ModuleInitializerAttribute : Attribute
    {
        public ModuleInitializerAttribute() { }
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

    // The platform attributes (BCL, OSPlatformAttribute.cs), step208: ported
    // code marks itself with them (json2dir: [assembly: UnsupportedOSPlatform
    // ("windows")]). Read by nothing here — the platform analyzer is the
    // compiler's.
    public abstract class OSPlatformAttribute : Attribute
    {
        private protected OSPlatformAttribute(string platformName) => PlatformName = platformName;
        public string PlatformName { get; }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Enum |
                    AttributeTargets.Event | AttributeTargets.Field | AttributeTargets.Interface | AttributeTargets.Method |
                    AttributeTargets.Module | AttributeTargets.Property | AttributeTargets.Struct,
                    AllowMultiple = true, Inherited = false)]
    public sealed class SupportedOSPlatformAttribute : OSPlatformAttribute
    {
        public SupportedOSPlatformAttribute(string platformName) : base(platformName) { }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Constructor | AttributeTargets.Enum |
                    AttributeTargets.Event | AttributeTargets.Field | AttributeTargets.Interface | AttributeTargets.Method |
                    AttributeTargets.Module | AttributeTargets.Property | AttributeTargets.Struct,
                    AllowMultiple = true, Inherited = false)]
    public sealed class UnsupportedOSPlatformAttribute : OSPlatformAttribute
    {
        public UnsupportedOSPlatformAttribute(string platformName) : base(platformName) { }
        public UnsupportedOSPlatformAttribute(string platformName, string message) : base(platformName) => Message = message;
        public string Message { get; }
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
    public class ObjectDisposedException : InvalidOperationException
    {
        // BCL (.NET 7) guards: the object's type name as ObjectName.
        public static void ThrowIf([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(true)] bool condition, object instance)
        {
            if (condition) throw new ObjectDisposedException(instance?.GetType().FullName);
        }

        public static void ThrowIf([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(true)] bool condition, Type type)
        {
            if (condition) throw new ObjectDisposedException(type?.FullName);
        }

        public ObjectDisposedException(string? objectName)
            : base("Cannot access a disposed object: " + (objectName ?? ""))
            { ObjectName = objectName ?? ""; }

        public ObjectDisposedException(string? objectName, string? message)
            : base(message ?? "") { ObjectName = objectName ?? ""; }

        public string ObjectName { get; }
    }

    public class PlatformNotSupportedException : NotSupportedException
    {
        public PlatformNotSupportedException() { }
        public PlatformNotSupportedException(string message) : base(message) { }
        public PlatformNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Fills the parameter with the name of the calling member. The compiler
    /// supplies the value at each call site, so the attribute only has to
    /// exist and be found by name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerMemberNameAttribute : Attribute
    {
        public CallerMemberNameAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerFilePathAttribute : Attribute
    {
        public CallerFilePathAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    public sealed class CallerLineNumberAttribute : Attribute
    {
        public CallerLineNumberAttribute() { }
    }

    /// <summary>The source text of another argument (C# 10; ArgumentNullException.ThrowIfNull and kin).</summary>
    [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
    public sealed class CallerArgumentExpressionAttribute : Attribute
    {
        public CallerArgumentExpressionAttribute(string parameterName) { ParameterName = parameterName; }
        public string ParameterName { get; }
    }
}
