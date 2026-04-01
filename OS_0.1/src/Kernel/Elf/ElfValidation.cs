using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static class ElfValidation
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "elf validation start");

            if (!ElfSmokeImage.TryCreate(out MemoryBlock image))
                Panic.Fail("elf smoke image allocation failed");

            if (!ElfParser.TryParse(image, out ElfParseResult result, out ElfParseError error))
            {
                ElfDiagnostics.WriteParseError(error);
                Panic.Fail("elf parser failed");
            }

            ValidateSegments(ref result);
            ElfDiagnostics.DumpSummary(ref result);
            Log.Write(LogLevel.Info, "elf validation done");
        }

        private static void ValidateSegments(ref ElfParseResult result)
        {
            uint loadSegments = 0;

            for (ushort i = 0; i < result.Header.ProgramHeaderCount; i++)
            {
                KernelAssert.True(
                    ElfParser.TryGetProgramHeader(ref result, i, out Elf64ProgramHeader header),
                    "elf validation: phdr read failed");

                if (header.Type != ElfProgramType.Load)
                    continue;

                KernelAssert.True(
                    header.FileSize <= header.MemorySize,
                    "elf validation: filesz greater than memsz");

                if (header.Align != 0)
                {
                    ulong mask = header.Align - 1;
                    KernelAssert.True(
                        (header.Align & mask) == 0,
                        "elf validation: segment align is not power of two");
                }

                loadSegments++;
            }

            KernelAssert.True(loadSegments != 0, "elf validation: no PT_LOAD segments");
        }
    }
}
