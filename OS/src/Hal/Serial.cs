namespace OS.Hal
{
    // Phase B — own 16550 UART (COM1) driver. Until now ALL kernel/serial
    // output went through UEFI ConOut (OVMF SerialDxe mirror); that path
    // dies at ExitBootServices. This is a standalone, polled, no-interrupt
    // 16550 driver talking the chip directly via PortIo — the post-EBS
    // diagnostic substrate (prerequisite for Phase D SehUnwind work and
    // the native-tier off-ramp).
    //
    // Standard COM1 @ 0x3F8, 115200 8N1, FIFO on. 1:1 with the canonical
    // 16550 bring-up (OSDev/reference). Requires PortIoPatcher installed
    // (it is, early in boot — Rtc/CMOS already uses PortIo).
    internal static unsafe class Serial
    {
        private const ushort Com1 = 0x3F8;

        // Register offsets from the port base.
        private const ushort Data       = 0; // RBR/THR (DLAB=0) | DLL (DLAB=1)
        private const ushort IntEnable  = 1; // IER     (DLAB=0) | DLM (DLAB=1)
        private const ushort FifoCtrl   = 2; // FCR (write) / IIR (read)
        private const ushort LineCtrl   = 3; // LCR  (bit7 = DLAB)
        private const ushort ModemCtrl  = 4; // MCR
        private const ushort LineStatus = 5; // LSR  (bit5 = THR empty, bit0 = data ready)

        private static bool s_ready;
        private static bool s_present;

        public static bool IsReady => s_ready;
        public static bool IsPresent => s_present;

        // Initialize COM1 and run a loopback self-test. Returns true if a
        // 16550 actually answered (false on QEMU `-serial none` / no chip;
        // callers then keep using the UEFI mirror while it's available).
        public static bool Init()
        {
            if (s_ready) return s_present;

            PortIo.Out8(Com1 + IntEnable, 0x00);   // disable all UART interrupts (polled)
            PortIo.Out8(Com1 + LineCtrl, 0x80);    // DLAB = 1 (access divisor latch)
            PortIo.Out8(Com1 + Data, 0x01);        // divisor low  = 1  → 115200 baud
            PortIo.Out8(Com1 + IntEnable, 0x00);   // divisor high = 0
            PortIo.Out8(Com1 + LineCtrl, 0x03);    // DLAB = 0, 8 bits, no parity, 1 stop
            PortIo.Out8(Com1 + FifoCtrl, 0xC7);    // FIFO enable + clear RX/TX, 14-byte trigger
            PortIo.Out8(Com1 + ModemCtrl, 0x0B);   // DTR | RTS | OUT2

            // Loopback self-test: MCR loopback on, send a byte, read back.
            PortIo.Out8(Com1 + ModemCtrl, 0x1E);   // loopback | DTR | RTS | OUT2
            PortIo.Out8(Com1 + Data, 0xAE);
            byte echo = PortIo.In8(Com1 + Data);
            s_present = (echo == 0xAE);

            // Back to normal operating mode regardless of test outcome.
            PortIo.Out8(Com1 + ModemCtrl, 0x0F);   // DTR | RTS | OUT1 | OUT2
            s_ready = true;
            return s_present;
        }

        // Polled transmit: spin until the THR (transmit holding register)
        // is empty, then push the byte. No interrupts, no buffering.
        public static void WriteByte(byte b)
        {
            if (!s_ready) return;
            // LSR bit 5 (0x20) = THR empty / ready to accept next byte.
            while ((PortIo.In8(Com1 + LineStatus) & 0x20) == 0) { }
            PortIo.Out8(Com1 + Data, b);
        }

        // '\n' -> CRLF so terminals / captured logs render correctly
        // (matches the CRLF the UEFI ConOut mirror produced). Non-ASCII
        // codepoints are UTF-8 encoded rather than truncated to a single
        // garbage byte, so box/block glyphs (FETCH's banner) survive the
        // post-EBS serial path into any UTF-8 terminal / QEMU log. BMP
        // only — chars are UTF-16 code units (no surrogate pairing).
        public static void WriteChar(char c)
        {
            if (c == '\n') { WriteByte((byte)'\r'); WriteByte((byte)'\n'); return; }
            uint cp = c;
            if (cp < 0x80)
            {
                WriteByte((byte)cp);
            }
            else if (cp < 0x800)
            {
                WriteByte((byte)(0xC0 | (cp >> 6)));
                WriteByte((byte)(0x80 | (cp & 0x3F)));
            }
            else
            {
                WriteByte((byte)(0xE0 | (cp >> 12)));
                WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
                WriteByte((byte)(0x80 | (cp & 0x3F)));
            }
        }

        public static void WriteString(string s)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++) WriteChar(s[i]);
        }
    }
}
