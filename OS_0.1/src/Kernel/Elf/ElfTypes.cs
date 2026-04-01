using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal enum ElfType : ushort
    {
        None = 0,
        Executable = 2,
        SharedObject = 3,
    }

    internal enum ElfMachine : ushort
    {
        X86_64 = 0x3E,
    }

    internal enum ElfProgramType : uint
    {
        Null = 0,
        Load = 1,
        Dynamic = 2,
        Interpreter = 3,
        Note = 4,
    }

    internal enum ElfParseError : uint
    {
        None = 0,
        NullOrEmptyImage = 1,
        HeaderTooSmall = 2,
        BadMagic = 3,
        UnsupportedClass = 4,
        UnsupportedEndianness = 5,
        UnsupportedVersion = 6,
        UnsupportedType = 7,
        UnsupportedMachine = 8,
        UnsupportedHeaderSize = 9,
        UnsupportedProgramHeaderSize = 10,
        ProgramHeaderTableOutOfBounds = 11,
        ProgramHeaderOutOfBounds = 12,
        HeaderReadFailed = 13,
    }

    internal struct Elf64Header
    {
        public const uint Size = 64;
        public const ushort ProgramHeaderSize = 56;

        public byte Class;
        public byte DataEncoding;
        public byte IdentVersion;
        public ElfType Type;
        public ElfMachine Machine;
        public uint Version;
        public ulong Entry;
        public ulong ProgramHeaderOffset;
        public ushort HeaderSize;
        public ushort ProgramHeaderEntrySize;
        public ushort ProgramHeaderCount;
    }

    internal struct Elf64ProgramHeader
    {
        public const uint Size = 56;

        public ElfProgramType Type;
        public uint Flags;
        public ulong Offset;
        public ulong VirtualAddress;
        public ulong PhysicalAddress;
        public ulong FileSize;
        public ulong MemorySize;
        public ulong Align;
    }

    internal struct ElfParseResult
    {
        public MemoryBlock Image;
        public Elf64Header Header;
    }
}
