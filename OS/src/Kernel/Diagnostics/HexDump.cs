using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    internal static unsafe class HexDump
    {
        private const uint DefaultBytesPerRow = 16;

        public static void Dump(string label, void* pointer, uint length)
        {
            Dump(label, pointer, length, DefaultBytesPerRow);
        }

        public static void Dump(string label, void* pointer, uint length, uint bytesPerRow)
        {
            if (pointer == null)
            {
                Log.Write(LogLevel.Warn, "hexdump skipped: null pointer");
                return;
            }

            if (length == 0)
            {
                Log.Write(LogLevel.Warn, "hexdump skipped: empty length");
                return;
            }

            if (bytesPerRow == 0)
                bytesPerRow = DefaultBytesPerRow;

            byte* start = (byte*)pointer;

            Log.Begin(LogLevel.Info);
            Console.Write("hexdump ");
            Console.Write(label);
            Console.Write(" addr=0x");
            Console.WriteHex((ulong)pointer, 8);
            Console.Write(" len=");
            Console.WriteUInt(length);
            Log.EndLine();

            for (uint rowOffset = 0; rowOffset < length; rowOffset += bytesPerRow)
            {
                uint rowCount = length - rowOffset;
                if (rowCount > bytesPerRow)
                    rowCount = bytesPerRow;

                Log.Begin(LogLevel.Info);
                Console.Write("  ");
                Console.WriteHex((ulong)(start + rowOffset), 8);
                Console.Write(": ");

                for (uint i = 0; i < bytesPerRow; i++)
                {
                    if (i < rowCount)
                    {
                        Console.WriteHex(start[rowOffset + i], 2);
                    }
                    else
                    {
                        Console.Write("  ");
                    }

                    if (i + 1 != bytesPerRow)
                        Console.Write(" ");
                }

                Console.Write(" |");
                for (uint i = 0; i < rowCount; i++)
                {
                    byte value = start[rowOffset + i];
                    Console.WriteChar(IsPrintable(value) ? (char)value : '.');
                }

                for (uint i = rowCount; i < bytesPerRow; i++)
                    Console.WriteChar(' ');

                Console.Write("|");
                Log.EndLine();
            }
        }

        private static bool IsPrintable(byte value)
        {
            return value >= 32 && value <= 126;
        }
    }
}
