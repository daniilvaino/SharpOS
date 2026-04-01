using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static unsafe class ElfSmokeImage
    {
        private const uint ImageSize = 4096;
        private const ulong ProgramHeaderOffset = Elf64Header.Size;
        private const ushort ProgramHeaderCount = 3;

        public static bool TryCreate(out MemoryBlock image)
        {
            image = default;

            ulong physicalPage = global::OS.Kernel.PhysicalMemory.AllocPage();
            if (physicalPage == 0)
                return false;

            image = new MemoryBlock((void*)physicalPage, ImageSize);
            image.Clear();

            if (!WriteHeader(image))
                return false;

            if (!WriteProgramHeader(
                image,
                0,
                ElfProgramType.Load,
                5,
                0x100,
                0x0000000000400000UL,
                0x80,
                0x80,
                0x1000))
            {
                return false;
            }

            if (!WriteProgramHeader(
                image,
                1,
                ElfProgramType.Load,
                6,
                0x200,
                0x0000000000401000UL,
                0x40,
                0x100,
                0x1000))
            {
                return false;
            }

            if (!WriteProgramHeader(
                image,
                2,
                ElfProgramType.Note,
                4,
                0x300,
                0,
                0x20,
                0x20,
                8))
            {
                return false;
            }

            WritePattern(image, 0x100, 0x80, 0x11);
            WritePattern(image, 0x200, 0x40, 0x22);
            return true;
        }

        private static bool WriteHeader(MemoryBlock image)
        {
            return
                image.TryWriteByte(0, 0x7F) &&
                image.TryWriteByte(1, (byte)'E') &&
                image.TryWriteByte(2, (byte)'L') &&
                image.TryWriteByte(3, (byte)'F') &&
                image.TryWriteByte(4, 2) &&
                image.TryWriteByte(5, 1) &&
                image.TryWriteByte(6, 1) &&
                image.TryWriteUInt16(16, (ushort)ElfType.Executable) &&
                image.TryWriteUInt16(18, (ushort)ElfMachine.X86_64) &&
                image.TryWriteUInt32(20, 1) &&
                image.TryWriteUInt64(24, 0x0000000000400010UL) &&
                image.TryWriteUInt64(32, ProgramHeaderOffset) &&
                image.TryWriteUInt64(40, 0) &&
                image.TryWriteUInt32(48, 0) &&
                image.TryWriteUInt16(52, (ushort)Elf64Header.Size) &&
                image.TryWriteUInt16(54, (ushort)Elf64ProgramHeader.Size) &&
                image.TryWriteUInt16(56, ProgramHeaderCount) &&
                image.TryWriteUInt16(58, 0) &&
                image.TryWriteUInt16(60, 0) &&
                image.TryWriteUInt16(62, 0);
        }

        private static bool WriteProgramHeader(
            MemoryBlock image,
            uint index,
            ElfProgramType type,
            uint flags,
            ulong fileOffset,
            ulong virtualAddress,
            ulong fileSize,
            ulong memorySize,
            ulong align)
        {
            ulong start = ProgramHeaderOffset + (index * Elf64ProgramHeader.Size);
            if (start > 0xFFFFFFFFUL)
                return false;

            uint offset = (uint)start;

            return
                image.TryWriteUInt32(offset + 0, (uint)type) &&
                image.TryWriteUInt32(offset + 4, flags) &&
                image.TryWriteUInt64(offset + 8, fileOffset) &&
                image.TryWriteUInt64(offset + 16, virtualAddress) &&
                image.TryWriteUInt64(offset + 24, 0) &&
                image.TryWriteUInt64(offset + 32, fileSize) &&
                image.TryWriteUInt64(offset + 40, memorySize) &&
                image.TryWriteUInt64(offset + 48, align);
        }

        private static void WritePattern(MemoryBlock image, uint offset, uint count, byte seed)
        {
            for (uint i = 0; i < count; i++)
                image.TryWriteByte(offset + i, (byte)(seed + (byte)(i & 0x0F)));
        }
    }
}
