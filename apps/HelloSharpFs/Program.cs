using SharpOS.AppSdk;
using System;
using System.Runtime;

namespace HelloSharpFs
{
    internal static unsafe class AppEntry
    {
        private const int ExitCode = 21;
        private const bool EnableLauncherExplorer = true;
        private const bool EnableStringExperimentMode = false;
        private const string Banner = "string exp start\n";
        private const string AbiPrefix = "abi=";
        private const string TestIdPrefix = "test_id=";
        private const string ResultPrefix = "test_result=";
        private const string BootDirectory = "\\EFI\\BOOT";
        private const uint MaxDirectoryScanEntries = 256;
        private const uint MaxNameBytes = 256;
        private const ushort ScanCodeUp = 0x01;
        private const ushort ScanCodeDown = 0x02;
        private const ushort ScanCodeRight = 0x03;
        private const ushort ScanCodeLeft = 0x04;
        private const ushort ScanCodeEscape = 0x17;
        private const ushort CharEnter = 0x000D;
        private const ushort CharEscape = 0x001B;
        private const ushort CharRLower = (ushort)'r';
        private const ushort CharRUpper = (ushort)'R';
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

            if (EnableStringExperimentMode)
            {
                uint result = StringExperimentSuite.RunSelected(out uint testId);
                if (testId != 0)
                {
                    AppHost.WriteString(TestIdPrefix);
                    AppHost.WriteUInt(testId);
                    AppHost.WriteString(NewLine);

                    AppHost.WriteString(ResultPrefix);
                    AppHost.WriteUInt(result);
                    AppHost.WriteString(NewLine);

                    AppHost.Exit(ExitCode);
                    return;
                }
            }

            if (!EnableLauncherExplorer)
            {
                AppHost.Exit(ExitCode);
                return;
            }

            uint selectedIndex = 0;
            uint visibleCount = 0;
            bool needsRedraw = true;

            for (; ; )
            {
                if (needsRedraw)
                {
                    RenderLauncher(selectedIndex, out visibleCount);
                    if (visibleCount == 0)
                    {
                        selectedIndex = 0;
                    }
                    else if (selectedIndex >= visibleCount)
                    {
                        selectedIndex = visibleCount - 1;
                    }

                    needsRedraw = false;
                }

                KeyInfo keyInfo;
                if (AppHost.TryReadKey(out keyInfo) != AppServiceStatus.Ok)
                    continue;

                if (IsEscape(keyInfo))
                {
                    AppHost.Exit(ExitCode);
                    return;
                }

                if (IsRefresh(keyInfo))
                {
                    needsRedraw = true;
                    continue;
                }

                if (IsNext(keyInfo) && visibleCount != 0)
                {
                    if (selectedIndex + 1 >= visibleCount)
                    {
                        selectedIndex = 0;
                    }
                    else
                    {
                        selectedIndex++;
                    }

                    needsRedraw = true;
                    continue;
                }

                if (IsPrevious(keyInfo) && visibleCount != 0)
                {
                    if (selectedIndex == 0)
                    {
                        selectedIndex = visibleCount - 1;
                    }
                    else
                    {
                        selectedIndex--;
                    }

                    needsRedraw = true;
                    continue;
                }

                if (!IsEnter(keyInfo))
                    continue;

                if (visibleCount == 0)
                {
                    WriteResultBlock("none", AppServiceStatus.NotFound, 0);
                    needsRedraw = true;
                    continue;
                }

                byte* selectedNameBuffer = stackalloc byte[(int)MaxNameBytes];
                if (!TryRunSelectedEntry(selectedIndex, selectedNameBuffer, MaxNameBytes, out uint selectedNameLength, out AppServiceStatus runStatus, out int childExitCode))
                {
                    WriteResultBlock("none", AppServiceStatus.NotFound, 0);
                    needsRedraw = true;
                    continue;
                }

                string selectedName = string.FromAscii(selectedNameBuffer, selectedNameLength);
                WriteResultBlock(selectedName, runStatus, childExitCode);
                needsRedraw = true;
            }
        }

        private static void RenderLauncher(uint selectedIndex, out uint visibleCount)
        {
            visibleCount = 0;
            byte* entryName = stackalloc byte[(int)MaxNameBytes];
            FileEntry fileEntry = default;

            AppHost.WriteString("================ LAUNCHER ================\n");
            for (uint i = 0; i < MaxDirectoryScanEntries; i++)
            {
                AppServiceStatus status = AppHost.TryReadDirEntry(BootDirectory, i, entryName, MaxNameBytes, out fileEntry);
                if (status == AppServiceStatus.EndOfDirectory)
                    break;

                if (status != AppServiceStatus.Ok)
                    continue;

                if (!IsVisibleLauncherEntry(entryName, fileEntry.NameLength, fileEntry.IsDirectory))
                    continue;

                string fileName = string.FromAscii(entryName, fileEntry.NameLength);
                AppHost.WriteString(visibleCount == selectedIndex ? "> " : "  ");
                AppHost.WriteString(fileName);
                AppHost.WriteString(NewLine);
                visibleCount++;
            }

            if (visibleCount == 0)
            {
                AppHost.WriteString("(no .ELF files)\n");
            }

            AppHost.WriteString(NewLine);
            AppHost.WriteString("Enter=run  R=refresh  Esc=exit\n");
            AppHost.WriteString("==========================================\n");
        }

        private static bool TryRunSelectedEntry(
            uint selectedIndex,
            byte* selectedNameBuffer,
            uint selectedNameBufferCapacity,
            out uint selectedNameLength,
            out AppServiceStatus runStatus,
            out int exitCode)
        {
            selectedNameLength = 0;
            runStatus = AppServiceStatus.NotFound;
            exitCode = 0;
            if (selectedNameBuffer == null || selectedNameBufferCapacity == 0)
                return false;

            byte* entryName = stackalloc byte[(int)MaxNameBytes];
            FileEntry fileEntry = default;
            uint visibleIndex = 0;

            for (uint i = 0; i < MaxDirectoryScanEntries; i++)
            {
                AppServiceStatus status = AppHost.TryReadDirEntry(BootDirectory, i, entryName, MaxNameBytes, out fileEntry);
                if (status == AppServiceStatus.EndOfDirectory)
                    break;

                if (status != AppServiceStatus.Ok)
                    continue;

                if (!IsVisibleLauncherEntry(entryName, fileEntry.NameLength, fileEntry.IsDirectory))
                    continue;

                if (visibleIndex == selectedIndex)
                {
                    if (fileEntry.NameLength + 1 > selectedNameBufferCapacity)
                        return false;

                    SharpOS.Std.NoRuntime.MemoryPrimitives.Memcpy(
                        (void*)selectedNameBuffer, (void*)entryName, (ulong)fileEntry.NameLength);

                    selectedNameBuffer[fileEntry.NameLength] = 0;
                    selectedNameLength = fileEntry.NameLength;

                    string selectedName = string.FromAscii(selectedNameBuffer, selectedNameLength);
                    string path = StringAlgorithms.Concat(BootDirectory, @"\");
                    path = StringAlgorithms.Concat(path, selectedName);
                    runStatus = AppHost.TryRunApp(path, out exitCode);
                    return true;
                }

                visibleIndex++;
            }

            return false;
        }

        private static bool IsVisibleLauncherEntry(byte* name, uint nameLength, uint isDirectory)
        {
            if (name == null || nameLength < 4 || isDirectory != 0)
                return false;

            byte c0 = name[nameLength - 4];
            byte c1 = name[nameLength - 3];
            byte c2 = name[nameLength - 2];
            byte c3 = name[nameLength - 1];

            if (c0 != (byte)'.')
                return false;

            return IsAsciiLetter(c1, (byte)'E') &&
                   IsAsciiLetter(c2, (byte)'L') &&
                   IsAsciiLetter(c3, (byte)'F');
        }

        private static bool IsAsciiLetter(byte value, byte upper)
        {
            if (value == upper)
                return true;

            return value == (byte)(upper + 32);
        }

        private static bool IsEnter(KeyInfo keyInfo)
        {
            return keyInfo.UnicodeChar == CharEnter;
        }

        private static bool IsEscape(KeyInfo keyInfo)
        {
            return keyInfo.UnicodeChar == CharEscape || keyInfo.ScanCode == ScanCodeEscape;
        }

        private static bool IsRefresh(KeyInfo keyInfo)
        {
            return keyInfo.UnicodeChar == CharRLower || keyInfo.UnicodeChar == CharRUpper;
        }

        private static bool IsNext(KeyInfo keyInfo)
        {
            return keyInfo.ScanCode == ScanCodeDown || keyInfo.ScanCode == ScanCodeRight;
        }

        private static bool IsPrevious(KeyInfo keyInfo)
        {
            return keyInfo.ScanCode == ScanCodeUp || keyInfo.ScanCode == ScanCodeLeft;
        }

        private static void WriteResultBlock(string name, AppServiceStatus status, int exitCode)
        {
            AppHost.WriteString("---- app result ----\n");
            AppHost.WriteString("name: ");
            AppHost.WriteString(name);
            AppHost.WriteString(NewLine);
            AppHost.WriteString("status: ");
            AppHost.WriteString(StatusName(status));
            AppHost.WriteString(NewLine);
            AppHost.WriteString("exit: ");
            AppHost.WriteUInt((uint)exitCode);
            AppHost.WriteString(NewLine);
            AppHost.WriteString("--------------------\n");
            AppHost.WriteString(NewLine);
        }

        private static string StatusName(AppServiceStatus status)
        {
            switch (status)
            {
                case AppServiceStatus.Ok: return "ok";
                case AppServiceStatus.NoData: return "no_data";
                case AppServiceStatus.NotFound: return "not_found";
                case AppServiceStatus.EndOfDirectory: return "end_of_directory";
                case AppServiceStatus.BufferTooSmall: return "buffer_too_small";
                case AppServiceStatus.InvalidParameter: return "invalid_parameter";
                case AppServiceStatus.Unsupported: return "unsupported";
                case AppServiceStatus.DeviceError: return "device_error";
                default: return "unknown";
            }
        }

    }
}
