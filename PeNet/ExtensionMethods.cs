using PeNet.FileParser;
using PeNet.Header.Pe;

namespace PeNet
{
    // SharpOS milestone-1 cut of PeNet.ExtensionMethods: only the IRawFile
    // 32/64-bit magic probes are vendored (the sole extensions the native-PE
    // parse path needs). The upstream file's LINQ-based helpers (OrEmpty,
    // AsHexString, byte-search, etc.) are omitted until a consumer needs them.
    public static class ExtensionMethods
    {
        /// <summary>Check if a given file is 64 bit (PE32+).</summary>
        public static bool Is64Bit(this IRawFile peFile)
            => peFile.ReadUShort(peFile.ReadUInt(0x3c) + 0x18) == (ushort)MagicType.Bit64;

        /// <summary>Check if a given file is 32 bit (PE32).</summary>
        public static bool Is32Bit(this IRawFile peFile)
            => peFile.ReadUShort(peFile.ReadUInt(0x3c) + 0x18) == (ushort)MagicType.Bit32;
    }
}
