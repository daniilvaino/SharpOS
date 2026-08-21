using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    /// <summary>
    /// Checks that ILC turned the vector types in our own system module into
    /// instructions, and that those instructions agree with the answers a
    /// character scanner expects.
    /// </summary>
    /// <remarks>
    /// <c>IsHardwareAccelerated</c> is the recognition signal: ILC folds it to a
    /// constant true once it has taken the type over, and our own body returns
    /// false when it has not.
    ///
    /// The comparisons deliberately use MIXED lanes. Uniform data makes every
    /// lane agree, so the whole vector comes out all-set or all-clear and a
    /// swapped lane order or a half-formed mask reads as correct.
    /// </remarks>
    internal static unsafe class SimdProbe
    {
        public static void Run()
        {
            Log.Begin(LogLevel.Info);
            Console.Write("[simd] Vector128 accelerated=");
            Console.Write(Vector128.IsHardwareAccelerated ? "yes" : "no");
            Console.Write(" count=");
            Console.WriteUInt((uint)Vector128<ushort>.Count);
            Console.Write(" Vector256 accelerated=");
            Console.Write(Vector256.IsHardwareAccelerated ? "yes" : "no");
            Log.EndLine();

            ReportLanes();
        }

        private static void ReportLanes()
        {
            // Eight lanes, of which lanes 2 and 5 are the ones a scanner stops
            // on: '<' and '&'. Everything else is ordinary text.
            char* text = stackalloc char[8];
            text[0] = 'a'; text[1] = 'b'; text[2] = '<'; text[3] = 'd';
            text[4] = 'e'; text[5] = '&'; text[6] = 'g'; text[7] = 'h';

            Vector128<ushort> data = Unsafe.ReadUnaligned<Vector128<ushort>>(text);

            Vector128<ushort> hits =
                Vector128.Equals(data, Vector128.Create((ushort)'<'))
                | Vector128.Equals(data, Vector128.Create((ushort)'&'));

            // Which lanes matched, as a bit per lane — the shape a swapped lane
            // order would get wrong while an all-or-nothing check would not.
            uint mask = LaneMask(hits);
            const uint Expected = (1u << 2) | (1u << 5);

            bool anyHit = hits != Vector128<ushort>.Zero;
            bool noneOnClean = Vector128.Equals(data, Vector128.Create((ushort)'z')) == Vector128<ushort>.Zero;

            // Ordering, on the same data: every lane is below the surrogate
            // range, and none is below a space.
            bool allBelowSurrogates =
                Vector128.LessThan(data, Vector128.Create((ushort)0xD800)) == Vector128<ushort>.AllBitsSet;
            bool noControlChars =
                Vector128.LessThan(data, Vector128.Create((ushort)' ')) == Vector128<ushort>.Zero;

            bool ok = mask == Expected && anyHit && noneOnClean
                && allBelowSurrogates && noControlChars;

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("[simd] lanes mask=0x");
            Console.WriteHex(mask);
            Console.Write(" want=0x");
            Console.WriteHex(Expected);
            Console.Write(" clean=");
            Console.Write(noneOnClean ? "1" : "0");
            Console.Write(" lt=");
            Console.Write(allBelowSurrogates ? "1" : "0");
            Console.Write(" ctrl=");
            Console.Write(noControlChars ? "1" : "0");
            Console.Write(ok ? " PASS" : " FAIL");
            Log.EndLine();
        }

        private static uint LaneMask(Vector128<ushort> value)
        {
            ushort* lanes = stackalloc ushort[8];
            Unsafe.WriteUnaligned(lanes, value);

            uint mask = 0;
            for (int i = 0; i < 8; i++)
            {
                if (lanes[i] != 0)
                    mask |= 1u << i;
            }

            return mask;
        }
    }
}
