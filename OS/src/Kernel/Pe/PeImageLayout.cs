using System;
using PeNet.FileParser;
using PeNet.HeaderParser.Pe;

namespace OS.Kernel.Pe
{
    // PE loader, stage 1 (step136): lay a raw PE file out in its in-memory
    // image form -- PE headers at 0, every section's raw data at its
    // VirtualAddress (RVA), everything else left as the caller supplied it (the
    // BSS tail where VirtualSize > SizeOfRawData, and the gaps between
    // sections). Still a pure transform: no page tables, no execution.
    //
    // Reading the layout and writing it are separate on purpose. The
    // destination is SizeOfImage bytes, which an app carrying a 64 MiB static
    // GC pool pushes past 64 MiB, and allocating that as a managed byte[] per
    // launch cost the machine a 128 MiB GC segment every time -- segments are
    // never handed back, so ten launches were more than a gigabyte gone. The
    // caller now reads the size first, maps the pages, and has the sections
    // written straight into that window.
    //
    // Header structures come from the vendored PeNet native-PE parser
    // (NativeStructureParsers). Section placement mirrors the Windows loader.
    internal static class PeImageLayout
    {
        /// <summary>
        /// Reads the layout and places it into a buffer allocated here.
        /// </summary>
        /// <remarks>
        /// For small images — the probes' synthetic PEs. The launch path
        /// deliberately does not use this: a real app's SizeOfImage runs past
        /// 64 MiB, and one managed buffer that size per launch is what put the
        /// machine out of physical pages.
        /// </remarks>
        public static bool TryFlatten(
            byte[] file,
            out byte[] image,
            out ulong imageBase,
            out ulong entryPoint,
            out uint sectionCount)
        {
            image = null;

            if (!TryReadLayout(file, out uint sizeOfImage, out imageBase, out entryPoint, out sectionCount))
                return false;

            image = new byte[sizeOfImage];
            return TryPlace(file, image);
        }

        /// <summary>
        /// Reads the header fields the caller needs before it can make room for
        /// the image.
        /// </summary>
        public static bool TryReadLayout(
            byte[] file,
            out uint sizeOfImage,
            out ulong imageBase,
            out ulong entryPoint,
            out uint sectionCount)
        {
            sizeOfImage = 0;
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

            sizeOfImage = opt.SizeOfImage;
            if (sizeOfImage == 0 || sizeOfImage > 0x40000000) // 1 GiB sanity cap
                return false;

            imageBase = opt.ImageBase;
            entryPoint = imageBase + opt.AddressOfEntryPoint;
            sectionCount = nt.FileHeader.NumberOfSections;
            return true;
        }

        /// <summary>
        /// Writes headers and section data into <paramref name="image"/>, which
        /// must be SizeOfImage bytes and must already be zeroed: what is not
        /// written here is the image's BSS, and the caller owns making it zero.
        /// </summary>
        public static bool TryPlace(byte[] file, Span<byte> image)
        {
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

            uint sizeOfHeaders = opt.SizeOfHeaders;

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

        private static void Copy(byte[] src, int srcOffset, Span<byte> dst, int dstOffset, int count)
        {
            new Span<byte>(src, srcOffset, count).CopyTo(dst.Slice(dstOffset, count));
        }
    }
}
