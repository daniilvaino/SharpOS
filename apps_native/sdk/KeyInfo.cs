using System;
using SharpOS.Std;

namespace SharpOS.AppSdk
{
    public struct KeyInfo
    {
        public ushort UnicodeChar;
        public ushort ScanCode;

        // Raw set-1 key event (step143, from AppReadKeyRequest.Reserved):
        //   bits 0..7  make code
        //   bit  8     extended (0xE0-prefixed)
        //   bit  9     1 = key down, 0 = key up
        //   bits 10–12 Shift, Ctrl, Alt held after this event (step243)
        //   bit  31    raw info present (0 = pre-EBS legacy path, no raw)
        // Releases arrive with UnicodeChar/ScanCode == 0 -- legacy consumers
        // (the launcher's Enter/Esc matching) ignore them naturally.
        public uint Raw;

        public bool HasRaw => (Raw & 0x80000000u) != 0;
        public byte RawMake => (byte)(Raw & 0xFFu);
        public bool RawExtended => (Raw & 0x100u) != 0;
        public bool RawDown => (Raw & 0x200u) != 0;

        /// <summary>The modifiers held with this event.</summary>
        public ConsoleModifiers Modifiers
        {
            get
            {
                ConsoleModifiers m = 0;
                if ((Raw & 0x400u) != 0) m |= ConsoleModifiers.Shift;
                if ((Raw & 0x800u) != 0) m |= ConsoleModifiers.Control;
                if ((Raw & 0x1000u) != 0) m |= ConsoleModifiers.Alt;
                return m;
            }
        }

        /// <summary>
        /// The key as Console.ReadKey gives it — the same function the kernel
        /// builds the terminal's bytes with (step243). Default for a release
        /// or a modifier alone.
        /// </summary>
        public ConsoleKeyInfo ToConsoleKeyInfo()
            => HasRaw && !RawDown ? default : ConsoleKeys.FromKeyboard((char)UnicodeChar, ScanCode, Modifiers);
    }
}
