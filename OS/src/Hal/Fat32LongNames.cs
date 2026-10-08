namespace OS.Hal
{
    // Creating a file under a long name (step197: `> out.json`, WRITE
    // errors.json). Until now the writer stored 8.3 names only, and refused
    // anything else; the reader already matched long names.
    //
    // A long name is stored as VFAT does it: LFN entries carrying the name in
    // pieces of 13 UTF-16 characters, in reverse order, right before an 8.3
    // entry under an alias of the writer's choosing — the first six letters of
    // the name, ~N, the first three of its extension — with N the first one no
    // entry of the directory has. The LFN entries carry the alias's checksum,
    // as VFAT requires, so a reader that checks it finds the pair.
    //
    // The entries go into one run of free slots inside one cluster of the
    // directory; the directory is not grown (as for short names).
    internal static unsafe partial class Fat32
    {
        private const int MaxLongName = 255;

        private static bool TryCreateLongEntry(uint parentCluster, string path, int nameStart, int nameLength,
                                               out ulong entryLba, out uint entryOffset)
        {
            entryLba = 0;
            entryOffset = 0;
            if (nameLength == 0 || nameLength > MaxLongName) return false;
            for (int i = 0; i < nameLength; i++)
            {
                char c = path[nameStart + i];
                if (c < 0x20 || c == '"' || c == '*' || c == ':' || c == '<' || c == '>' || c == '?' || c == '|')
                    return false;
            }

            byte* alias = stackalloc byte[11];
            if (!TryMakeAlias(parentCluster, path, nameStart, nameLength, alias)) return false;
            byte checksum = AliasChecksum(alias);

            int pieces = (nameLength + 12) / 13;
            uint need = (uint)pieces + 1;
            uint perCluster = ClusterBytes / 32;
            uint cluster = parentCluster == 0 ? s_rootClus : parentCluster;
            uint guard = 0;
            while (cluster >= 2 && guard++ < 1_000_000)
            {
                if (!s_disk.Read(ClusterLba(cluster), s_spc, s_bulk)) return false;
                uint run = 0;
                for (uint i = 0; i < perCluster; i++)
                {
                    byte first = s_bulk[i * 32];
                    if (first != 0x00 && first != 0xE5) { run = 0; continue; }
                    if (++run < need) continue;

                    uint at = i + 1 - need;
                    for (int k = pieces; k >= 1; k--, at++)
                        FillLongEntry(s_bulk + at * 32, path, nameStart, nameLength, k, k == pieces, checksum);
                    FillEntry(s_bulk + at * 32, alias, 0, 0);
                    if (!s_disk.Write(ClusterLba(cluster), s_spc, s_bulk)) return false;
                    entryLba = ClusterLba(cluster) + at * 32 / s_bps;
                    entryOffset = at * 32 % s_bps;
                    return true;
                }
                uint next = FatNext(cluster);
                if (next == 0) break;
                cluster = next;
            }
            return false;
        }

        // Piece k (1-based) of the name: 13 characters, a 0x0000 after the
        // name's end and 0xFFFF after that.
        private static void FillLongEntry(byte* e, string path, int nameStart, int nameLength, int k, bool last, byte checksum)
        {
            for (int i = 0; i < 32; i++) e[i] = 0;
            e[0] = (byte)(k | (last ? 0x40 : 0));
            e[11] = 0x0F;
            e[13] = checksum;
            int* offsets = stackalloc int[13] { 1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30 };
            for (int j = 0; j < 13; j++)
            {
                int index = (k - 1) * 13 + j;
                ushort u = index < nameLength ? path[nameStart + index]
                         : index == nameLength ? (ushort)0x0000
                         : (ushort)0xFFFF;
                e[offsets[j]] = (byte)u;
                e[offsets[j] + 1] = (byte)(u >> 8);
            }
        }

        private static byte AliasChecksum(byte* alias)
        {
            byte sum = 0;
            for (int i = 0; i < 11; i++) sum = (byte)(((sum & 1) << 7) + (sum >> 1) + alias[i]);
            return sum;
        }

        // BASIS~N.EXT: letters and digits of the name before its last dot,
        // upper-cased, and of what follows it; N the first free.
        private static bool TryMakeAlias(uint parentCluster, string path, int nameStart, int nameLength, byte* alias)
        {
            int dot = -1;
            for (int i = nameLength - 1; i > 0; i--)
                if (path[nameStart + i] == '.') { dot = i; break; }
            int baseEnd = dot < 0 ? nameLength : dot;

            byte* basis = stackalloc byte[8];
            int basisLength = 0;
            for (int i = 0; i < baseEnd && basisLength < 6; i++)
                if (TryUpper(path[nameStart + i], out byte c) && c != (byte)'~') basis[basisLength++] = c;
            if (basisLength == 0) basis[basisLength++] = (byte)'_';

            byte* ext = stackalloc byte[3];
            int extLength = 0;
            for (int i = dot + 1; dot >= 0 && i < nameLength && extLength < 3; i++)
                if (TryUpper(path[nameStart + i], out byte c) && c != (byte)'~') ext[extLength++] = c;

            char* text = stackalloc char[13];
            for (int n = 1; n < 1_000_000; n++)
            {
                string tail = "~" + n.ToString();
                int keep = basisLength;
                if (keep + tail.Length > 8) keep = 8 - tail.Length;
                for (int i = 0; i < 11; i++) alias[i] = 0x20;
                for (int i = 0; i < keep; i++) alias[i] = basis[i];
                for (int i = 0; i < tail.Length; i++) alias[keep + i] = (byte)tail[i];
                for (int i = 0; i < extLength; i++) alias[8 + i] = ext[i];

                int length = Name83Out(alias, text, 13);
                string candidate = new string(new System.ReadOnlySpan<char>(text, length));
                if (!FindIn(parentCluster, candidate, 0, candidate.Length, out _, out _, out _)) return true;
            }
            return false;
        }
    }
}
