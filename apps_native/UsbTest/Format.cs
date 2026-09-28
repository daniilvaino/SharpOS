using SharpOS.AppSdk;

namespace UsbTest
{
    // Turning USB's numbers into words.
    //
    // Separate from the views because both tabs and the details pane want the
    // same names, and a class code spelled one way in a tree row and another in
    // a detail line reads as two different devices.
    internal static class Format
    {
        public static string Hex2(byte value)
        {
            char[] chars = new char[2];
            chars[0] = Digit(value >> 4);
            chars[1] = Digit(value & 0xF);
            return new string(chars, 0, 2);
        }

        public static string Hex4(ushort value)
        {
            char[] chars = new char[4];
            chars[0] = Digit((value >> 12) & 0xF);
            chars[1] = Digit((value >> 8) & 0xF);
            chars[2] = Digit((value >> 4) & 0xF);
            chars[3] = Digit(value & 0xF);
            return new string(chars, 0, 4);
        }

        public static string Hex8(uint value)
            => Hex4((ushort)(value >> 16)) + Hex4((ushort)value);

        private static char Digit(int nibble)
            => (char)(nibble < 10 ? '0' + nibble : 'a' + (nibble - 10));

        public static string Speed(uint speed)
        {
            switch (speed)
            {
                case 1: return "full";
                case 2: return "low";
                case 3: return "high";
                case 4: return "super";
                case 5: return "super+";
                default: return "speed " + speed.ToString();
            }
        }

        public static string Class(uint cls, uint subclass, uint protocol)
        {
            string name = ClassName(cls);

            // Boot protocol is the one subclass worth spelling out: it is what
            // lets a keyboard be read without parsing its report descriptor.
            string suffix = cls == 0x03 && subclass == 1 ? " boot" : "";

            return name + "/" + subclass.ToString() + "/" + protocol.ToString() + suffix;
        }

        public static string ClassName(uint cls)
        {
            switch (cls)
            {
                // Zero at the device level means "see the interfaces", which is
                // what every composite device says. Named, because a bare 0
                // reads as missing information rather than as an answer.
                case 0x00: return "per-interface";
                case 0x01: return "audio";
                case 0x02: return "cdc-control";
                case 0x03: return "hid";
                case 0x05: return "physical";
                case 0x06: return "image";
                case 0x07: return "printer";
                case 0x08: return "mass-storage";
                case 0x09: return "hub";
                case 0x0A: return "cdc-data";
                case 0x0B: return "smart-card";
                case 0x0E: return "video";
                case 0xE0: return "wireless";
                case 0xFE: return "application";
                case 0xFF: return "vendor";
                default: return "class 0x" + Hex2((byte)cls);
            }
        }

        public static string EndpointType(byte type)
        {
            switch (type)
            {
                case 0: return "control";
                case 1: return "iso";
                case 2: return "bulk";
                case 3: return "interrupt";
                default: return "type " + type.ToString();
            }
        }

        public static string Status(AppServiceStatus status)
        {
            switch (status)
            {
                case AppServiceStatus.Ok: return "ok";
                case AppServiceStatus.NoData: return "no data";
                case AppServiceStatus.NotFound: return "not found";
                case AppServiceStatus.BufferTooSmall: return "buffer too small";
                case AppServiceStatus.InvalidParameter: return "invalid parameter";
                case AppServiceStatus.Unsupported: return "unsupported";
                case AppServiceStatus.DeviceError: return "device error";
                default: return "status " + ((uint)status).ToString();
            }
        }

        /// <summary>
        /// Offset, sixteen bytes, then the same bytes as text.
        /// </summary>
        /// <remarks>
        /// The text column earns its place: names of algorithms and extensions
        /// sit in the middle of a CBOR map and are readable from it without a
        /// decoder, which is how the key's reply was first understood at all.
        /// </remarks>
        public static unsafe string Dump(byte* data, int length)
        {
            var text = new System.Text.StringBuilder();

            for (int offset = 0; offset < length; offset += 16)
            {
                text.Append(Hex4((ushort)offset));
                text.Append("  ");

                for (int i = 0; i < 16; i++)
                {
                    if (offset + i < length)
                        text.Append(Hex2(data[offset + i]));
                    else
                        text.Append("  ");
                    text.Append(' ');
                }

                text.Append(' ');
                for (int i = 0; i < 16 && offset + i < length; i++)
                {
                    byte b = data[offset + i];
                    text.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }

                text.Append('\n');
            }

            return text.ToString();
        }
    }
}
