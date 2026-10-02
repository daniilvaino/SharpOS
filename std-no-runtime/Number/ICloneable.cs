// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/ICloneable.cs
// Verbatim. NumberFormatInfo implements it.

namespace System
{
    public interface ICloneable
    {
        object Clone();
    }
}
