// System.Runtime.InteropServices.RuntimeInformation — BCL's surface
// (dotnet/runtime v8.0.27 RuntimeInformation.cs and the per-OS parts) with
// SharpOS's answers: x64 only, no host OS among BCL's names, so IsOSPlatform
// is OperatingSystem's (true only for "SHARPOS").

namespace System.Runtime.InteropServices
{
    public static class RuntimeInformation
    {
        public static string FrameworkDescription => ".NET 8 (SharpOS, NativeAOT)";

        public static string OSDescription => "SharpOS";

        public static string RuntimeIdentifier => "sharpos-x64";

        public static Architecture OSArchitecture => Architecture.X64;

        public static Architecture ProcessArchitecture => Architecture.X64;

        public static bool IsOSPlatform(OSPlatform osPlatform) => OperatingSystem.IsOSPlatform(osPlatform.Name);
    }
}
