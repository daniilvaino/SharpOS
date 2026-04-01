using OS.Hal;

namespace OS.Kernel.Elf
{
    internal static class ElfDiagnostics
    {
        private const uint ProgramFlagExecute = 1U << 0;
        private const uint ProgramFlagWrite = 1U << 1;
        private const uint ProgramFlagRead = 1U << 2;

        public static void DumpSummary(ref ElfParseResult result)
        {
            Log.Write(LogLevel.Info, "elf ok");

            Log.Begin(LogLevel.Info);
            Console.Write("elf entry = 0x");
            Console.WriteHex(result.Header.Entry, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("elf phdr count = ");
            Console.WriteUInt(result.Header.ProgramHeaderCount);
            Log.EndLine();

            uint loadIndex = 0;
            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                if (!ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header))
                {
                    Log.Write(LogLevel.Warn, "elf phdr read failed");
                    continue;
                }

                if (header.Type != ElfProgramType.Load)
                    continue;

                DumpLoadSegment(loadIndex, header);
                loadIndex++;
            }

            if (loadIndex == 0)
                Log.Write(LogLevel.Warn, "elf has no PT_LOAD segments");
        }

        public static void WriteParseError(ElfParseError error)
        {
            Log.Begin(LogLevel.Error);
            Console.Write("elf parse error: ");
            WriteParseErrorName(error);
            Log.EndLine();
        }

        private static void DumpLoadSegment(uint loadIndex, Elf64ProgramHeader header)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("load segment ");
            Console.WriteUInt(loadIndex);
            Console.Write(": vaddr=0x");
            Console.WriteHex(header.VirtualAddress, 16);
            Console.Write(" offset=0x");
            Console.WriteHex(header.Offset, 8);
            Console.Write(" filesz=");
            Console.WriteULong(header.FileSize);
            Console.Write(" memsz=");
            Console.WriteULong(header.MemorySize);
            Console.Write(" flags=");
            WriteProgramFlags(header.Flags);
            Log.EndLine();
        }

        private static void WriteProgramFlags(uint flags)
        {
            bool first = true;

            if ((flags & ProgramFlagRead) != 0)
            {
                Console.Write("R");
                first = false;
            }

            if ((flags & ProgramFlagWrite) != 0)
            {
                if (!first)
                    Console.Write("|");

                Console.Write("W");
                first = false;
            }

            if ((flags & ProgramFlagExecute) != 0)
            {
                if (!first)
                    Console.Write("|");

                Console.Write("X");
                first = false;
            }

            if (first)
                Console.Write("None");
        }

        private static void WriteParseErrorName(ElfParseError error)
        {
            switch (error)
            {
                case ElfParseError.NullOrEmptyImage: Console.Write("null_or_empty_image"); break;
                case ElfParseError.HeaderTooSmall: Console.Write("header_too_small"); break;
                case ElfParseError.BadMagic: Console.Write("bad_magic"); break;
                case ElfParseError.UnsupportedClass: Console.Write("unsupported_class"); break;
                case ElfParseError.UnsupportedEndianness: Console.Write("unsupported_endianness"); break;
                case ElfParseError.UnsupportedVersion: Console.Write("unsupported_version"); break;
                case ElfParseError.UnsupportedType: Console.Write("unsupported_type"); break;
                case ElfParseError.UnsupportedMachine: Console.Write("unsupported_machine"); break;
                case ElfParseError.UnsupportedHeaderSize: Console.Write("unsupported_header_size"); break;
                case ElfParseError.UnsupportedProgramHeaderSize: Console.Write("unsupported_phdr_size"); break;
                case ElfParseError.ProgramHeaderTableOutOfBounds: Console.Write("phdr_table_out_of_bounds"); break;
                case ElfParseError.ProgramHeaderOutOfBounds: Console.Write("phdr_out_of_bounds"); break;
                case ElfParseError.HeaderReadFailed: Console.Write("header_read_failed"); break;
                default: Console.Write("unknown"); break;
            }
        }
    }
}
