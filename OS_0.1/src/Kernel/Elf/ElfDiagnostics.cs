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

        public static void WriteLoadError(ElfLoadError error)
        {
            Log.Begin(LogLevel.Error);
            Console.Write("elf load error: ");
            WriteLoadErrorName(error);
            Log.EndLine();
        }

        public static void DumpLoadedImage(ElfLoadedImage image)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("elf loaded segments/pages: ");
            Console.WriteUInt(image.LoadedSegmentCount);
            Console.Write("/");
            Console.WriteULong(image.LoadedPages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("elf loaded range: 0x");
            Console.WriteHex(image.LowestVirtualAddress, 16);
            Console.Write("..0x");
            Console.WriteHex(image.HighestVirtualAddressExclusive, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("elf entry ready = 0x");
            Console.WriteHex(image.EntryPoint, 16);
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

        internal static void WriteProgramFlags(uint flags)
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

        private static void WriteLoadErrorName(ElfLoadError error)
        {
            switch (error)
            {
                case ElfLoadError.NoLoadSegments: Console.Write("no_load_segments"); break;
                case ElfLoadError.ProgramHeaderReadFailed: Console.Write("phdr_read_failed"); break;
                case ElfLoadError.SegmentFileSizeExceedsMemorySize: Console.Write("segment_filesz_gt_memsz"); break;
                case ElfLoadError.SegmentAddressOverflow: Console.Write("segment_address_overflow"); break;
                case ElfLoadError.SegmentFileRangeOutOfBounds: Console.Write("segment_file_range_out_of_bounds"); break;
                case ElfLoadError.SegmentPageMapFailed: Console.Write("segment_page_map_failed"); break;
                case ElfLoadError.SegmentCopyFailed: Console.Write("segment_copy_failed"); break;
                case ElfLoadError.SegmentZeroFillFailed: Console.Write("segment_zero_fill_failed"); break;
                default: Console.Write("unknown"); break;
            }
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
