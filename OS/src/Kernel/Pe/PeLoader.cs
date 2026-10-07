using System;
using OS.Kernel.Process;
using OS.Kernel.Paging;
using OS.Kernel.Util;
using OS.Hal;

namespace OS.Kernel.Pe
{
    // PE loader, execute path (step137): take a raw PE file image, flatten it
    // (PeImageLayout), map it into the address space at the base the caller
    // chose, and return a LoadedImage so the existing ProcessImageBuilder +
    // JumpStub pipeline (format-agnostic: it only needs EntryPoint + the mapped
    // VA range) runs unchanged.
    //
    // Since step194 every process has an address range of its own in the one
    // address space, so two images — two copies of one program included — are
    // mapped at once, and an image is relocated to its range by its base
    // relocations (apps are linked without /FIXED). Every page is mapped RWX
    // (Present|Writable|User) — per-section protection is still deferred. The
    // image's .pdata is registered for managed EH (step140).
    internal static unsafe class PeLoader
    {
        private const ulong PageSize = X64PageTable.PageSize;

        // PE\0\0 -> "MZ" DOS magic at offset 0.
        public const ushort DosMagicMZ = 0x5A4D;

        public static bool TryLoad(MemoryBlock image, ulong loadBase, out LoadedImage loadedImage, out int stage)
        {
            loadedImage = default;
            stage = 0;

            if (!image.IsValid)
                return false;

            // Copy the raw file into a managed byte[] for the pure-transform
            // flatten stage.
            int fileLen = (int)image.Length;
            byte[] file = new byte[fileLen];
            new Span<byte>(image.Pointer, fileLen).CopyTo(new Span<byte>(file));
            stage = 1;

            if (!PeImageLayout.TryReadLayout(file, out uint sizeOfImage32,
                    out ulong preferredBase, out ulong entryPoint, out uint sectionCount))
                return false;
            stage = 2;

            ulong imageBase = loadBase;
            if (imageBase == 0 || (imageBase & (PageSize - 1)) != 0)
                return false;
            entryPoint = entryPoint - preferredBase + imageBase;

            ulong sizeOfImage = sizeOfImage32;
            uint pageCount = (uint)((sizeOfImage + PageSize - 1) / PageSize);
            if (pageCount == 0)
                return false;

            // Drop any stale mappings across the target window.
            ulong va = imageBase;
            for (uint i = 0; i < pageCount; i++)
            {
                if (Pager.TryQuery(va, out _, out _) && !Pager.Unmap(va))
                    return false;
                va += PageSize;
            }
            stage = 3;

            // Page by page, the free list first: the mapping does not need
            // physical contiguity. A contiguous run comes from fresh memory
            // only, so with RAM to spare every image took new pages while the
            // ones processes gave back piled up in the free list, until it was
            // full and the next ones were lost (8 GiB, step194: +3.4 GB over
            // PROCTEST). With 2 GiB the fresh memory ran out first and hid it.
            stage = 4;

            // Map phys -> VA at the load base, RWX (no NX for now).
            PageFlags flags = PageFlags.Present | PageFlags.Writable | PageFlags.User;
            va = imageBase;
            for (uint i = 0; i < pageCount; i++)
            {
                ulong pa = global::OS.Kernel.PhysicalMemory.AllocPage();
                if (pa == 0 || !Pager.Map(va, pa, flags))
                {
                    if (pa != 0) global::OS.Kernel.PhysicalMemory.FreePage(pa);
                    ReleaseMapped(imageBase, i);
                    return false;
                }
                va += PageSize;
            }
            stage = 5;

            // Zero the whole window before anything is written into it. The
            // image's BSS is defined by what nobody writes, and these pages come
            // off the physical freelist now — they carry the last process's
            // bytes, not zeroes, which a fresh managed buffer used to hide.
            new Span<byte>((void*)imageBase, (int)(pageCount * PageSize)).Clear();

            // Lay the image out directly in the mapped window: headers at 0,
            // sections at their RVAs. No SizeOfImage buffer in between.
            if (!PeImageLayout.TryPlace(file, new Span<byte>((void*)imageBase, (int)sizeOfImage)))
            {
                ReleaseMapped(imageBase, pageCount);
                return false;
            }
            stage = 6;

            // To its range: every absolute address in the image moves with it.
            if (!TryRelocate((byte*)imageBase, sizeOfImage, preferredBase, imageBase, out string why))
            {
                Console.Write("[pe] image cannot be relocated to 0x");
                Console.WriteHex(imageBase);
                Console.Write(": ");
                Console.WriteLine(why);
                ReleaseMapped(imageBase, pageCount);
                return false;
            }

            // Register the app's .pdata so the managed EH walk can unwind app
            // frames (step140). An image without an exception directory stays
            // Tier-B (halt-on-throw). One that has it and is refused does not
            // run: its frames would not unwind and its collector would find no
            // roots below them (step194). Unregistered at teardown via
            // CoffRuntimeFunctionTable.UnregisterImage in UnmapMappedRange.
            if (!TryRegisterExceptionTable((byte*)imageBase, (int)sizeOfImage, imageBase))
            {
                Console.WriteLine("[pe] image refused: the table of images with unwind data is full");
                ReleaseMapped(imageBase, pageCount);
                return false;
            }

            TryReadManifest((byte*)imageBase, (uint)sizeOfImage, ref loadedImage);

            loadedImage.EntryPoint = entryPoint;
            loadedImage.LowestVirtualAddress = imageBase;
            loadedImage.HighestVirtualAddressExclusive = imageBase + (ulong)pageCount * PageSize;
            loadedImage.LoadedPages = pageCount;
            loadedImage.SectionCount = sectionCount;
            stage = 7;
            return true;
        }

        // Unmaps what a failed load mapped and gives its pages back.
        private static void ReleaseMapped(ulong imageBase, uint pages)
        {
            for (uint i = 0; i < pages; i++)
            {
                ulong va = imageBase + (ulong)i * PageSize;
                if (Pager.TryQuery(va, out ulong pa, out _))
                {
                    Pager.Unmap(va);
                    if (pa != 0) global::OS.Kernel.PhysicalMemory.FreePage(pa & ~(PageSize - 1));
                }
            }
        }

        /// <summary>
        /// Applies the image's base relocations in place, in the mapped window
        /// (RVA == offset there), and records the new base in its header.
        /// </summary>
        /// <remarks>
        /// DIR64 entries (the only kind an x64 image uses for addresses) and
        /// padding; anything else is refused with its type, as is an image
        /// moved without a relocation directory (linked /FIXED).
        /// </remarks>
        internal static bool TryRelocate(byte* image, ulong size, ulong preferredBase, ulong actualBase, out string why)
        {
            why = null;
            int peOff = *(int*)(image + 0x3C);
            byte* opt = image + peOff + 4 + 20;
            *(ulong*)(opt + 24) = actualBase;            // OptionalHeader64.ImageBase
            if (actualBase == preferredBase)
                return true;

            const int BaseRelocDirIndex = 5;
            uint dirRva = *(uint*)(opt + 112 + BaseRelocDirIndex * 8);
            uint dirSize = *(uint*)(opt + 112 + BaseRelocDirIndex * 8 + 4);
            if (dirRva == 0 || dirSize == 0)
            {
                why = "no base relocations (linked /FIXED?)";
                return false;
            }
            if ((ulong)dirRva + dirSize > size)
            {
                why = "relocation directory outside the image";
                return false;
            }

            long delta = (long)(actualBase - preferredBase);
            byte* block = image + dirRva;
            byte* end = block + dirSize;
            while (block + 8 <= end)
            {
                uint pageRva = *(uint*)block;
                uint blockSize = *(uint*)(block + 4);
                if (blockSize < 8 || block + blockSize > end)
                {
                    why = "malformed relocation block";
                    return false;
                }
                ushort* entries = (ushort*)(block + 8);
                uint count = (blockSize - 8) / 2;
                for (uint i = 0; i < count; i++)
                {
                    int type = entries[i] >> 12;
                    uint offset = (uint)(entries[i] & 0xFFF);
                    if (type == 0) continue;                 // IMAGE_REL_BASED_ABSOLUTE: padding
                    if (type != 10)                          // IMAGE_REL_BASED_DIR64
                    {
                        why = "relocation type " + type.ToString() + " not supported";
                        return false;
                    }
                    ulong at = (ulong)pageRva + offset;
                    if (at + 8 > size)
                    {
                        why = "relocation outside the image";
                        return false;
                    }
                    *(long*)(image + at) += delta;
                }
                block += blockSize;
            }
            return true;
        }

        /// <summary>
        /// Reads the SharpOS record out of the image's manifest resource.
        /// </summary>
        /// <remarks>
        /// Best-effort. An image without a manifest, or with one that carries no
        /// sharpos element, simply reports nothing found — the launch path then
        /// falls back the way it always did. A manifest that is present but does
        /// not parse is a different thing, and the caller is the one that
        /// decides what to do about it.
        /// </remarks>
        private static void TryReadManifest(byte* imageBase, uint imageSize, ref LoadedImage loadedImage)
        {
            if (!global::OS.Kernel.Pe.PeResources.TryFind(
                    imageBase, imageSize, PeResources.TypeManifest, out byte* data, out uint size))
                return;

            string? xml = DecodeUtf8(data, size);
            if (xml == null)
                return;

            SharpAppManifest manifest = SharpAppManifest.Parse(xml);
            if (!manifest.Found)
                return;

            loadedImage.ManifestFound = true;
            loadedImage.ManifestSchema = manifest.Schema;
            loadedImage.ManifestAbi = manifest.Abi;
            loadedImage.ManifestServiceAbi = manifest.ServiceAbi;
        }

        /// <summary>
        /// Turns the resource bytes into a string, skipping a byte-order mark.
        /// </summary>
        /// <remarks>
        /// The linker writes the manifest as UTF-8 with a BOM. Left in place it
        /// would be the first character of the document, and the parser would
        /// reject the whole thing over a character nobody wrote.
        /// </remarks>
        private static string? DecodeUtf8(byte* data, uint size)
        {
            if (data == null || size == 0)
                return null;

            uint start = 0;
            if (size >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
                start = 3;

            return global::System.Text.Encoding.UTF8.GetString(
                new global::System.ReadOnlySpan<byte>(data + start, (int)(size - start)));
        }

        // Parse data-directory index 3 (IMAGE_DIRECTORY_ENTRY_EXCEPTION) from the
        // mapped PE header (headers sit at offset 0 of the flattened image) and
        // register the app's RUNTIME_FUNCTION array with the managed EH
        // function-table registry. Records are addressed at imageBase + pdataRva
        // (the runtime VA, where the section is mapped). True without doing
        // anything on a parse failure or an image without a .pdata section;
        // false only when the registry refuses it.
        private static bool TryRegisterExceptionTable(byte* flat, int flatLength, ulong imageBase)
        {
            const int PeSig = 0x00004550;   // "PE\0\0"
            const ushort Pe32Plus = 0x020B;
            const int ExceptionDirIndex = 3;

            if (flat == null || flatLength < 0x40) return true;

            {
                byte* fp = flat;
                int peOff = *(int*)(fp + 0x3C);
                if (peOff <= 0 || (long)peOff + 4 + 20 + 112 + (ExceptionDirIndex + 1) * 8 > flatLength)
                    return true;
                if (*(uint*)(fp + peOff) != PeSig) return true;

                byte* opt = fp + peOff + 4 + 20;
                if (*(ushort*)opt != Pe32Plus) return true;

                byte* dataDir = opt + 112;
                uint pdataRva = *(uint*)(dataDir + ExceptionDirIndex * 8);
                uint pdataSize = *(uint*)(dataDir + ExceptionDirIndex * 8 + 4);
                if (pdataRva == 0 || pdataSize == 0 || (pdataSize % 12) != 0) return true;

                var records = (global::OS.Boot.EH.RuntimeFunction*)(imageBase + pdataRva);
                return global::OS.Boot.EH.CoffRuntimeFunctionTable.RegisterImage(
                    (byte*)imageBase, records, (int)(pdataSize / 12)) >= 0;
            }
        }
    }
}
