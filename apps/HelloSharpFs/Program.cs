using SharpOS.AppSdk;
using System;
using System.Runtime;

namespace HelloSharpFs
{
    internal static unsafe class AppEntry
    {
        private const int ExitCode = 21;
        private const string Banner = "string exp start\n";
        private const string AbiPrefix = "abi=";
        private const string TestIdPrefix = "test_id=";
        private const string ResultPrefix = "test_result=";
        private const string NestedStatusPrefix = "nested_status=";
        private const string NestedExitPrefix = " nested_exit=";
        private const string NestedPath = "\\EFI\\BOOT\\HELLO.ELF";
        private const string NewLine = "\n";

        private static int Main()
        {
            Run();
            return 0;
        }

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            Run();
            return ExitCode;
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            System.Runtime.RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static void Run()
        {
            AppHost.WriteString(Banner);
            AppHost.WriteString(AbiPrefix);
            AppHost.WriteUInt(AppHost.GetAbiVersion());
            AppHost.WriteString(NewLine);

            uint result = StringExperimentSuite.RunSelected(out uint testId);

            AppHost.WriteString(TestIdPrefix);
            AppHost.WriteUInt(testId);
            AppHost.WriteString(NewLine);

            AppHost.WriteString(ResultPrefix);
            AppHost.WriteUInt(result);
            AppHost.WriteString(NewLine);

            RunNestedSmoke();

            byte* entryname = stackalloc byte[256];
            FileEntry fileEntry;

            uint selectedIndex = 0;
            uint cachedEntryCount = 0;
            for (;  ; )
            {
                bool enterNow = false;
                KeyInfo keyInfo;
                if (AppHost.TryReadKey(out keyInfo) == AppServiceStatus.Ok)
                {
                    if (keyInfo.ScanCode == 0x03 && cachedEntryCount != 0)
                    {
                        if (selectedIndex < cachedEntryCount - 1)
                            selectedIndex += 1;
                        else
                            selectedIndex = 0;
                    }

                    if (keyInfo.ScanCode == 0x04 && cachedEntryCount != 0)
                    {
                        if (selectedIndex == 0)
                            selectedIndex = cachedEntryCount - 1;
                        else
                            selectedIndex -= 1;
                    }

                    if (keyInfo.UnicodeChar == 0x000D)
                    {
                        AppHost.WriteString("ENTER\n");
                        enterNow = true;
                    }
                    
                    if (keyInfo.UnicodeChar == 0x001B || keyInfo.ScanCode == 0x0017)
                    {
                        AppHost.Exit(ExitCode);
                        return;
                    }
                }
                else
                {
                    continue;
                }

                string dir = @"\EFI\BOOT";
                uint currentEntryCount = 0;
                for (uint i = 0; i < 256; i++)
                {
                    var appServiceStatus = AppHost.TryReadDirEntry(dir, i, entryname, 256, out fileEntry);
                    if (appServiceStatus == AppServiceStatus.Ok)
                    {
                        currentEntryCount += 1;
                        string filename = AppString.FromAscii(entryname, fileEntry.NameLength);
                        if (!enterNow)
                        {
                            if (i == selectedIndex)
                            {
                                AppHost.WriteString("[");
                            }
                            //else AppHost.WriteString(" ");
                            AppHost.WriteString(filename);
                            if (i == selectedIndex)
                            {
                                AppHost.WriteString("]");
                            }
                            AppHost.WriteString(" ");
                        }
                        if (enterNow)
                        {
                            if (i == selectedIndex)
                            {
                                string path = StringAlgorithms.Concat(dir, @"\");
                                path = StringAlgorithms.Concat(path, filename);
                                enterNow = false;
                                int childExit;
                                ResolveLaunchAbi(filename, out uint abiVersion, out AppServiceAbi serviceAbi);
                                AppServiceStatus runStatus = AppHost.TryRunApp(path, abiVersion, serviceAbi, out childExit);

                                AppHost.WriteString("run_status=");
                                AppHost.WriteUInt((uint)runStatus);
                                AppHost.WriteString(" run_exit=");
                                AppHost.WriteUInt((uint)childExit);
                                AppHost.WriteString("\n");
                                break;
                            }
                        }

                    }
                    else if (appServiceStatus == AppServiceStatus.EndOfDirectory)
                    {
                        break;
                    }
                    else
                    {
                        AppHost.Exit(90);
                        return;
                    }
                    
                }

                cachedEntryCount = currentEntryCount;
                if (cachedEntryCount == 0)
                {
                    selectedIndex = 0;
                }
                else if (selectedIndex >= cachedEntryCount)
                {
                    selectedIndex = cachedEntryCount - 1;
                }

                AppHost.WriteString(NewLine);
            }
            

            AppHost.Exit(ExitCode);
        }

        private static void RunNestedSmoke()
        {
            int childExitCode = 0;
            AppServiceStatus status = AppHost.TryRunApp(
                NestedPath,
                AppStartupBlock.AbiVersionV1,
                AppServiceAbi.WindowsX64,
                out childExitCode);

            AppHost.WriteString(NestedStatusPrefix);
            AppHost.WriteUInt((uint)status);
            AppHost.WriteString(NestedExitPrefix);
            AppHost.WriteUInt((uint)childExitCode);
            AppHost.WriteString(NewLine);
        }

        private static void ResolveLaunchAbi(string filename, out uint abiVersion, out AppServiceAbi serviceAbi)
        {
            if (IsHelloCsElfName(filename))
            {
                abiVersion = AppStartupBlock.AbiVersionV2;
                serviceAbi = AppServiceAbi.SystemV;
                return;
            }

            abiVersion = AppStartupBlock.AbiVersionV1;
            serviceAbi = AppServiceAbi.WindowsX64;
        }

        private static bool IsHelloCsElfName(string filename)
        {
            if (filename == null || filename.Length != 11)
                return false;

            return
                filename[0] == 'H' &&
                filename[1] == 'E' &&
                filename[2] == 'L' &&
                filename[3] == 'L' &&
                filename[4] == 'O' &&
                filename[5] == 'C' &&
                filename[6] == 'S' &&
                filename[7] == '.' &&
                filename[8] == 'E' &&
                filename[9] == 'L' &&
                filename[10] == 'F';
        }

        internal static unsafe class AppString
        {
            public static string FromAscii(byte* src, uint len)
            {
                if (src == null || len == 0)
                    return string.Empty;

                string s = new string('\0', (int)len);
                fixed (char* dst = s)
                {
                    for (uint i = 0; i < len; i++)
                    {
                        byte b = src[i];
                        dst[i] = b <= 0x7F ? (char)b : '?';
                    }
                }

                return s;
            }

            public static string FromAsciiZ(byte* src)
            {
                if (src == null)
                    return string.Empty;

                uint len = 0;
                while (src[len] != 0)
                    len++;

                return FromAscii(src, len);
            }
        }
    }
}
