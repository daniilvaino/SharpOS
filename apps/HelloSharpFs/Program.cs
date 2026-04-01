using SharpOS.AppSdk;
using System.Runtime;

namespace HelloSharpFs
{
    internal static unsafe class AppEntry
    {
        private const int ExitCode = 21;

        private static int Main()
        {
            return 0;
        }

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            Run();
            return ExitCode;
        }

        private static void Run()
        {
            byte* hello = stackalloc byte[]
            {
                (byte)'h',(byte)'e',(byte)'l',(byte)'l',(byte)'o',(byte)' ',
                (byte)'f',(byte)'r',(byte)'o',(byte)'m',(byte)' ',
                (byte)'c',(byte)'s',(byte)'h',(byte)'a',(byte)'r',(byte)'p',(byte)' ',
                (byte)'f',(byte)'s',(byte)' ',
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
