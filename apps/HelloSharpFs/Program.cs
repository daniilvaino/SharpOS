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
            byte* banner = stackalloc byte[]
            {
                (byte)'s',(byte)'t',(byte)'r',(byte)'i',(byte)'n',(byte)'g',(byte)' ',
                (byte)'e',(byte)'x',(byte)'p',(byte)' ',
                (byte)'s',(byte)'t',(byte)'a',(byte)'r',(byte)'t',(byte)'\n',0
            };
            byte* testIdPrefix = stackalloc byte[]
            {
                (byte)'t',(byte)'e',(byte)'s',(byte)'t',(byte)'_',(byte)'i',(byte)'d',(byte)'=',0
            };
            byte* resultPrefix = stackalloc byte[]
            {
                (byte)'t',(byte)'e',(byte)'s',(byte)'t',(byte)'_',(byte)'r',(byte)'e',(byte)'s',(byte)'u',(byte)'l',(byte)'t',(byte)'=',0
            };
            byte* abiPrefix = stackalloc byte[] { (byte)'a',(byte)'b',(byte)'i',(byte)'=',0 };
            byte* newline = stackalloc byte[] { (byte)'\n',0 };

            AppHost.WriteString(banner);
            AppHost.WriteString(abiPrefix);
            AppHost.WriteUInt(AppHost.GetAbiVersion());
            AppHost.WriteString(newline);

            uint result = StringExperimentSuite.RunSelected(out uint testId);

            AppHost.WriteString(testIdPrefix);
            AppHost.WriteUInt(testId);
            AppHost.WriteString(newline);

            AppHost.WriteString(resultPrefix);
            AppHost.WriteUInt(result);
            AppHost.WriteString(newline);

            AppHost.Exit(ExitCode);
        }
    }
}
