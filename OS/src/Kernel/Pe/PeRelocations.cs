using PeNet.FileParser;
using PeNet.HeaderParser.Pe;

namespace OS.Kernel.Pe
{
    // PE loader, stage 2 (step136): apply base relocations to a flattened image
    // (see PeImageLayout). When the image is placed at a load address other than
    // its preferred ImageBase, every absolute address baked into the image must
    // be adjusted by delta = actualBase - preferredBase. The .reloc directory
    // lists them as (block page RVA, per-entry offset+type) pairs.
    //
    // Operates on the flattened buffer, where RVA == offset, so the vendored
    // ImageBaseRelocationsParser reads reloc blocks straight from the image at
    // the directory RVA. Supported entry types: DIR64 (10, 64-bit), HIGHLOW
    // (3, 32-bit), ABSOLUTE (0, padding -> skipped). Others are skipped.
    internal static class PeRelocations
    {
        private const byte RelBasedAbsolute = 0;
        private const byte RelBasedHighLow = 3;
        private const byte RelBasedDir64 = 10;

        public static bool TryApply(
            byte[] image,
            ulong preferredBase,
            ulong actualBase,
            uint relocDirRva,
            uint relocDirSize,
            out int applied)
        {
            applied = 0;

            if (image == null)
                return false;

            // No move -> nothing to patch (this is the common case: load at the
            // preferred base).
            if (actualBase == preferredBase)
                return true;

            long delta = (long)actualBase - (long)preferredBase;

            // Base moved but the image has no relocation info: it can't be
            // relocated. A stripped image would only be loadable at its base.
            if (relocDirRva == 0 || relocDirSize == 0)
                return false;

            var raw = new BufferFile(image); // flattened: RVA == offset
            var blocks = new ImageBaseRelocationsParser(raw, relocDirRva, relocDirSize).GetParserTarget();
            if (blocks == null)
                return false;

            for (int bi = 0; bi < blocks.Length; bi++)
            {
                var block = blocks[bi];
                var tos = block.TypeOffsets;
                if (tos == null)
                    continue;

                for (int ti = 0; ti < tos.Length; ti++)
                {
                    byte type = tos[ti].Type;
                    if (type == RelBasedAbsolute)
                        continue;

                    long targetRva = (long)block.VirtualAddress + tos[ti].Offset;

                    if (type == RelBasedDir64)
                    {
                        if (targetRva < 0 || targetRva + 8 > image.Length)
                            return false;
                        ulong v = ReadU64(image, (int)targetRva);
                        WriteU64(image, (int)targetRva, (ulong)((long)v + delta));
                        applied++;
                    }
                    else if (type == RelBasedHighLow)
                    {
                        if (targetRva < 0 || targetRva + 4 > image.Length)
                            return false;
                        uint v = ReadU32(image, (int)targetRva);
                        WriteU32(image, (int)targetRva, (uint)((long)v + delta));
                        applied++;
                    }
                    // Unsupported types (HIGH/LOW/HIGHADJ/ARM/etc.) are skipped.
                }
            }

            return true;
        }

        internal static uint ReadU32(byte[] b, int o)
            => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        internal static ulong ReadU64(byte[] b, int o)
            => ReadU32(b, o) | ((ulong)ReadU32(b, o + 4) << 32);

        internal static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        internal static void WriteU64(byte[] b, int o, ulong v)
        {
            WriteU32(b, o, (uint)v); WriteU32(b, o + 4, (uint)(v >> 32));
        }
    }
}
