using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static class ElfParser
    {
        private const byte ExpectedMagic0 = 0x7F;
        private const byte ExpectedMagic1 = (byte)'E';
        private const byte ExpectedMagic2 = (byte)'L';
        private const byte ExpectedMagic3 = (byte)'F';

        private const byte ExpectedClass64 = 2;
        private const byte ExpectedLittleEndian = 1;
        private const byte ExpectedIdentVersion = 1;
        private const uint ExpectedVersion = 1;

        public static bool TryParse(MemoryBlock image, out ElfParseResult result, out ElfParseError error)
        {
            result = default;
            error = ElfParseError.None;

            if (!image.IsValid)
            {
                error = ElfParseError.NullOrEmptyImage;
                return false;
            }

            if (image.Length < Elf64Header.Size)
            {
                error = ElfParseError.HeaderTooSmall;
                return false;
            }

            if (!TryReadHeader(image, out Elf64Header header))
            {
                error = ElfParseError.HeaderReadFailed;
                return false;
            }

            if (!HasValidMagic(image))
            {
                error = ElfParseError.BadMagic;
                return false;
            }

            if (header.Class != ExpectedClass64)
            {
                error = ElfParseError.UnsupportedClass;
                return false;
            }

            if (header.DataEncoding != ExpectedLittleEndian)
            {
                error = ElfParseError.UnsupportedEndianness;
                return false;
            }

            if (header.IdentVersion != ExpectedIdentVersion || header.Version != ExpectedVersion)
            {
                error = ElfParseError.UnsupportedVersion;
                return false;
            }

            if (header.Type != ElfType.Executable && header.Type != ElfType.SharedObject)
            {
                error = ElfParseError.UnsupportedType;
                return false;
            }

            if (header.Machine != ElfMachine.X86_64)
            {
                error = ElfParseError.UnsupportedMachine;
                return false;
            }

            if (header.HeaderSize != Elf64Header.Size)
            {
                error = ElfParseError.UnsupportedHeaderSize;
                return false;
            }

            if (header.ProgramHeaderEntrySize < Elf64Header.ProgramHeaderSize)
            {
                error = ElfParseError.UnsupportedProgramHeaderSize;
                return false;
            }

            if (!IsProgramHeaderTableInBounds(image.Length, header))
            {
                error = ElfParseError.ProgramHeaderTableOutOfBounds;
                return false;
            }

            result.Image = image;
            result.Header = header;
            return true;
        }

        public static bool TryGetProgramHeader(ref ElfParseResult result, ushort index, out Elf64ProgramHeader header)
        {
            header = default;
            if (index >= result.Header.ProgramHeaderCount)
                return false;

            ulong entryOffset = result.Header.ProgramHeaderOffset + ((ulong)index * result.Header.ProgramHeaderEntrySize);
            if (!CanReadBlock(result.Image.Length, entryOffset, Elf64ProgramHeader.Size))
                return false;

            if (!TryReadUInt32(result.Image, entryOffset + 0, out uint type))
                return false;

            if (!TryReadUInt32(result.Image, entryOffset + 4, out uint flags))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 8, out ulong fileOffset))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 16, out ulong virtualAddress))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 24, out ulong physicalAddress))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 32, out ulong fileSize))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 40, out ulong memorySize))
                return false;

            if (!TryReadUInt64(result.Image, entryOffset + 48, out ulong align))
                return false;

            header.Type = (ElfProgramType)type;
            header.Flags = flags;
            header.Offset = fileOffset;
            header.VirtualAddress = virtualAddress;
            header.PhysicalAddress = physicalAddress;
            header.FileSize = fileSize;
            header.MemorySize = memorySize;
            header.Align = align;
            return true;
        }

        private static bool TryReadHeader(MemoryBlock image, out Elf64Header header)
        {
            header = default;

            if (!image.TryReadByte(4, out byte elfClass))
                return false;

            if (!image.TryReadByte(5, out byte dataEncoding))
                return false;

            if (!image.TryReadByte(6, out byte identVersion))
                return false;

            if (!image.TryReadUInt16(16, out ushort type))
                return false;

            if (!image.TryReadUInt16(18, out ushort machine))
                return false;

            if (!image.TryReadUInt32(20, out uint version))
                return false;

            if (!image.TryReadUInt64(24, out ulong entry))
                return false;

            if (!image.TryReadUInt64(32, out ulong programHeaderOffset))
                return false;

            if (!image.TryReadUInt16(52, out ushort headerSize))
                return false;

            if (!image.TryReadUInt16(54, out ushort programHeaderEntrySize))
                return false;

            if (!image.TryReadUInt16(56, out ushort programHeaderCount))
                return false;

            header.Class = elfClass;
            header.DataEncoding = dataEncoding;
            header.IdentVersion = identVersion;
            header.Type = (ElfType)type;
            header.Machine = (ElfMachine)machine;
            header.Version = version;
            header.Entry = entry;
            header.ProgramHeaderOffset = programHeaderOffset;
            header.HeaderSize = headerSize;
            header.ProgramHeaderEntrySize = programHeaderEntrySize;
            header.ProgramHeaderCount = programHeaderCount;
            return true;
        }

        private static bool HasValidMagic(MemoryBlock image)
        {
            return
                image.TryReadByte(0, out byte m0) && m0 == ExpectedMagic0 &&
                image.TryReadByte(1, out byte m1) && m1 == ExpectedMagic1 &&
                image.TryReadByte(2, out byte m2) && m2 == ExpectedMagic2 &&
                image.TryReadByte(3, out byte m3) && m3 == ExpectedMagic3;
        }

        private static bool IsProgramHeaderTableInBounds(uint imageLength, Elf64Header header)
        {
            ulong tableSize = (ulong)header.ProgramHeaderEntrySize * header.ProgramHeaderCount;
            return CanReadBlock(imageLength, header.ProgramHeaderOffset, tableSize);
        }

        private static bool CanReadBlock(uint imageLength, ulong offset, ulong size)
        {
            if (size == 0)
                return true;

            if (offset > imageLength)
                return false;

            ulong remaining = (ulong)imageLength - offset;
            return size <= remaining;
        }

        private static bool TryReadUInt16(MemoryBlock image, ulong offset, out ushort value)
        {
            value = 0;
            if (offset > 0xFFFFFFFFUL)
                return false;

            return image.TryReadUInt16((uint)offset, out value);
        }

        private static bool TryReadUInt32(MemoryBlock image, ulong offset, out uint value)
        {
            value = 0;
            if (offset > 0xFFFFFFFFUL)
                return false;

            return image.TryReadUInt32((uint)offset, out value);
        }

        private static bool TryReadUInt64(MemoryBlock image, ulong offset, out ulong value)
        {
            value = 0;
            if (offset > 0xFFFFFFFFUL)
                return false;

            return image.TryReadUInt64((uint)offset, out value);
        }
    }
}
