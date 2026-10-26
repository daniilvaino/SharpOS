// Ported from dotnet/runtime release/7.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Double.cs, Single.cs
// (the CoreLib snapshot in gc-experiment/dotnet-runtime). IsInteger / IsEvenInteger /
// IsOddInteger of double and float — upstream's INumberBase members, kept as plain
// statics (no generic math interfaces in std). Bodies verbatim except that
// Truncate/Abs are spelled Math.Truncate/Math.Abs (upstream's double.Truncate and
// double.Abs are INumber members std does not declare) — marked "SharpOS:".

namespace System
{
    public partial struct Double
    {
        public static bool IsEvenInteger(double value) => IsInteger(value) && (Math.Abs(value % 2) == 0); // SharpOS: Math.Abs

        public static bool IsInteger(double value) => IsFinite(value) && (value == Math.Truncate(value)); // SharpOS: Math.Truncate

        public static bool IsOddInteger(double value) => IsInteger(value) && (Math.Abs(value % 2) == 1); // SharpOS: Math.Abs
    }

    public partial struct Single
    {
        public static bool IsEvenInteger(float value) => IsInteger(value) && (Math.Abs(value % 2) == 0); // SharpOS: Math.Abs

        public static bool IsInteger(float value) => IsFinite(value) && (value == MathF.Truncate(value)); // SharpOS: MathF.Truncate

        public static bool IsOddInteger(float value) => IsInteger(value) && (Math.Abs((value) % 2) == 1); // SharpOS: Math.Abs
    }
}
