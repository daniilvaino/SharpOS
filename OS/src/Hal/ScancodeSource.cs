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

        // Set once the input pump runs: from then on the pump is the only
        // reader of the hardware, and everyone else takes from the ring.
        //
        // Readers used to fall through to the hardware whenever the ring was
        // empty — from their own thread, while the pump read it too. A code the
        // pump had taken but not yet pushed was then overtaken by the next one
        // read directly: a Shift release came before the key it belonged to,
        // or was lost in the USB queue both threads touched unguarded. Fast
        // typing came out with Shift on the wrong keys and keys missing.
        private static volatile bool s_pumped;

        /// <summary>The input pump is running: readers take from the ring only.</summary>
        public static void PumpStarted() => s_pumped = true;

        /// <summary>Called by the input pump. Full ring drops the oldest.</summary>
        public static void Push(byte scancode)
        {
            // As TryReadScancode: a reader must not see the ring between the
            // two writes below.
            OS.Kernel.Threading.Preemption.Suppress();
            int next = (s_head + 1) % RingSize;
            if (next == s_tail)
            {
                s_tail = (s_tail + 1) % RingSize;   // overwrite oldest
                s_dropped++;
            }
            s_ring[s_head] = scancode;
            s_head = next;
            OS.Kernel.Threading.Preemption.Allow();
        }

        /// <summary>Takes directly from the hardware. For the pump only.</summary>
        public static bool TryReadHardware(out byte scancode)
        {
            if (Ps2Keyboard.IsPresent() && Ps2Keyboard.TryReadScancode(out scancode))
                return true;

            if (Usb.UsbKeyboard.TryReadScancode(out scancode))
                return true;

            // Last, and on the test rig the only one that answers: typing sent
            // down the USB serial port, translated to the same set-1 codes.
            // Polled on its own clock, so asking costs nothing between polls.
            if (OS.Kernel.Diagnostics.Probes.SerialInput
                && SerialInput.TryReadScancode(out scancode))
                return true;

            scancode = 0;
            return false;
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

            // Buffered first; the hardware only while no pump runs (early boot,
            // probes) — with one, the pump alone reads it (s_pumped).
            return have || (!s_pumped && TryReadHardware(out scancode));
        }

        /// <summary>Binds the USB keyboard, if the xHCI stack found one.</summary>
        public static bool TryAttachUsb() => Usb.UsbKeyboard.TryAttach();

        public static bool UsbAttached => Usb.UsbKeyboard.IsPresent;
    }
}
