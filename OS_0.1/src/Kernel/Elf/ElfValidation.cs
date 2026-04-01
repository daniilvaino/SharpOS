using OS.Boot;
using OS.Hal;
using OS.Kernel.Diagnostics;
using OS.Kernel.Exec;
using OS.Kernel.Paging;
using OS.Kernel.Process;
using OS.Kernel.Util;

namespace OS.Kernel.Elf
{
    internal static unsafe class ElfValidation
    {
        public static void Run(BootInfo bootInfo)
        {
            Log.Write(LogLevel.Info, "elf validation start");

            if (bootInfo.ExternalElfImage == null || bootInfo.ExternalElfImageSize == 0)
                Panic.Fail("external elf image is not available");

            MemoryBlock image = new MemoryBlock(bootInfo.ExternalElfImage, bootInfo.ExternalElfImageSize);

            Log.Write(LogLevel.Info, "open elf file ok");
            Log.Begin(LogLevel.Info);
            Console.Write("read elf bytes = ");
            Console.WriteUInt(bootInfo.ExternalElfImageSize);
            Log.EndLine();

            if (!ElfParser.TryParse(image, out ElfParseResult result, out ElfParseError error))
            {
                ElfDiagnostics.WriteParseError(error);
                Panic.Fail("elf parser failed");
            }

            ValidateSegments(ref result);
            ElfDiagnostics.DumpSummary(ref result);

            if (!ElfLoader.TryLoad(ref result, out ElfLoadedImage loadedImage, out ElfLoadError loadError))
            {
                ElfDiagnostics.WriteLoadError(loadError);
                Panic.Fail("elf loader failed");
            }

            ElfLoadValidation.Run(ref result, ref loadedImage);
            ElfDiagnostics.DumpLoadedImage(loadedImage);

            Log.Write(LogLevel.Info, "process build start");
            if (!ProcessImageBuilder.TryBuild(ref loadedImage, ElfAppContract.MarkerVirtualAddress, out ProcessImage processImage))
                Panic.Fail("process image build failed");

            ProcessValidation.Run(ref processImage, ref result);
            ProcessDiagnostics.DumpSummary(ref processImage);

            Log.Write(LogLevel.Info, "jump start");
            if (!JumpStub.Run(
                processImage.EntryPointPhysical,
                processImage.StackTopPhysical,
                processImage.StartupBlockPhysical,
                out int exitCode))
            {
                Panic.Fail("jump stub failed");
            }

            processImage.ExitCode = exitCode;
            if (AppServiceBuilder.TryConsumeExit(out int requestedExitCode))
                processImage.ExitCode = requestedExitCode;

            Log.Write(LogLevel.Info, "process returned");
            Log.Begin(LogLevel.Info);
            Console.Write("process exit code = ");
            Console.WriteInt(processImage.ExitCode);
            Log.EndLine();

            KernelAssert.Equal(ElfAppContract.ExitCodeExpected, processImage.ExitCode, "elf validation: exit code mismatch");
            VerifySmokeMarker();
            Log.Write(LogLevel.Info, "elf validation done");

            Platform.Shutdown();
            Platform.Halt();
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

        private static void VerifySmokeMarker()
        {
            KernelAssert.True(
                TryReadMappedUInt32(ElfAppContract.MarkerVirtualAddress, out uint markerValue),
                "elf validation: marker read failed");

            Log.Begin(LogLevel.Info);
            Console.Write("process wrote marker = 0x");
            Console.WriteHex(markerValue, 8);
            Log.EndLine();

            KernelAssert.Equal(
                ElfAppContract.MarkerExpectedValue,
                markerValue,
                "elf validation: marker value mismatch");
        }

        private static bool TryReadMappedUInt32(ulong virtualAddress, out uint value)
        {
            value = 0;

            for (uint i = 0; i < 4; i++)
            {
                if (!Pager.TryQuery(virtualAddress + i, out ulong physicalAddress, out _))
                    return false;

                value |= ((uint)(*((byte*)physicalAddress)) << (int)(i * 8));
            }

            return true;
        }
    }
}
