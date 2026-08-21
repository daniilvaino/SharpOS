// Marker types the C# compiler looks for by name. Both are complete — there is
// nothing to implement, so these are the real thing rather than a partial
// stand-in, and they belong under the canonical names.
//
// From dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Presence of this type is how the compiler decides `init` accessors and
    /// `record` types are allowed. It is never instantiated.
    /// </summary>
    internal static class IsExternalInit
    {
    }

    /// <summary>
    /// Suppresses the CLR's zero-initialisation of locals for the method, type
    /// or module it is applied to.
    /// </summary>
    /// <remarks>
    /// Advisory: code carrying it must not read a local before writing it. Here
    /// it changes nothing at runtime — the flag it sets is honoured by the
    /// runtime that owns the frame — but the attribute has to exist for such
    /// code to compile at all.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct
        | AttributeTargets.Interface | AttributeTargets.Constructor | AttributeTargets.Method
        | AttributeTargets.Property | AttributeTargets.Event, Inherited = false)]
    internal sealed class SkipLocalsInitAttribute : Attribute
    {
        public SkipLocalsInitAttribute()
        {
        }
    }
}
