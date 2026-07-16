using System;
using PeNet.FileParser;
using PeNet.Header.Pe;

namespace PeNet
{
    // SharpOS cut of PeNet.ExtensionMethods. Vendored: the IRawFile 32/64-bit
    // magic probes (phase 1) + RvaToOffset (phase 2, imports/exports/reloc).
    // The upstream LINQ helpers (OrEmpty, ToHexString, byte-search, TrySelect)
    // are omitted until a consumer needs them.
    //
    // RvaToOffset is re-typed to take ImageSectionHeader[] (upstream:
    // ICollection<ImageSectionHeader> + .Count/.ElementAt). Our arrays don't
    // implement ICollection<T>/IEnumerable<T> (no SZArrayHelper), so the
    // interface-based version would fault at runtime; the array overload walks
    // by index (sh.Length / sh[i]) directly. Semantics identical.
    public static class ExtensionMethods
    {
        /// <summary>Check if a given file is 64 bit (PE32+).</summary>
        public static bool Is64Bit(this IRawFile peFile)
            => peFile.ReadUShort(peFile.ReadUInt(0x3c) + 0x18) == (ushort)MagicType.Bit64;

        /// <summary>Check if a given file is 32 bit (PE32).</summary>
        public static bool Is32Bit(this IRawFile peFile)
            => peFile.ReadUShort(peFile.ReadUInt(0x3c) + 0x18) == (ushort)MagicType.Bit32;

        /// <summary>Map a relative virtual address to a raw file offset.</summary>
        public static ulong RvaToOffset(this ulong rva, ImageSectionHeader[] sectionHeaders)
        {
            ImageSectionHeader sec = GetSectionForRva(sectionHeaders, rva);
            if (sec is null)
                throw new Exception("Cannot find corresponding section.");
            return rva - sec.VirtualAddress + sec.PointerToRawData;
        }

        /// <summary>Map a relative virtual address to a raw file offset.</summary>
        public static uint RvaToOffset(this uint rva, ImageSectionHeader[] sectionHeaders)
            => (uint)RvaToOffset((ulong)rva, sectionHeaders);

        /// <summary>Non-throwing RvaToOffset.</summary>
        public static bool TryRvaToOffset(this uint rva, ImageSectionHeader[] sectionHeaders, out uint fileOffset)
        {
            fileOffset = 0;
            if (sectionHeaders is null)
                return false;
            try
            {
                fileOffset = rva.RvaToOffset(sectionHeaders);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static ImageSectionHeader GetSectionForRva(ImageSectionHeader[] sh, ulong relVirAdr)
        {
            ImageSectionHeader sec = null;

            // Some linkers zero VirtualSize for .edata/.reloc/.rsrc; fall back to
            // SizeOfRawData in that case (matches upstream).
            for (int i = 0; i < sh.Length; i++)
            {
                uint secSize = sh[i].VirtualSize == 0 ? sh[i].SizeOfRawData : sh[i].VirtualSize;
                if (relVirAdr >= sh[i].VirtualAddress && relVirAdr < sh[i].VirtualAddress + secSize)
                    sec = sh[i];
            }
            if (sec != null)
                return sec;

            for (int i = sh.Length - 1; i >= 0; i--)
            {
                uint secSize = sh[i].VirtualSize == 0 ? sh[i].SizeOfRawData : sh[i].VirtualSize;
                if (relVirAdr >= sh[i].VirtualAddress && relVirAdr <= sh[i].VirtualAddress + secSize)
                    sec = sh[i];
            }
            return sec;
        }
    }
}
