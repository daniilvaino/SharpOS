// BitConverter.GetBytes (step223, the XZ reader), as BCL: little-endian
// bytes of the value, through BinaryPrimitives.

using System.Buffers.Binary;

namespace System
{
    public static partial class BitConverter
    {
        public static byte[] GetBytes(bool value) => new byte[] { value ? (byte)1 : (byte)0 };

        public static byte[] GetBytes(char value) => GetBytes((short)value);

        public static byte[] GetBytes(short value)
        {
            var bytes = new byte[sizeof(short)];
            BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
            return bytes;
        }

        public static byte[] GetBytes(int value)
        {
            var bytes = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            return bytes;
        }

        public static byte[] GetBytes(long value)
        {
            var bytes = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            return bytes;
        }

        public static byte[] GetBytes(ushort value) => GetBytes((short)value);

        public static byte[] GetBytes(uint value) => GetBytes((int)value);

        public static byte[] GetBytes(ulong value) => GetBytes((long)value);
    }
}
