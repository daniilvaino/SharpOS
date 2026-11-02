// Turning a keyboard event into the key the library expects.
//
// The kernel reports two things about a keystroke: the character it produced,
// if any, and the raw set-1 scan code with an extended flag and a make/break
// bit. Both matter, and neither is sufficient alone —
//
//   * a character has no arrow keys, no function keys, no Home;
//   * a scan code has no idea whether Shift was down, so it cannot tell A
//     from a.
//
// So the character wins where there is one, and the scan code answers for
// everything else. That is the same split the stock drivers make.

using SharpOS.AppSdk;

namespace Terminal.Gui
{
    internal static class SharpOSKeyMap
    {
        // Set-1 make codes for the modifiers. Tracked here because the kernel
        // reports them as ordinary keys, and the library wants them as flags on
        // the key that follows.
        private const byte ScanLeftShift = 0x2A;
        private const byte ScanRightShift = 0x36;
        private const byte ScanCtrl = 0x1D;
        private const byte ScanAlt = 0x38;

        private static bool s_shift;
        private static bool s_ctrl;
        private static bool s_alt;

        public static KeyEvent ToKeyEvent(KeyInfo info)
        {
            TrackModifiers(info);

            Key key = Translate(info);

            // Shift is already in a character (A, ?, "): the stock drivers mark
            // only keys without one, so Shift+arrows select. Marking characters
            // made TextView drop every shifted one (step232: "/*" in EDIT).
            if (s_shift && (key & Key.SpecialMask) != 0) key |= Key.ShiftMask;
            if (s_alt) key |= Key.AltMask;

            return new KeyEvent(key, new KeyModifiers
            {
                Shift = s_shift,
                Ctrl = s_ctrl,
                Alt = s_alt,
            });
        }

        private static void TrackModifiers(KeyInfo info)
        {
            if (!info.HasRaw) return;

            bool down = info.RawDown;

            switch (info.RawMake)
            {
                case ScanLeftShift:
                case ScanRightShift: s_shift = down; break;
                case ScanCtrl: s_ctrl = down; break;
                case ScanAlt: s_alt = down; break;
            }
        }

        private static Key Translate(KeyInfo info)
        {
            if (info.HasRaw)
            {
                Key special = FromScanCode(info.RawMake, info.RawExtended);
                if (special != Key.Unknown) return special;
            }

            char ch = (char)info.UnicodeChar;
            if (ch == 0) return Key.Unknown;

            // Ctrl+letter as the stock drivers give it — the capital with
            // CtrlMask: QuitKey is Key.Q | CtrlMask, TextView binds Key.Y |
            // CtrlMask and so on. The control code (Ctrl+Q as 17) matched none
            // of them (step230: EDIT did not quit). Shift rides along as a flag:
            // Ctrl+Shift+S is a binding of its own.
            if (s_ctrl && ch >= 'a' && ch <= 'z') return WithShift((Key)(ch - 'a' + 'A') | Key.CtrlMask);
            if (s_ctrl && ch >= 'A' && ch <= 'Z') return WithShift((Key)ch | Key.CtrlMask);
            // The kernel may report Ctrl+letter as its control code already.
            if (s_ctrl && ch >= 1 && ch <= 26) return WithShift((Key)(ch - 1 + 'A') | Key.CtrlMask);

            // Alt+letter as the stock drivers give it — the capital: the menu
            // bar compares its hotkeys upper-cased, and Alt+F never matched.
            if (s_alt && ch >= 'a' && ch <= 'z') return (Key)(ch - 'a' + 'A');

            return (Key)ch;
        }

        private static Key WithShift(Key key) => s_shift ? key | Key.ShiftMask : key;

        /// <summary>
        /// The keys that produce no character. Returns Unknown when the scan
        /// code is an ordinary key, so the character path can have it.
        /// </summary>
        private static Key FromScanCode(byte make, bool extended)
        {
            // Set-1 make codes. The navigation block appears twice on a PC
            // keyboard — once as the dedicated keys (E0-prefixed) and once on
            // the keypad with Num Lock off — and both send these codes, so the
            // extended flag is not consulted for them.
            switch (make)
            {
                case 0x01: return Key.Esc;
                case 0x0E: return Key.Backspace;
                case 0x0F: return s_shift ? Key.BackTab : Key.Tab;
                case 0x1C: return Key.Enter;

                case 0x48: return Key.CursorUp;
                case 0x50: return Key.CursorDown;
                case 0x4B: return Key.CursorLeft;
                case 0x4D: return Key.CursorRight;
                case 0x47: return Key.Home;
                case 0x4F: return Key.End;
                case 0x49: return Key.PageUp;
                case 0x51: return Key.PageDown;
                case 0x52: return Key.InsertChar;
                case 0x53: return Key.DeleteChar;

                // F1..F10 are consecutive from 0x3B; F11 and F12 are not, which
                // is why they are named rather than folded into the range.
                case 0x3B: return Key.F1;
                case 0x3C: return Key.F2;
                case 0x3D: return Key.F3;
                case 0x3E: return Key.F4;
                case 0x3F: return Key.F5;
                case 0x40: return Key.F6;
                case 0x41: return Key.F7;
                case 0x42: return Key.F8;
                case 0x43: return Key.F9;
                case 0x44: return Key.F10;
                case 0x57: return Key.F11;
                case 0x58: return Key.F12;
            }

            return Key.Unknown;
        }
    }
}
