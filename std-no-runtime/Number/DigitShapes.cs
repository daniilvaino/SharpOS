// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/DigitShapes.cs
// Verbatim. NumberFormatInfo.DigitSubstitution is typed by it.

namespace System.Globalization
{
    public enum DigitShapes : int
    {
        Context         = 0x0000,   // The shape depends on the previous text in the same output.
        None            = 0x0001,   // Gives full Unicode compatibility.
        NativeNational  = 0x0002    // National shapes
    }
}
