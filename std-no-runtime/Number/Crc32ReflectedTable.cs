// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/Common/src/System/Numerics/Crc32ReflectedTable.cs
// Verbatim. BitOperations.Crc32C's software fallback builds its table with it.

namespace System.Numerics
{
    internal static class Crc32ReflectedTable
    {
        internal static uint[] Generate(uint reflectedPolynomial)
        {
            uint[] table = new uint[256];

            for (int i = 0; i < 256; i++)
            {
                uint val = (uint)i;

                for (int j = 0; j < 8; j++)
                {
                    if ((val & 0b0000_0001) == 0)
                    {
                        val >>= 1;
                    }
                    else
                    {
                        val = (val >> 1) ^ reflectedPolynomial;
                    }
                }

                table[i] = val;
            }

            return table;
        }
    }
}
