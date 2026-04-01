using SharpOS.AppSdk;
using System.Runtime.InteropServices;

namespace HelloSharp
{
    internal static unsafe class AppEntry
    {
        private const int ExitCode = 21;

        private static void Main() { }

        [UnmanagedCallersOnly(EntryPoint = "SharpAppEntry")]
        private static int SharpAppEntry(void* startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            RunMain();
            return ExitCode;
        }

        private static void RunMain()
        {
            byte* hello = stackalloc byte[]
            {
                (byte)'h',(byte)'e',(byte)'l',(byte)'l',(byte)'o',(byte)' ',
                (byte)'f',(byte)'r',(byte)'o',(byte)'m',(byte)' ',
                (byte)'c',(byte)'s',(byte)'h',(byte)'a',(byte)'r',(byte)'p',(byte)' ',
                (byte)'a',(byte)'p',(byte)'p',(byte)'\n', 0
            };

            byte* abiPrefix = stackalloc byte[]
            {
                (byte)'a',(byte)'b',(byte)'i',(byte)'=', 0
            };

            byte* newline = stackalloc byte[]
            {
                (byte)'\n', 0
            };

            AppHost.WriteString(hello);
            AppHost.WriteString(abiPrefix);
            AppHost.WriteUInt(AppHost.GetAbiVersion());
            AppHost.WriteString(newline);
            AppHost.Exit(ExitCode);
        }
    }
}
