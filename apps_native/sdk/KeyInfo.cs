namespace SharpOS.AppSdk
{
    internal struct KeyInfo
    {
        public ushort UnicodeChar;
        public ushort ScanCode;

        // Raw set-1 key event (step143, from AppReadKeyRequest.Reserved):
        //   bits 0..7  make code
        //   bit  8     extended (0xE0-prefixed)
        //   bit  9     1 = key down, 0 = key up
        //   bit  31    raw info present (0 = pre-EBS legacy path, no raw)
        // Releases arrive with UnicodeChar/ScanCode == 0 -- legacy consumers
        // (the launcher's Enter/Esc matching) ignore them naturally.
        public uint Raw;

        public bool HasRaw => (Raw & 0x80000000u) != 0;
        public byte RawMake => (byte)(Raw & 0xFFu);
        public bool RawExtended => (Raw & 0x100u) != 0;
        public bool RawDown => (Raw & 0x200u) != 0;
    }
}
