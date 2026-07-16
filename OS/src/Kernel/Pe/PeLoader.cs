using System;
using OS.Kernel.Elf;
using OS.Kernel.Paging;
using OS.Kernel.Util;

namespace OS.Kernel.Pe
{
    // PE loader, execute path (step137): take a raw PE file image, flatten it
    // (PeImageLayout), map it into the address space at its preferred ImageBase,
    // and return an ElfLoadedImage so the existing ProcessImageBuilder + JumpStub
    // pipeline (format-agnostic: it only needs EntryPoint + the mapped VA range)
    // runs unchanged.
    //
    // v1 scope: honor ImageBase (the launcher is linked with /BASE matching the
    // app VA window, so no relocation needed); map every page RWX (Present|
    // Writable|User, executable) -- per-section NX/RO protection, base
    // relocations for off-base loads, and per-image .pdata/EH registration are
    // deferred. EH is Tier-B (halt-on-throw), same as the ELF apps today.
    internal static unsafe class PeLoader
    {
        private const ulong PageSize = X64PageTable.PageSize;

        // PE\0\0 -> "MZ" DOS magic at offset 0.
        public const ushort DosMagicMZ = 0x5A4D;

        public static bool TryLoad(MemoryBlock image, out ElfLoadedImage loadedImage, out int stage)
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

            if (!PeImageLayout.TryFlatten(file, out byte[] flat, out ulong imageBase, out ulong entryPoint, out uint sectionCount))
                return false;
            stage = 2;

            if (imageBase == 0 || (imageBase & (PageSize - 1)) != 0)
                return false; // ImageBase must be page-aligned to honor it directly

            ulong sizeOfImage = (ulong)flat.Length;
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

            ulong physBase = global::OS.Kernel.PhysicalMemory.AllocPages(pageCount);
            if (physBase == 0)
                return false;
            stage = 4;

            // Map contiguous phys -> VA at ImageBase, RWX (no NX for now).
            PageFlags flags = PageFlags.Present | PageFlags.Writable | PageFlags.User;
            va = imageBase;
            for (uint i = 0; i < pageCount; i++)
            {
                ulong pa = physBase + (ulong)i * PageSize;
                if (!Pager.Map(va, pa, flags))
                {
                    Pager.UnmapRange(imageBase, i);
                    return false;
                }
                va += PageSize;
            }
            stage = 5;

            // Blit the flattened image into the now-mapped window. Tail of the
            // last page (pageCount*PageSize - sizeOfImage) stays zero.
            new Span<byte>(flat).CopyTo(new Span<byte>((void*)imageBase, flat.Length));
            stage = 6;

            // Register the app's .pdata so the managed EH walk can unwind app
            // frames (step140). Best-effort: an app with no exception directory
            // stays Tier-B (halt-on-throw). Unregistered at teardown via
            // CoffRuntimeFunctionTable.UnregisterImage in UnmapMappedRange.
            TryRegisterExceptionTable(flat, imageBase);

            loadedImage.EntryPoint = entryPoint;
            loadedImage.LowestVirtualAddress = imageBase;
            loadedImage.HighestVirtualAddressExclusive = imageBase + (ulong)pageCount * PageSize;
            loadedImage.LoadedPages = pageCount;
            loadedImage.LoadedSegmentCount = sectionCount;
            stage = 7;
            return true;
        }

        // Parse data-directory index 3 (IMAGE_DIRECTORY_ENTRY_EXCEPTION) from the
        // mapped PE header (headers sit at offset 0 of the flattened image) and
        // register the app's RUNTIME_FUNCTION array with the managed EH
        // function-table registry. Records are addressed at imageBase + pdataRva
        // (the runtime VA, where the section is mapped). No-op on any parse
        // failure or an image without a .pdata section.
        private static void TryRegisterExceptionTable(byte[] flat, ulong imageBase)
        {
            const int PeSig = 0x00004550;   // "PE\0\0"
            const ushort Pe32Plus = 0x020B;
            const int ExceptionDirIndex = 3;

            if (flat == null || flat.Length < 0x40) return;

            fixed (byte* fp = flat)
            {
                int peOff = *(int*)(fp + 0x3C);
                if (peOff <= 0 || (long)peOff + 4 + 20 + 112 + (ExceptionDirIndex + 1) * 8 > flat.Length)
                    return;
                if (*(uint*)(fp + peOff) != PeSig) return;

                byte* opt = fp + peOff + 4 + 20;
                if (*(ushort*)opt != Pe32Plus) return;

                byte* dataDir = opt + 112;
                uint pdataRva = *(uint*)(dataDir + ExceptionDirIndex * 8);
                uint pdataSize = *(uint*)(dataDir + ExceptionDirIndex * 8 + 4);
                if (pdataRva == 0 || pdataSize == 0 || (pdataSize % 12) != 0) return;

                var records = (global::OS.Boot.EH.RuntimeFunction*)(imageBase + pdataRva);
                global::OS.Boot.EH.CoffRuntimeFunctionTable.RegisterImage(
                    (byte*)imageBase, records, (int)(pdataSize / 12));
            }
        }
    }
}
