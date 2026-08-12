namespace OS.Hal.Usb
{
    // A USB keyboard presented as a stream of set-1 scancodes.
    //
    // Translating to the PS/2 encoding rather than inventing a second key
    // format is the whole point: five consumers already decode set-1
    // (line editor, shell, console input, app key service, DOOM), and every
    // one of them keeps working untouched. It is also exactly what a BIOS
    // does for legacy USB support.
    //
    // HID reports state, not events: each report lists the keys currently
    // held. Presses and releases have to be recovered by comparing against
    // the previous report — a key in the new one but not the old is a make,
    // the reverse is a break.
    internal static unsafe class UsbKeyboard
    {
        private const int QueueSize = 64;
        private static readonly byte[] s_queue = new byte[QueueSize];
        private static int s_head, s_tail;

        private static readonly byte[] s_previous = new byte[8];
        // The controller is held alongside the slot: a slot number is only
        // meaningful to the controller that issued it, and on a machine with
        // several, the keyboard and the disk are not on the same one.
        private static XhciController s_hc;
        private static uint s_slot;
        private static bool s_present;

        public static bool IsPresent => s_present;

        /// <summary>Binds to the first configured boot-protocol keyboard.</summary>
        public static bool TryAttach()
        {
            if (s_present) return true;

            if (!Xhci.TryFindHid(1, out XhciController hc, out uint slot)) return false;

            s_hc = hc;
            s_slot = slot;
            s_present = true;
            hc.TryQueueReport(slot);
            return true;
        }

        /// <summary>
        /// Non-blocking: collect any completed report, turn the difference
        /// from the previous one into scancodes, and re-arm the endpoint.
        /// </summary>
        // Same boundary guard as the mass-storage path: a poll drives a TRB
        // through the interrupt ring and waits for its event.
        public static void Poll()
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try { PollCore(); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        private static void PollCore()
        {
            if (!s_present) return;

            byte* report = stackalloc byte[8];
            if (s_hc.TryCollectReport(s_slot, report, 8, 0))
                Translate(report);

            // Always re-arm: a read that completed leaves the endpoint idle,
            // and one that never started would mean no further keys at all.
            s_hc.TryQueueReport(s_slot);
        }

        public static bool TryReadScancode(out byte scancode)
        {
            scancode = 0;
            if (s_head == s_tail)
            {
                Poll();
                if (s_head == s_tail) return false;
            }
            scancode = s_queue[s_tail];
            s_tail = (s_tail + 1) % QueueSize;
            return true;
        }

        private static void Enqueue(byte scancode)
        {
            int next = (s_head + 1) % QueueSize;
            if (next == s_tail) return;      // full: drop, never overwrite unread
            s_queue[s_head] = scancode;
            s_head = next;
        }

        private static void EnqueueKey(byte usage, bool release)
        {
            byte set1 = ToSet1(usage, out bool extended);
            if (set1 == 0) return;
            if (extended) Enqueue(0xE0);
            Enqueue(release ? (byte)(set1 | 0x80) : set1);
        }

        private static void Translate(byte* report)
        {
            byte modifiers = report[0];
            byte wasModifiers = s_previous[0];
            if (modifiers != wasModifiers)
                TranslateModifiers(modifiers, wasModifiers);

            // Bytes 2..7 hold up to six concurrently held keys, unordered.
            for (int i = 2; i < 8; i++)
            {
                byte usage = report[i];
                if (usage <= 3) continue;       // 0 = empty, 1..3 = error states
                if (!Contains(s_previous, usage))
                    EnqueueKey(usage, release: false);
            }

            for (int i = 2; i < 8; i++)
            {
                byte usage = s_previous[i];
                if (usage <= 3) continue;
                if (!ContainsPtr(report, usage))
                    EnqueueKey(usage, release: true);
            }

            for (int i = 0; i < 8; i++) s_previous[i] = report[i];
        }

        private static void TranslateModifiers(byte now, byte before)
        {
            EmitModifier(now, before, 0x01, 0x1D, false);   // left ctrl
            EmitModifier(now, before, 0x02, 0x2A, false);   // left shift
            EmitModifier(now, before, 0x04, 0x38, false);   // left alt
            EmitModifier(now, before, 0x10, 0x1D, true);    // right ctrl
            EmitModifier(now, before, 0x20, 0x36, false);   // right shift
            EmitModifier(now, before, 0x40, 0x38, true);    // right alt
        }

        private static void EmitModifier(byte now, byte before, byte bit, byte set1, bool extended)
        {
            bool isDown = (now & bit) != 0;
            bool wasDown = (before & bit) != 0;
            if (isDown == wasDown) return;
            if (extended) Enqueue(0xE0);
            Enqueue(isDown ? set1 : (byte)(set1 | 0x80));
        }

        private static bool Contains(byte[] report, byte usage)
        {
            for (int i = 2; i < 8; i++)
                if (report[i] == usage) return true;
            return false;
        }

        private static bool ContainsPtr(byte* report, byte usage)
        {
            for (int i = 2; i < 8; i++)
                if (report[i] == usage) return true;
            return false;
        }

        // HID usage -> set-1 scancode. A switch rather than a table: a
        // static readonly array would need a class constructor, which is the
        // one thing this environment cannot run (see limits §1).
        private static byte ToSet1(byte usage, out bool extended)
        {
            extended = false;

            if (usage >= 0x04 && usage <= 0x1D)
            {
                // a..z in HID order; set-1 letters are scattered, so spell it.
                switch (usage)
                {
                    case 0x04: return 0x1E; case 0x05: return 0x30;
                    case 0x06: return 0x2E; case 0x07: return 0x20;
                    case 0x08: return 0x12; case 0x09: return 0x21;
                    case 0x0A: return 0x22; case 0x0B: return 0x23;
                    case 0x0C: return 0x17; case 0x0D: return 0x24;
                    case 0x0E: return 0x25; case 0x0F: return 0x26;
                    case 0x10: return 0x32; case 0x11: return 0x31;
                    case 0x12: return 0x18; case 0x13: return 0x19;
                    case 0x14: return 0x10; case 0x15: return 0x13;
                    case 0x16: return 0x1F; case 0x17: return 0x14;
                    case 0x18: return 0x16; case 0x19: return 0x2F;
                    case 0x1A: return 0x11; case 0x1B: return 0x2D;
                    case 0x1C: return 0x15; default: return 0x2C;
                }
            }

            // 1..9 then 0 — contiguous in both encodings.
            if (usage >= 0x1E && usage <= 0x27) return (byte)(usage - 0x1E + 0x02);

            // F1..F10 contiguous; F11/F12 sit elsewhere in set 1.
            if (usage >= 0x3A && usage <= 0x43) return (byte)(usage - 0x3A + 0x3B);

            switch (usage)
            {
                case 0x28: return 0x1C;   // enter
                case 0x29: return 0x01;   // escape
                case 0x2A: return 0x0E;   // backspace
                case 0x2B: return 0x0F;   // tab
                case 0x2C: return 0x39;   // space
                case 0x2D: return 0x0C;   // -
                case 0x2E: return 0x0D;   // =
                case 0x2F: return 0x1A;   // [
                case 0x30: return 0x1B;   // ]
                case 0x31: return 0x2B;   // backslash
                case 0x33: return 0x27;   // ;
                case 0x34: return 0x28;   // '
                case 0x35: return 0x29;   // `
                case 0x36: return 0x33;   // ,
                case 0x37: return 0x34;   // .
                case 0x38: return 0x35;   // /
                case 0x39: return 0x3A;   // caps lock
                case 0x44: return 0x57;   // F11
                case 0x45: return 0x58;   // F12

                // The navigation cluster is 0xE0-prefixed in set 1, which is
                // what the existing decoder expects for arrows.
                case 0x49: extended = true; return 0x52;   // insert
                case 0x4A: extended = true; return 0x47;   // home
                case 0x4B: extended = true; return 0x49;   // page up
                case 0x4C: extended = true; return 0x53;   // delete
                case 0x4D: extended = true; return 0x4F;   // end
                case 0x4E: extended = true; return 0x51;   // page down
                case 0x4F: extended = true; return 0x4D;   // right
                case 0x50: extended = true; return 0x4B;   // left
                case 0x51: extended = true; return 0x50;   // down
                case 0x52: extended = true; return 0x48;   // up

                default: return 0;
            }
        }
    }
}
