using OS.Hal;
using OS.Kernel.Paging;

namespace OS.Kernel.Process
{
    internal static unsafe class AppServiceBuilder
    {
        private const int MaxWriteStringBytes = 512;

        private static int s_exitRequested;
        private static int s_exitCode;

        public static bool TryBuild(ulong serviceVirtual, out ulong servicePhysical)
        {
            servicePhysical = 0;

            if (!Pager.TryQuery(serviceVirtual, out servicePhysical, out _))
                return false;

            delegate* managed<ulong, void> writeStringAddress = &WriteString;
            delegate* managed<int, void> exitAddress = &Exit;

            AppServiceTable table = default;
            table.AbiVersion = AppServiceTable.CurrentAbiVersion;
            table.Reserved = 0;
            table.WriteStringAddress = (ulong)writeStringAddress;
            table.ExitAddress = (ulong)exitAddress;

            *((AppServiceTable*)servicePhysical) = table;
            s_exitRequested = 0;
            s_exitCode = 0;
            return true;
        }

        public static bool TryConsumeExit(out int exitCode)
        {
            exitCode = 0;
            if (s_exitRequested == 0)
                return false;

            exitCode = s_exitCode;
            s_exitRequested = 0;
            return true;
        }

        private static void WriteString(ulong textAddress)
        {
            if (textAddress == 0)
                return;

            byte* pointer = (byte*)textAddress;
            for (int i = 0; i < MaxWriteStringBytes; i++)
            {
                byte value = pointer[i];
                if (value == 0)
                    return;

                Console.WriteChar((char)value);
            }
        }

        private static void Exit(int exitCode)
        {
            s_exitCode = exitCode;
            s_exitRequested = 1;
        }
    }
}
