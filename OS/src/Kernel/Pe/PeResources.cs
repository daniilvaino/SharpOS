namespace OS.Kernel.Pe
{
    /// <summary>
    /// Finds a resource in a mapped PE image.
    /// </summary>
    /// <remarks>
    /// The resource tree is three levels deep — type, then name or id, then
    /// language — and every offset inside it is relative to the start of the
    /// resource directory, while the leaf's own pointer is an image RVA. Mixing
    /// the two is the classic way to read a resource that looks almost right.
    ///
    /// Reached through data directory 2 rather than by looking for a section
    /// called ".rsrc": the directory is where the format says the answer is, and
    /// a section name is only a convention.
    /// </remarks>
    internal static unsafe class PeResources
    {
        private const int PeSignature = 0x00004550;   // "PE\0\0"
        private const ushort Pe32Plus = 0x020B;
        private const int ResourceDirectoryIndex = 2;

        /// <summary>Resource type of an application manifest (RT_MANIFEST).</summary>
        public const uint TypeManifest = 24;

        private const uint SubdirectoryFlag = 0x80000000;

        public static bool TryFind(byte* imageBase, uint imageSize, uint typeId,
            out byte* data, out uint size)
        {
            data = null;
            size = 0;

            if (imageBase == null || imageSize < 0x40)
                return false;

            if (!TryReadResourceDirectory(imageBase, imageSize, out uint directoryRva, out uint directorySize))
                return false;

            byte* directory = imageBase + directoryRva;

            // Level 1: type.
            if (!TryFindEntry(directory, directory, directorySize, typeId, out uint typeOffset, out bool typeIsDirectory)
                || !typeIsDirectory)
                return false;

            // Levels 2 and 3: name/id, then language. Both are taken as they
            // come — an app has one manifest, and picking "the first" is what
            // the loader does for a resource with no language of its own.
            if (!TryFirstEntry(directory, directory + typeOffset, directorySize, out uint nameOffset, out bool nameIsDirectory)
                || !nameIsDirectory)
                return false;

            if (!TryFirstEntry(directory, directory + nameOffset, directorySize, out uint leafOffset, out bool leafIsDirectory)
                || leafIsDirectory)
                return false;

            // IMAGE_RESOURCE_DATA_ENTRY: RVA, size, code page, reserved.
            if (leafOffset + 16 > directorySize)
                return false;

            uint* leaf = (uint*)(directory + leafOffset);
            uint dataRva = leaf[0];
            uint dataSize = leaf[1];

            if (dataRva == 0 || dataSize == 0)
                return false;
            if ((ulong)dataRva + dataSize > imageSize)
                return false;

            data = imageBase + dataRva;
            size = dataSize;
            return true;
        }

        private static bool TryReadResourceDirectory(byte* imageBase, uint imageSize,
            out uint directoryRva, out uint directorySize)
        {
            directoryRva = 0;
            directorySize = 0;

            int peOffset = *(int*)(imageBase + 0x3C);
            if (peOffset <= 0 || (ulong)peOffset + 4 + 20 + 112 + (ResourceDirectoryIndex + 1) * 8 > imageSize)
                return false;

            if (*(uint*)(imageBase + peOffset) != PeSignature)
                return false;

            byte* optional = imageBase + peOffset + 4 + 20;
            if (*(ushort*)optional != Pe32Plus)
                return false;

            byte* dataDirectory = optional + 112;
            directoryRva = *(uint*)(dataDirectory + ResourceDirectoryIndex * 8);
            directorySize = *(uint*)(dataDirectory + ResourceDirectoryIndex * 8 + 4);

            if (directoryRva == 0 || directorySize == 0)
                return false;

            return (ulong)directoryRva + directorySize <= imageSize;
        }

        /// <summary>Looks for one id among a directory node's entries.</summary>
        private static bool TryFindEntry(byte* directoryBase, byte* node, uint directorySize,
            uint id, out uint offset, out bool isDirectory)
        {
            offset = 0;
            isDirectory = false;

            if (!TryReadEntryCount(directoryBase, node, directorySize, out int named, out int ids))
                return false;

            // Named entries come first and are skipped: a manifest is found by
            // id, and a name entry's id field is an offset to a string instead.
            for (int i = named; i < named + ids; i++)
            {
                uint* entry = (uint*)(node + 16 + i * 8);
                if (entry[0] != id)
                    continue;

                return TryDecodeEntry(directoryBase, directorySize, entry[1], out offset, out isDirectory);
            }

            return false;
        }

        /// <summary>Takes a directory node's first entry, whatever it is called.</summary>
        private static bool TryFirstEntry(byte* directoryBase, byte* node, uint directorySize,
            out uint offset, out bool isDirectory)
        {
            offset = 0;
            isDirectory = false;

            if (!TryReadEntryCount(directoryBase, node, directorySize, out int named, out int ids))
                return false;

            if (named + ids == 0)
                return false;

            uint* entry = (uint*)(node + 16);
            return TryDecodeEntry(directoryBase, directorySize, entry[1], out offset, out isDirectory);
        }

        private static bool TryReadEntryCount(byte* directoryBase, byte* node, uint directorySize,
            out int named, out int ids)
        {
            named = 0;
            ids = 0;

            ulong nodeOffset = (ulong)(node - directoryBase);
            if (nodeOffset + 16 > directorySize)
                return false;

            named = *(ushort*)(node + 12);
            ids = *(ushort*)(node + 14);

            ulong end = nodeOffset + 16 + (ulong)(named + ids) * 8;
            return end <= directorySize;
        }

        private static bool TryDecodeEntry(byte* directoryBase, uint directorySize, uint raw,
            out uint offset, out bool isDirectory)
        {
            isDirectory = (raw & SubdirectoryFlag) != 0;
            offset = raw & ~SubdirectoryFlag;
            return offset < directorySize;
        }
    }
}
