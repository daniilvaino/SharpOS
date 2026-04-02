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
                (byte)'a',(byte)'p',(byte)'p',(byte)'\n',0
            };
            byte* abiPrefix = stackalloc byte[] { (byte)'a',(byte)'b',(byte)'i',(byte)'=',0 };
            byte* fileExistsPrefix = stackalloc byte[]
            {
                (byte)'f',(byte)'i',(byte)'l',(byte)'e',(byte)'_',(byte)'e',(byte)'x',(byte)'i',(byte)'s',(byte)'t',(byte)'s',(byte)'=',0
            };
            byte* dirEntryPrefix = stackalloc byte[]
            {
                (byte)'d',(byte)'i',(byte)'r',(byte)'_',(byte)'e',(byte)'n',(byte)'t',(byte)'r',(byte)'y',(byte)'=',0
            };
            byte* dirNamePrefix = stackalloc byte[]
            {
                (byte)'d',(byte)'i',(byte)'r',(byte)'_',(byte)'n',(byte)'a',(byte)'m',(byte)'e',(byte)'=',0
            };
            byte* newline = stackalloc byte[] { (byte)'\n',0 };
            byte* filePath = stackalloc byte[]
            {
                (byte)'\\',(byte)'E',(byte)'F',(byte)'I',(byte)'\\',(byte)'B',(byte)'O',(byte)'O',(byte)'T',(byte)'\\',
                (byte)'H',(byte)'E',(byte)'L',(byte)'L',(byte)'O',(byte)'.',(byte)'E',(byte)'L',(byte)'F',0
            };
            byte* bootDirPath = stackalloc byte[]
            {
                (byte)'\\',(byte)'E',(byte)'F',(byte)'I',(byte)'\\',(byte)'B',(byte)'O',(byte)'O',(byte)'T',0
            };

            AppHost.WriteString(hello);
            AppHost.WriteString(abiPrefix);
            AppHost.WriteUInt(AppHost.GetAbiVersion());
            AppHost.WriteString(newline);

            AppHost.WriteString(fileExistsPrefix);
            AppHost.WriteUInt((uint)AppHost.FileExistsEx(filePath));
            AppHost.WriteString(newline);

            for (uint i = 0; ; i++)
            {
                byte* nameBuffer = stackalloc byte[260];
                FileEntry entry;
                AppServiceStatus dirStatus = AppHost.TryReadDirEntry(bootDirPath, i, nameBuffer, 260, out entry);
                AppHost.WriteString(dirEntryPrefix);
                AppHost.WriteUInt((uint)dirStatus);
                AppHost.WriteString(newline);

                if (dirStatus == AppServiceStatus.Ok)
                {
                    AppHost.WriteString(dirNamePrefix);
                    AppHost.WriteString(nameBuffer);
                    AppHost.WriteString(newline);
                }
                else if (dirStatus == AppServiceStatus.EndOfDirectory)
                {
                    break;
                }
                else
                {
                    AppHost.Exit(90);
                }
            }

            AppHost.Exit(ExitCode);
        }
    }
}
