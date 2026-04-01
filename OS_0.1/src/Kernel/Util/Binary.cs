namespace OS.Kernel.Util
{
    internal static unsafe class Binary
    {
        public static bool Read7BitInt(byte* pointer, out int value, out uint bytesRead)
        {
            value = 0;
            bytesRead = 0;

            if (pointer == null)
                return false;

            int shift = 0;
            for (int i = 0; i < 5; i++)
            {
                byte b = pointer[i];
                bytesRead++;
                value |= (b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                    return true;

                shift += 7;
            }

            value = 0;
            bytesRead = 0;
            return false;
        }

        public static bool ReadPrefixedBytes(
            byte* source,
            uint sourceLength,
            byte* destination,
            uint destinationLength,
            out uint consumed,
            out uint written)
        {
            consumed = 0;
            written = 0;

            if (source == null)
                return false;

            if (!Read7BitInt(source, out int payloadLength, out uint headerBytes))
                return false;

            if (payloadLength < 0)
                return false;

            uint payload = (uint)payloadLength;
            if (headerBytes > sourceLength || payload > sourceLength - headerBytes)
                return false;

            consumed = headerBytes + payload;
            if (destination == null || destinationLength == 0)
                return true;

            uint bytesToCopy = payload;
            if (bytesToCopy > destinationLength)
                bytesToCopy = destinationLength;

            Memory.MemCopy(destination, source + headerBytes, bytesToCopy);
            written = bytesToCopy;
            return true;
        }
    }
}
