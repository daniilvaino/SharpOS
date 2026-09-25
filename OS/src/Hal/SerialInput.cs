namespace OS.Hal
{
    // Typing that arrives over the USB serial port, turned into set-1
    // scancodes so it is indistinguishable from a keyboard.
    //
    // The rig has no other way in. Its laptop has no PS/2, and the phone's HID
    // gadget presents a placeholder report descriptor whatever is written to
    // configfs — proven on 2026-09-25 against four independent readers,
    // including the phone's own vendor instance and a third-party app with its
    // own descriptor. A serial port asks nothing of the phone's kernel but
    // bytes, and both ends of it are ours.
    //
    // Translating to scancodes rather than characters is what makes this
    // universal: everything above reads keys the same way it always did, so
    // the launcher, the shell and DOOM all get input without knowing where it
    // came from. The cost is this file — a small ASCII-to-set-1 table and an
    // escape-sequence decoder, which is the price of speaking a keyboard's
    // language instead of inventing a second input path.
    internal static unsafe class SerialInput
    {
        // Scancodes waiting to be handed out. One character becomes up to four
        // of them (shift down, key down, key up, shift up), so a reader that
        // takes one at a time needs somewhere for the rest to sit.
        private const int PendingSize = 256;
        private static readonly byte[] s_pending = new byte[PendingSize];
        private static int s_head;
        private static int s_tail;

        // Bytes are asked for on a clock, not on demand. The input pump calls
        // the source up to sixty-four times per ten-millisecond pass, and an
        // empty read costs a transfer plus two ring commands to take back —
        // that would be hundreds of commands a second to learn that nobody is
        // typing. Forty milliseconds is invisible to a typist and cheap.
        private const uint PollIntervalMs = 40;

        // How long one poll may hold the CPU. UsbCdcAcm.Read suppresses
        // preemption for its duration, so this is two milliseconds of every
        // forty — five per cent, paid only while the port exists.
        private const uint ReadTimeoutMs = 2;
        private static ulong s_nextPoll;

        // Escape-sequence state: 0 nothing, 1 saw ESC, 2 saw ESC [.
        private static int s_escape;
        private static bool s_skipLineFeed;

        private static uint s_bytes;
        private static uint s_dropped;

        private static uint s_polls;

        public static uint BytesRead => s_bytes;
        public static uint Dropped => s_dropped;
        public static uint Polls => s_polls;

        /// <summary>
        /// One line on the diagnostics channel, from the sampler's idle
        /// window. Says whether anything is arriving at all.
        /// </summary>
        /// <remarks>
        /// Here because its absence cost a round: forty keystrokes were sent
        /// down the port and nothing happened, and "the machine ignored them"
        /// could not be told from "they never arrived" without rebuilding.
        /// polls=0 means the port was never asked; bytes=0 with polls rising
        /// means it was asked and had nothing.
        /// </remarks>
        public static void Report()
        {
            if (!Usb.UsbCdcAcm.IsPresent) return;

            Put("[serialin] polls=");
            PutUInt(s_polls);
            Put(" bytes=");
            PutUInt(s_bytes);
            Put(" dropped=");
            PutUInt(s_dropped);
            Put("\n");
        }

        // Straight to the diagnostics channel, a character at a time: this
        // runs from the sampler on a timer tick, where a formatter that
        // allocates would ask the heap from inside an interrupt.
        private static void Put(string text)
        {
            for (int i = 0; i < text.Length; i++)
                Platform.WriteChar(text[i], OutputChannel.Perf);
        }

        private static void PutUInt(uint value)
        {
            char* digits = stackalloc char[10];
            int count = 0;
            do
            {
                digits[count++] = (char)('0' + (int)(value % 10u));
                value /= 10u;
            }
            while (value != 0);

            while (count > 0)
                Platform.WriteChar(digits[--count], OutputChannel.Perf);
        }

        /// <summary>
        /// One scancode from the serial line, or false when there is nothing.
        /// Shaped like the other sources so ScancodeSource can just ask.
        /// </summary>
        public static bool TryReadScancode(out byte scancode)
        {
            if (TryPop(out scancode)) return true;

            if (!Usb.UsbCdcAcm.IsPresent) return false;
            if (!DuePoll()) return false;

            // A real timeout, not zero. Zero means "check the deadline on the
            // first turn", which is before the controller has had any chance
            // at all — the transfer was queued and taken back in the same
            // breath, and forty keystrokes arrived as nothing. Two
            // milliseconds is far longer than a bulk IN with data waiting
            // needs, and it is spent at most once per poll interval.
            byte* buffer = stackalloc byte[32];
            int n = Usb.UsbCdcAcm.Read(buffer, 32, ReadTimeoutMs);
            if (n <= 0)
            {
                // A lone ESC is a real key, and it is also how every arrow
                // starts. Holding it for one interval tells them apart; after
                // that, nothing followed, so it was the key.
                if (s_escape == 1) { s_escape = 0; PushKey(0x01, false); }
                return TryPop(out scancode);
            }

            s_bytes += (uint)n;
            for (int i = 0; i < n; i++)
            {
                // Whole characters or none. One character is up to four codes,
                // and stopping halfway through would hand out a key press whose
                // release never comes — a key stuck down for the rest of the
                // session, which is worse than a character that never arrived.
                if (Free() < 4) { s_dropped++; break; }
                Decode(buffer[i]);
            }
            return TryPop(out scancode);
        }

        private static bool DuePoll()
        {
            if (!Timer.Hpet.IsInitialized) return true;   // no clock yet: ask every time

            ulong now = Timer.Hpet.ReadCounter();
            if (now < s_nextPoll) return false;

            ulong perMs = Timer.Hpet.FrequencyHz / 1000;
            s_nextPoll = now + perMs * PollIntervalMs;
            s_polls++;
            return true;
        }

        private static void Decode(byte b)
        {
            if (s_escape == 1)
            {
                if (b == (byte)'[') { s_escape = 2; return; }
                s_escape = 0;
                PushKey(0x01, false);          // the ESC that was waiting
                // and then this byte, whatever it is
            }
            else if (s_escape == 2)
            {
                s_escape = 0;
                // Arrows and friends arrive as ESC [ letter, and go out as the
                // two-byte extended codes a PC keyboard sends for the grey
                // keys — which is what every decoder above already expects.
                byte extended = b switch
                {
                    (byte)'A' => 0x48,   // up
                    (byte)'B' => 0x50,   // down
                    (byte)'C' => 0x4D,   // right
                    (byte)'D' => 0x4B,   // left
                    (byte)'H' => 0x47,   // home
                    (byte)'F' => 0x4F,   // end
                    _ => 0,
                };
                if (extended != 0) PushExtended(extended);
                return;
            }

            if (b == 0x1B) { s_escape = 1; return; }

            // CR, or CR LF, or LF: one Enter either way. Without this a line
            // typed from a terminal that sends both would submit twice.
            if (b == 0x0D) { s_skipLineFeed = true; PushKey(0x1C, false); return; }
            if (b == 0x0A)
            {
                if (s_skipLineFeed) { s_skipLineFeed = false; return; }
                PushKey(0x1C, false);
                return;
            }
            s_skipLineFeed = false;

            if (b == 0x08 || b == 0x7F) { PushKey(0x0E, false); return; }   // backspace
            if (b == 0x09) { PushKey(0x0F, false); return; }                // tab

            // Control characters: a terminal sends Ctrl+C as byte 3. Sent as
            // the real chord, so the Ctrl+C detector in the input pump sees
            // what it is watching for rather than a character nobody typed.
            if (b >= 1 && b <= 26)
            {
                byte letter = ScancodeOfChar((char)('a' + b - 1), out _);
                if (letter != 0) PushChord(0x1D, letter);
                return;
            }

            byte code = ScancodeOfChar((char)b, out bool shifted);
            if (code != 0) PushKey(code, shifted);
        }

        // Make codes of a US layout, set 1. The break code is the make code
        // with the top bit set, which is why only one number per key is here.
        private static byte ScancodeOfChar(char c, out bool shifted)
        {
            shifted = false;

            if (c >= 'a' && c <= 'z') return LetterCode(c);
            if (c >= 'A' && c <= 'Z') { shifted = true; return LetterCode((char)(c + 32)); }
            if (c >= '1' && c <= '9') return (byte)(0x02 + (c - '1'));
            if (c == '0') return 0x0B;

            switch (c)
            {
                case ' ': return 0x39;
                case '-': return 0x0C;
                case '=': return 0x0D;
                case '[': return 0x1A;
                case ']': return 0x1B;
                case ';': return 0x27;
                case '\'': return 0x28;
                case '`': return 0x29;
                case '\\': return 0x2B;
                case ',': return 0x33;
                case '.': return 0x34;
                case '/': return 0x35;
            }

            shifted = true;
            switch (c)
            {
                case '!': return 0x02;
                case '@': return 0x03;
                case '#': return 0x04;
                case '$': return 0x05;
                case '%': return 0x06;
                case '^': return 0x07;
                case '&': return 0x08;
                case '*': return 0x09;
                case '(': return 0x0A;
                case ')': return 0x0B;
                case '_': return 0x0C;
                case '+': return 0x0D;
                case '{': return 0x1A;
                case '}': return 0x1B;
                case ':': return 0x27;
                case '"': return 0x28;
                case '~': return 0x29;
                case '|': return 0x2B;
                case '<': return 0x33;
                case '>': return 0x34;
                case '?': return 0x35;
            }

            shifted = false;
            return 0;
        }

        private static byte LetterCode(char c)
            => c switch
            {
                'q' => 0x10, 'w' => 0x11, 'e' => 0x12, 'r' => 0x13, 't' => 0x14,
                'y' => 0x15, 'u' => 0x16, 'i' => 0x17, 'o' => 0x18, 'p' => 0x19,
                'a' => 0x1E, 's' => 0x1F, 'd' => 0x20, 'f' => 0x21, 'g' => 0x22,
                'h' => 0x23, 'j' => 0x24, 'k' => 0x25, 'l' => 0x26,
                'z' => 0x2C, 'x' => 0x2D, 'c' => 0x2E, 'v' => 0x2F, 'b' => 0x30,
                'n' => 0x31, 'm' => 0x32,
                _ => 0,
            };

        private const byte LeftShiftMake = 0x2A;
        private const byte Extended = 0xE0;

        private static void PushKey(byte make, bool shifted)
        {
            if (shifted) Push(LeftShiftMake);
            Push(make);
            Push((byte)(make | 0x80));
            if (shifted) Push((byte)(LeftShiftMake | 0x80));
        }

        private static void PushChord(byte modifierMake, byte make)
        {
            Push(modifierMake);
            Push(make);
            Push((byte)(make | 0x80));
            Push((byte)(modifierMake | 0x80));
        }

        private static void PushExtended(byte make)
        {
            Push(Extended);
            Push(make);
            Push(Extended);
            Push((byte)(make | 0x80));
        }

        // Room left, in scancodes. The decoder checks this before each
        // character rather than each code, so a character is either fully
        // queued or not started.
        private static int Free()
        {
            int used = s_head - s_tail;
            if (used < 0) used += PendingSize;
            return PendingSize - 1 - used;
        }

        private static void Push(byte scancode)
        {
            int next = (s_head + 1) % PendingSize;
            if (next == s_tail) { s_dropped++; return; }   // Free() keeps this unreachable
            s_pending[s_head] = scancode;
            s_head = next;
        }

        private static bool TryPop(out byte scancode)
        {
            if (s_tail == s_head) { scancode = 0; return false; }
            scancode = s_pending[s_tail];
            s_tail = (s_tail + 1) % PendingSize;
            return true;
        }
    }
}
