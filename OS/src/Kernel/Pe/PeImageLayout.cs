using System;
using PeNet.FileParser;
using PeNet.HeaderParser.Pe;

namespace OS.Kernel.Pe
{
    // PE loader, stage 1 (step136): flatten a raw PE file into its in-memory
    // image layout. Produces a SizeOfImage byte[] with the PE headers at 0 and
    // every section's raw data placed at its VirtualAddress (RVA). Regions not
    // backed by raw data -- the BSS tail where VirtualSize > SizeOfRawData, and
    // gaps between sections -- stay zero. This is a pure buffer transform: no
    // page tables, no execution. Base relocations (M2) and import resolution
    // (M3) run over this buffer next; only the final map-into-pages + jump step
    // touches the live address space.
    //
    // Header structures come from the vendored PeNet native-PE parser
    // (NativeStructureParsers). Section placement mirrors the Windows loader.
    internal static class PeImageLayout
    {
        public static bool TryFlatten(
            byte[] file,
            out byte[] image,
            out ulong imageBase,
            out ulong entryPoint,
            out uint sectionCount)
        {
            image = null;
            imageBase = 0;
            entryPoint = 0;
            sectionCount = 0;

            if (file == null || file.Length < 0x40)
                return false;

            var raw = new BufferFile(file);
            var parsers = new NativeStructureParsers(raw);

            var nt = parsers.ImageNtHeaders;
            if (nt == null || nt.Signature != 0x4550)
                return false;

            var opt = nt.OptionalHeader;
            var secs = parsers.ImageSectionHeaders;
            if (opt == null || secs == null)
                return false;

            uint sizeOfImage = opt.SizeOfImage;
            uint sizeOfHeaders = opt.SizeOfHeaders;
            if (sizeOfImage == 0 || sizeOfImage > 0x40000000) // 1 GiB sanity cap
                return false;

            image = new byte[sizeOfImage];
            imageBase = opt.ImageBase;
            entryPoint = imageBase + opt.AddressOfEntryPoint;
            sectionCount = nt.FileHeader.NumberOfSections;

            // Headers: copy [0, SizeOfHeaders) from the file, clamped to both
            // the file length and the image buffer.
            int hdr = (int)sizeOfHeaders;
            if (hdr > file.Length) hdr = file.Length;
            if (hdr > image.Length) hdr = image.Length;
            if (hdr > 0)
                Copy(file, 0, image, 0, hdr);

            // Sections: raw data -> VirtualAddress. Leave the rest zero (BSS +
            // inter-section padding).
            for (int i = 0; i < secs.Length; i++)
            {
                var s = secs[i];
                long va = s.VirtualAddress;
                long ptr = s.PointerToRawData;
                long rawSize = s.SizeOfRawData;
                if (rawSize <= 0)
                    continue;

                // Clamp the copy to the file (source) and image (destination).
                if (ptr < 0 || ptr >= file.Length)
                    continue;
                if (ptr + rawSize > file.Length)
                    rawSize = file.Length - ptr;
                if (va < 0 || va >= image.Length)
                    continue;
                if (va + rawSize > image.Length)
                    rawSize = image.Length - va;
                if (rawSize <= 0)
                    continue;

                Copy(file, (int)ptr, image, (int)va, (int)rawSize);
            }

            return true;
        }

        private static void Copy(byte[] src, int srcOffset, byte[] dst, int dstOffset, int count)
        {
            new Span<byte>(src, srcOffset, count).CopyTo(new Span<byte>(dst, dstOffset, count));
        }
    }
}
