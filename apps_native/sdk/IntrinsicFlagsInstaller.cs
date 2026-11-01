namespace SharpOS.AppSdk
{
    // Writes the CPU's intrinsic flags into this image's g_cpuFeatures — the
    // variable ILC's optimistic X.IsSupported reads (HardwareIntrinsicFlags).
    // Managed code cannot name a native data symbol, so it is found the way a
    // loader would: the image exports it (/EXPORT:g_cpuFeatures,DATA,
    // FreestandingPe.props), and its headers are mapped at ImageBase.
    // Runs before anything else allocates or dispatches: no strings, no GC.
    internal static unsafe class IntrinsicFlagsInstaller
    {
        public static bool Write(ulong imageBase, int flags)
        {
            int* slot = Find((byte*)imageBase);
            if (slot == null) return false;
            *slot = flags;
            return true;
        }

        private static int* Find(byte* image)
        {
            if (image == null || image[0] != (byte)'M' || image[1] != (byte)'Z') return null;
            byte* nt = image + *(int*)(image + 0x3C);
            if (*(uint*)nt != 0x00004550) return null;                  // "PE\0\0"
            byte* optional = nt + 4 + 20;
            if (*(ushort*)optional != 0x20B) return null;               // PE32+
            uint exportRva = *(uint*)(optional + 112);                  // DataDirectory[0]
            if (exportRva == 0) return null;

            byte* exports = image + exportRva;
            uint count = *(uint*)(exports + 24);                        // NumberOfNames
            uint* functions = (uint*)(image + *(uint*)(exports + 28));
            uint* names = (uint*)(image + *(uint*)(exports + 32));
            ushort* ordinals = (ushort*)(image + *(uint*)(exports + 36));
            for (uint i = 0; i < count; i++)
            {
                if (Is(image + names[i], "g_cpuFeatures"))
                    return (int*)(image + functions[ordinals[i]]);
            }
            return null;
        }

        private static bool Is(byte* name, string wanted)
        {
            for (int i = 0; i < wanted.Length; i++)
                if (name[i] != (byte)wanted[i]) return false;
            return name[wanted.Length] == 0;
        }
    }
}
