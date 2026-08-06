namespace OS.Hal
{
    // Where raw set-1 scancodes come from, whatever hardware produced them.
    // Named for the layer it is: OS.Kernel.Input.Keyboard sits above this and
    // turns these codes into key events.
    //
    // Both sources speak set-1 scancodes (the USB side translates), so this
    // is a source selector, not a translation layer — every consumer keeps
    // decoding exactly as before.
    //
    // Both are polled, not just the first that answers: a machine can have a
    // live 8042 and a USB keyboard at once, and the test machines have no
    // PS/2 at all.
    internal static class ScancodeSource
    {
        public static bool TryReadScancode(out byte scancode)
        {
            if (Ps2Keyboard.IsPresent() && Ps2Keyboard.TryReadScancode(out scancode))
                return true;

            return Usb.UsbKeyboard.TryReadScancode(out scancode);
        }

        /// <summary>Binds the USB keyboard, if the xHCI stack found one.</summary>
        public static bool TryAttachUsb() => Usb.UsbKeyboard.TryAttach();

        public static bool UsbAttached => Usb.UsbKeyboard.IsPresent;
    }
}
