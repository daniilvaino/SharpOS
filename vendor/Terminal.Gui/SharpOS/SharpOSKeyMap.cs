// Turning a keyboard event into the key the library expects.
//
// The kernel hands over key events as Windows does: a press and a release, the
// key as a ConsoleKeyInfo (built by the same function the terminal's bytes are
// built with, SharpOS.Std.ConsoleKeys, step243), and the modifiers held. So the
// mapping is the stock WindowsDriver's — MapKey and MapKeyModifiers copied from
// Terminal.Gui/ConsoleDrivers/WindowsDriver.cs, and the modifier-alone events
// as its ProcessInput makes them (Alt alone opens the menu bar on release).
//
// SharpOS cut: the NumPad cases of MapKey (the kernel reports the keypad as the
// keys it types) and the ConsoleKeyInfoEx wrapper they needed.

using System;
using SharpOS.AppSdk;

namespace Terminal.Gui
{
    internal static class SharpOSKeyMap
    {
        // Set-1 make codes of the modifiers, for the events of a modifier alone:
        // WindowsDriver tells them by their virtual key codes.
        private const byte ScanLeftShift = 0x2A;
        private const byte ScanRightShift = 0x36;
        private const byte ScanCtrl = 0x1D;
        private const byte ScanAlt = 0x38;

        /// <summary>No key in the event: a release, or a modifier alone.</summary>
        public const Key NoKey = (Key)0xffffffff;

        public static KeyModifiers Modifiers(KeyInfo info)
        {
            ConsoleModifiers m = info.Modifiers;
            return new KeyModifiers
            {
                Shift = (m & ConsoleModifiers.Shift) != 0,
                Ctrl = (m & ConsoleModifiers.Control) != 0,
                Alt = (m & ConsoleModifiers.Alt) != 0,
            };
        }

        /// <summary>The key of an event MapKey has none for (WindowsDriver: by virtual key code).</summary>
        public static Key ModifierKey(KeyInfo info)
        {
            if (!info.HasRaw) return Key.Unknown;
            switch (info.RawMake)
            {
                case ScanLeftShift:
                case ScanRightShift: return Key.ShiftMask;
                case ScanCtrl: return Key.CtrlMask;
                case ScanAlt: return Key.AltMask;
                default: return Key.Unknown;
            }
        }

        public static Key MapKey(ConsoleKeyInfo keyInfo)
        {
            switch (keyInfo.Key) {
            case ConsoleKey.Escape:
                return MapKeyModifiers (keyInfo, Key.Esc);
            case ConsoleKey.Tab:
                return keyInfo.Modifiers == ConsoleModifiers.Shift ? Key.BackTab : Key.Tab;
            case ConsoleKey.Clear:
                return MapKeyModifiers (keyInfo, Key.Clear);
            case ConsoleKey.Home:
                return MapKeyModifiers (keyInfo, Key.Home);
            case ConsoleKey.End:
                return MapKeyModifiers (keyInfo, Key.End);
            case ConsoleKey.LeftArrow:
                return MapKeyModifiers (keyInfo, Key.CursorLeft);
            case ConsoleKey.RightArrow:
                return MapKeyModifiers (keyInfo, Key.CursorRight);
            case ConsoleKey.UpArrow:
                return MapKeyModifiers (keyInfo, Key.CursorUp);
            case ConsoleKey.DownArrow:
                return MapKeyModifiers (keyInfo, Key.CursorDown);
            case ConsoleKey.PageUp:
                return MapKeyModifiers (keyInfo, Key.PageUp);
            case ConsoleKey.PageDown:
                return MapKeyModifiers (keyInfo, Key.PageDown);
            case ConsoleKey.Enter:
                return MapKeyModifiers (keyInfo, Key.Enter);
            case ConsoleKey.Spacebar:
                return MapKeyModifiers (keyInfo, keyInfo.KeyChar == 0 ? Key.Space : (Key)keyInfo.KeyChar);
            case ConsoleKey.Backspace:
                return MapKeyModifiers (keyInfo, Key.Backspace);
            case ConsoleKey.Delete:
                return MapKeyModifiers (keyInfo, Key.DeleteChar);
            case ConsoleKey.Insert:
                return MapKeyModifiers (keyInfo, Key.InsertChar);
            case ConsoleKey.PrintScreen:
                return MapKeyModifiers (keyInfo, Key.PrintScreen);

            // SharpOS cut: ConsoleKey.NumPad0..NumPad9.

            case ConsoleKey.Oem1:
            case ConsoleKey.Oem2:
            case ConsoleKey.Oem3:
            case ConsoleKey.Oem4:
            case ConsoleKey.Oem5:
            case ConsoleKey.Oem6:
            case ConsoleKey.Oem7:
            case ConsoleKey.Oem8:
            case ConsoleKey.Oem102:
            case ConsoleKey.OemPeriod:
            case ConsoleKey.OemComma:
            case ConsoleKey.OemPlus:
            case ConsoleKey.OemMinus:
                if (keyInfo.KeyChar == 0)
                    return Key.Unknown;

                return (Key)((uint)keyInfo.KeyChar);
            }

            var key = keyInfo.Key;

            if (key >= ConsoleKey.A && key <= ConsoleKey.Z) {
                var delta = key - ConsoleKey.A;
                if (keyInfo.Modifiers == ConsoleModifiers.Control) {
                    return (Key)(((uint)Key.CtrlMask) | ((uint)Key.A + delta));
                }
                if (keyInfo.Modifiers == ConsoleModifiers.Alt) {
                    return (Key)(((uint)Key.AltMask) | ((uint)Key.A + delta));
                }
                if (keyInfo.Modifiers == (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) {
                    return MapKeyModifiers (keyInfo, (Key)((uint)Key.A + delta));
                }
                if ((keyInfo.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0) {
                    if (keyInfo.KeyChar == 0 || (keyInfo.KeyChar != 0 && keyInfo.KeyChar >= 1 && keyInfo.KeyChar <= 26)) {
                        return MapKeyModifiers (keyInfo, (Key)((uint)Key.A + delta));
                    }
                }
                return (Key)((uint)keyInfo.KeyChar);
            }
            if (key >= ConsoleKey.D0 && key <= ConsoleKey.D9) {
                var delta = key - ConsoleKey.D0;
                if (keyInfo.Modifiers == ConsoleModifiers.Alt) {
                    return (Key)(((uint)Key.AltMask) | ((uint)Key.D0 + delta));
                }
                if (keyInfo.Modifiers == ConsoleModifiers.Control) {
                    return (Key)(((uint)Key.CtrlMask) | ((uint)Key.D0 + delta));
                }
                if (keyInfo.Modifiers == (ConsoleModifiers.Shift | ConsoleModifiers.Alt)) {
                    return MapKeyModifiers (keyInfo, (Key)((uint)Key.D0 + delta));
                }
                if ((keyInfo.Modifiers & (ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0) {
                    if (keyInfo.KeyChar == 0 || keyInfo.KeyChar == 30 || keyInfo.KeyChar == ((uint)Key.D0 + delta)) {
                        return MapKeyModifiers (keyInfo, (Key)((uint)Key.D0 + delta));
                    }
                }
                return (Key)((uint)keyInfo.KeyChar);
            }
            if (key >= ConsoleKey.F1 && key <= ConsoleKey.F12) {
                var delta = key - ConsoleKey.F1;
                if ((keyInfo.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Alt | ConsoleModifiers.Control)) != 0) {
                    return MapKeyModifiers (keyInfo, (Key)((uint)Key.F1 + delta));
                }

                return (Key)((uint)Key.F1 + delta);
            }
            if (keyInfo.KeyChar != 0) {
                return MapKeyModifiers (keyInfo, (Key)((uint)keyInfo.KeyChar));
            }

            return (Key)(0xffffffff);
        }

        private static Key MapKeyModifiers (ConsoleKeyInfo keyInfo, Key key)
        {
            Key keyMod = new Key ();
            if ((keyInfo.Modifiers & ConsoleModifiers.Shift) != 0)
                keyMod = Key.ShiftMask;
            if ((keyInfo.Modifiers & ConsoleModifiers.Control) != 0)
                keyMod |= Key.CtrlMask;
            if ((keyInfo.Modifiers & ConsoleModifiers.Alt) != 0)
                keyMod |= Key.AltMask;

            return keyMod != Key.Null ? keyMod | key : key;
        }
    }
}
