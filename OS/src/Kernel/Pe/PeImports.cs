using System;
using PeNet;
using PeNet.FileParser;
using PeNet.Header.Pe;
using PeNet.HeaderParser.Pe;

namespace OS.Kernel.Pe
{
    // PE loader, stage 3 (step136): resolve imports and bind the IAT of a
    // flattened image (see PeImageLayout). For each imported function the
    // resolver supplies its address (e.g. a kernel export); that address is
    // written into the image's Import Address Table slot, which is what the
    // loaded code calls through.
    //
    // Parsing runs on the ORIGINAL file (the vendored parsers use RvaToOffset,
    // which maps RVA -> *file* offset via the section headers). The resolved
    // pointers are then written into the FLATTENED image at the IAT slot RVA,
    // which equals the offset in the flattened buffer. Slot RVA is reconstructed
    // as (IAT directory VA + ImportFunction.IATOffset), matching how the parser
    // computed IATOffset.
    internal static class PeImports
    {
        public static bool TryResolve(
            byte[] flatImage,
            byte[] file,
            Func<ImportFunction, ulong> resolver,
            out int resolved,
            out int unresolved)
        {
            resolved = 0;
            unresolved = 0;

            if (flatImage == null || file == null || resolver == null)
                return false;

            var raw = new BufferFile(file);
            var parsers = new NativeStructureParsers(raw);
            var nt = parsers.ImageNtHeaders;
            var secs = parsers.ImageSectionHeaders;
            if (nt == null || secs == null)
                return false;

            var dds = nt.OptionalHeader.DataDirectory;
            bool is64 = raw.Is64Bit();
            if (!is64)
                return false; // PE32 (32-bit IAT slots) not handled yet

            uint impDirRva = dds[1].VirtualAddress;
            if (impDirRva == 0)
                return true; // no import directory -> nothing to bind

            uint impDirOff = impDirRva.RvaToOffset(secs);
            var descs = new ImageImportDescriptorsParser(raw, impDirOff).GetParserTarget();
            var imports = new ImportedFunctionsParser(raw, descs, secs, dds, is64).GetParserTarget();
            if (imports == null)
                return true;

            uint iatVa = dds[12].VirtualAddress;

            for (int i = 0; i < imports.Length; i++)
            {
                ulong addr = resolver(imports[i]);
                if (addr == 0)
                {
                    unresolved++;
                    continue;
                }

                long slotRva = (long)iatVa + imports[i].IATOffset;
                if (slotRva < 0 || slotRva + 8 > flatImage.Length)
                    return false;

                PeRelocations.WriteU64(flatImage, (int)slotRva, addr);
                resolved++;
            }

            return true;
        }
    }
}
