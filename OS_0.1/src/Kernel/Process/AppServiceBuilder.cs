using OS.Hal;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    internal static unsafe class AppServiceBuilder
    {
        private const int MaxWriteStringBytes = 512;
        private const uint SystemVThunkPageSize = 4096;
        private const uint SystemVOneArgThunkSize = 24;
        private const uint SystemVNoArgThunkSize = 21;

        private static int s_exitRequested;
        private static int s_exitCode;
        private static bool s_systemVThunksInitialized;
        private static ulong s_systemVWriteStringThunk;
        private static ulong s_systemVWriteUIntThunk;
        private static ulong s_systemVWriteHexThunk;
        private static ulong s_systemVGetAbiVersionThunk;
        private static ulong s_systemVExitThunk;

        public static bool TryBuild(ulong serviceVirtual, AppServiceAbi serviceAbi, out ulong servicePhysical)
        {
            servicePhysical = 0;

            if (!Pager.TryQuery(serviceVirtual, out servicePhysical, out _))
                return false;

            delegate* managed<ulong, void> writeStringAddress = &WriteString;
            delegate* managed<uint, void> writeUIntAddress = &WriteUInt;
            delegate* managed<ulong, void> writeHexAddress = &WriteHex;
            delegate* managed<uint> getAbiVersionAddress = &GetAbiVersion;
            delegate* managed<int, void> exitAddress = &Exit;
            ulong tableWriteStringAddress = (ulong)writeStringAddress;
            ulong tableWriteUIntAddress = (ulong)writeUIntAddress;
            ulong tableWriteHexAddress = (ulong)writeHexAddress;
            ulong tableGetAbiVersionAddress = (ulong)getAbiVersionAddress;
            ulong tableExitAddress = (ulong)exitAddress;

            if (serviceAbi == AppServiceAbi.SystemV)
            {
                if (!EnsureSystemVThunks(
                    (ulong)writeStringAddress,
                    (ulong)writeUIntAddress,
                    (ulong)writeHexAddress,
                    (ulong)getAbiVersionAddress,
                    (ulong)exitAddress))
                {
                    return false;
                }

                tableWriteStringAddress = s_systemVWriteStringThunk;
                tableWriteUIntAddress = s_systemVWriteUIntThunk;
                tableWriteHexAddress = s_systemVWriteHexThunk;
                tableGetAbiVersionAddress = s_systemVGetAbiVersionThunk;
                tableExitAddress = s_systemVExitThunk;
            }

            AppServiceTable table = default;
            table.AbiVersion = AppServiceTable.CurrentAbiVersion;
            table.Reserved = 0;
            table.WriteStringAddress = tableWriteStringAddress;
            table.WriteUIntAddress = tableWriteUIntAddress;
            table.WriteHexAddress = tableWriteHexAddress;
            table.GetAbiVersionAddress = tableGetAbiVersionAddress;
            table.ExitAddress = tableExitAddress;

            *((AppServiceTable*)servicePhysical) = table;
            s_exitRequested = 0;
            s_exitCode = 0;
            return true;
        }

        private static bool EnsureSystemVThunks(
            ulong writeStringTarget,
            ulong writeUIntTarget,
            ulong writeHexTarget,
            ulong getAbiVersionTarget,
            ulong exitTarget)
        {
            if (s_systemVThunksInitialized)
                return true;

            ulong thunkPage = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (thunkPage == 0)
                return false;

            global::OS.Kernel.Util.Memory.Zero((void*)thunkPage, SystemVThunkPageSize);

            byte* page = (byte*)thunkPage;
            uint cursor = 0;

            s_systemVWriteStringThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeStringTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVWriteUIntThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeUIntTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVWriteHexThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, writeHexTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVGetAbiVersionThunk = thunkPage + cursor;
            if (!TryWriteSystemVNoArgThunk(page + cursor, getAbiVersionTarget))
                return false;
            cursor += SystemVNoArgThunkSize;

            s_systemVExitThunk = thunkPage + cursor;
            if (!TryWriteSystemVOneArgThunk(page + cursor, exitTarget))
                return false;
            cursor += SystemVOneArgThunkSize;

            s_systemVThunksInitialized = true;
            return true;
        }

        private static bool TryWriteSystemVOneArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rax, imm64
            destination[0] = 0x48;
            destination[1] = 0xB8;
            WriteU64(destination + 2, target);

            // mov rcx, rdi
            destination[10] = 0x48;
            destination[11] = 0x89;
            destination[12] = 0xF9;

            // sub rsp, 0x28
            destination[13] = 0x48;
            destination[14] = 0x83;
            destination[15] = 0xEC;
            destination[16] = 0x28;

            // call rax
            destination[17] = 0xFF;
            destination[18] = 0xD0;

            // add rsp, 0x28
            destination[19] = 0x48;
            destination[20] = 0x83;
            destination[21] = 0xC4;
            destination[22] = 0x28;

            // ret
            destination[23] = 0xC3;
            return true;
        }

        private static bool TryWriteSystemVNoArgThunk(byte* destination, ulong target)
        {
            if (destination == null || target == 0)
                return false;

            // mov rax, imm64
            destination[0] = 0x48;
            destination[1] = 0xB8;
            WriteU64(destination + 2, target);

            // sub rsp, 0x28
            destination[10] = 0x48;
            destination[11] = 0x83;
            destination[12] = 0xEC;
            destination[13] = 0x28;

            // call rax
            destination[14] = 0xFF;
            destination[15] = 0xD0;

            // add rsp, 0x28
            destination[16] = 0x48;
            destination[17] = 0x83;
            destination[18] = 0xC4;
            destination[19] = 0x28;

            // ret
            destination[20] = 0xC3;
            return true;
        }

        private static void WriteU64(byte* destination, ulong value)
        {
            destination[0] = (byte)(value & 0xFF);
            destination[1] = (byte)((value >> 8) & 0xFF);
            destination[2] = (byte)((value >> 16) & 0xFF);
            destination[3] = (byte)((value >> 24) & 0xFF);
            destination[4] = (byte)((value >> 32) & 0xFF);
            destination[5] = (byte)((value >> 40) & 0xFF);
            destination[6] = (byte)((value >> 48) & 0xFF);
            destination[7] = (byte)((value >> 56) & 0xFF);
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

        private static void WriteUInt(uint value)
        {
            Console.WriteUInt(value);
        }

        private static void WriteHex(ulong value)
        {
            Console.Write("0x");
            Console.WriteHex(value, 16);
        }

        private static uint GetAbiVersion()
        {
            return AppServiceTable.CurrentAbiVersion;
        }

        private static void Exit(int exitCode)
        {
            s_exitCode = exitCode;
            s_exitRequested = 1;
        }
    }
}
