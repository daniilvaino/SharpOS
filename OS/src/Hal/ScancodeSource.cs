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
        // Codes taken from the hardware before anyone asked for them.
        //
        // Both sources are polled, and nothing polls them while a command is
        // running: keys sat in the 8042 and in the USB ring until the shell
        // came back to read, so Ctrl+C could not interrupt anything — it was
        // noticed only once the work it was meant to stop had finished. The
        // pump thread drains them on its own schedule and parks them here;
        // readers see the same bytes in the same order, just already collected.
        private const int RingSize = 128;
        private static readonly byte[] s_ring = new byte[RingSize];
        private static int s_head;   // next write
        private static int s_tail;   // next read
        private static uint s_dropped;

        public static uint Dropped => s_dropped;

        /// <summary>Called by the input pump. Full ring drops the oldest.</summary>
        public static void Push(byte scancode)
        {
            int next = (s_head + 1) % RingSize;
            if (next == s_tail)
            {
                s_tail = (s_tail + 1) % RingSize;   // overwrite oldest
                s_dropped++;
            }
            s_ring[s_head] = scancode;
            s_head = next;
        }

        /// <summary>Takes directly from the hardware. For the pump only.</summary>
        public static bool TryReadHardware(out byte scancode)
        {
            if (Ps2Keyboard.IsPresent() && Ps2Keyboard.TryReadScancode(out scancode))
                return true;

            return Usb.UsbKeyboard.TryReadScancode(out scancode);
        }

        public static bool TryReadScancode(out byte scancode)
        {
            OS.Kernel.Threading.Preemption.Suppress();
            bool have = s_tail != s_head;
            if (have)
            {
                scancode = s_ring[s_tail];
                s_tail = (s_tail + 1) % RingSize;
            }
            else scancode = 0;
            OS.Kernel.Threading.Preemption.Allow();

            // Buffered first, hardware second: without a pump running (early
            // boot, probes) this behaves exactly as it always did.
            return have || TryReadHardware(out scancode);
        }

        /// <summary>Binds the USB keyboard, if the xHCI stack found one.</summary>
        public static bool TryAttachUsb() => Usb.UsbKeyboard.TryAttach();

        public static bool UsbAttached => Usb.UsbKeyboard.IsPresent;
    }
}
