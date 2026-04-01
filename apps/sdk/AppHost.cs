namespace SharpOS.AppSdk
{
    internal static unsafe class AppHost
    {
        public static void WriteString(byte* text)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null || text == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteStringAddress;
            if (write == null)
                return;

            write((ulong)text);
        }

        public static void WriteUInt(uint value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<uint, void> write = (delegate* unmanaged<uint, void>)services->WriteUIntAddress;
            if (write == null)
                return;

            write(value);
        }

        public static void WriteHex(ulong value)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<ulong, void> write = (delegate* unmanaged<ulong, void>)services->WriteHexAddress;
            if (write == null)
                return;

            write(value);
        }

        public static uint GetAbiVersion()
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return 0;

            delegate* unmanaged<uint> getVersion = (delegate* unmanaged<uint>)services->GetAbiVersionAddress;
            if (getVersion == null)
                return 0;

            return getVersion();
        }

        public static void Exit(int exitCode)
        {
            AppServiceTable* services = AppRuntime.Services;
            if (services == null)
                return;

            delegate* unmanaged<int, void> exit = (delegate* unmanaged<int, void>)services->ExitAddress;
            if (exit == null)
                return;

            exit(exitCode);
        }
    }
}
