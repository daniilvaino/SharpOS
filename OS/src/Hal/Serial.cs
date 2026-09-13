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
    //
    // COM1 is the kernel's log: the host tees it into last_build.log and the
    // tools read that file. COM3 @ 0x3E8, when the machine has one, carries
    // what programs print (see OutputRouting), so an application's output
    // neither costs the kernel log its UART time nor lands in the middle of it.
    //
    // COM3 and not COM2: the firmware attaches a console of its own to every
    // port on its fixed list, and that list is COM1 and COM2 (OVMF SioBusDxe).
    // A program port at 0x2F8 opened with a full copy of the firmware's and
    // the early kernel's output — the log it was meant to be separate from.
    // 0x3E8 is a port the firmware never looks at.
    //
    // COM4 @ 0x2E8, likewise off the firmware's list, carries what programs
    // write to their error stream, so failures do not have to be dug out of
    // everything else they printed.
    internal static unsafe class Serial
    {
        private const ushort Com1 = 0x3F8;
        private const ushort Com3 = 0x3E8;
        private const ushort Com4 = 0x2E8;

        // Register offsets from the port base.
        private const ushort Data       = 0; // RBR/THR (DLAB=0) | DLL (DLAB=1)
        private const ushort IntEnable  = 1; // IER     (DLAB=0) | DLM (DLAB=1)
        private const ushort FifoCtrl   = 2; // FCR (write) / IIR (read)
        private const ushort LineCtrl   = 3; // LCR  (bit7 = DLAB)
        private const ushort ModemCtrl  = 4; // MCR
        private const ushort LineStatus = 5; // LSR  (bit5 = THR empty, bit0 = data ready)

        private static bool s_ready;
        private static bool s_present;
        private static bool s_com3Ready;
        private static bool s_com3Present;
        private static bool s_com4Ready;
        private static bool s_com4Present;

        // Whether the last character sent to the port was '\r' (see PutChar).
        private static bool s_com1AfterCr;
        private static bool s_com3AfterCr;
        private static bool s_com4AfterCr;

        public static bool IsReady => s_ready;
        public static bool IsPresent => s_present;

        /// <summary>A 16550 at COM3 answered its loopback test.</summary>
        public static bool Com3Present => s_com3Present;

        /// <summary>A 16550 at COM4 answered its loopback test.</summary>
        public static bool Com4Present => s_com4Present;

        // Initialize COM1 and run a loopback self-test. Returns true if a
        // 16550 actually answered (false on QEMU `-serial none` / no chip;
        // callers then keep using the UEFI mirror while it's available).
        public static bool Init()
        {
            if (s_ready) return s_present;
            s_present = InitPort(Com1);
            s_ready = true;
            return s_present;
        }

        /// <summary>Initializes COM3. Returns whether one is there.</summary>
        /// <remarks>
        /// Unlike COM1, an absent COM3 is the usual case — a laptop has none,
        /// VirtualBox and QEMU only when configured with the port — so
        /// nothing is ever written to it unless the loopback test passed.
        /// Without that, every byte would still cost a status read that
        /// answers 0xFF from an empty bus. The same holds for COM4.
        /// </remarks>
        public static bool InitCom3()
        {
            if (s_com3Ready) return s_com3Present;
            s_com3Present = InitPort(Com3);
            s_com3Ready = true;
            return s_com3Present;
        }

        /// <summary>Initializes COM4. Returns whether one is there.</summary>
        public static bool InitCom4()
        {
            if (s_com4Ready) return s_com4Present;
            s_com4Present = InitPort(Com4);
            s_com4Ready = true;
            return s_com4Present;
        }

        private static bool InitPort(ushort port)
        {
            PortIo.Out8((ushort)(port + IntEnable), 0x00);   // disable all UART interrupts (polled)
            PortIo.Out8((ushort)(port + LineCtrl), 0x80);    // DLAB = 1 (access divisor latch)
            PortIo.Out8((ushort)(port + Data), 0x01);        // divisor low  = 1  → 115200 baud
            PortIo.Out8((ushort)(port + IntEnable), 0x00);   // divisor high = 0
            PortIo.Out8((ushort)(port + LineCtrl), 0x03);    // DLAB = 0, 8 bits, no parity, 1 stop
            PortIo.Out8((ushort)(port + FifoCtrl), 0xC7);    // FIFO enable + clear RX/TX, 14-byte trigger
            PortIo.Out8((ushort)(port + ModemCtrl), 0x0B);   // DTR | RTS | OUT2

            // Loopback self-test: MCR loopback on, send a byte, read back.
            PortIo.Out8((ushort)(port + ModemCtrl), 0x1E);   // loopback | DTR | RTS | OUT2
            PortIo.Out8((ushort)(port + Data), 0xAE);
            byte echo = PortIo.In8((ushort)(port + Data));

            // Back to normal operating mode regardless of test outcome.
            PortIo.Out8((ushort)(port + ModemCtrl), 0x0F);   // DTR | RTS | OUT1 | OUT2
            return echo == 0xAE;
        }

        // Polled transmit: spin until the THR (transmit holding register)
        // is empty, then push the byte. No interrupts, no buffering.
        public static void WriteByte(byte b)
        {
            if (!s_ready) return;
            PutByte(Com1, b);
        }

        private static void PutByte(ushort port, byte b)
        {
            // LSR bit 5 (0x20) = THR empty / ready to accept next byte.
            while ((PortIo.In8((ushort)(port + LineStatus)) & 0x20) == 0) { }
            PortIo.Out8((ushort)(port + Data), b);
        }

        public static void WriteChar(char c)
        {
            if (!s_ready) return;
            PutChar(Com1, c, ref s_com1AfterCr);
            OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.Com1Chars);
        }

        /// <summary>Writes one character to COM3; nothing if there is none.</summary>
        public static void WriteCharCom3(char c)
        {
            if (!s_com3Present) return;
            PutChar(Com3, c, ref s_com3AfterCr);
            OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.Com3Chars);
        }

        /// <summary>Writes one character to COM4; nothing if there is none.</summary>
        public static void WriteCharCom4(char c)
        {
            if (!s_com4Present) return;
            PutChar(Com4, c, ref s_com4AfterCr);
            OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.Com4Chars);
        }

        // '\n' -> CRLF so terminals / captured logs render correctly
        // (matches the CRLF the UEFI ConOut mirror produced) — unless the
        // '\r' is already there. Windows programs end lines with "\r\n", and
        // expanding their '\n' as well sent "\r\r\n": invisible on a terminal
        // and on COM1 (the host's pipeline re-splits lines), but a raw port
        // file (COM3) read as a blank line after every line of output.
        //
        // Non-ASCII codepoints are UTF-8 encoded rather than truncated to a
        // single garbage byte, so box/block glyphs (FETCH's banner) survive
        // the post-EBS serial path into any UTF-8 terminal / QEMU log. BMP
        // only — chars are UTF-16 code units (no surrogate pairing).
        private static void PutChar(ushort port, char c, ref bool afterCr)
        {
            if (c == '\n')
            {
                if (!afterCr) PutByte(port, (byte)'\r');
                PutByte(port, (byte)'\n');
                afterCr = false;
                return;
            }
            afterCr = c == '\r';

            uint cp = c;
            if (cp < 0x80)
            {
                PutByte(port, (byte)cp);
            }
            else if (cp < 0x800)
            {
                PutByte(port, (byte)(0xC0 | (cp >> 6)));
                PutByte(port, (byte)(0x80 | (cp & 0x3F)));
            }
            else
            {
                PutByte(port, (byte)(0xE0 | (cp >> 12)));
                PutByte(port, (byte)(0x80 | ((cp >> 6) & 0x3F)));
                PutByte(port, (byte)(0x80 | (cp & 0x3F)));
            }
        }

        public static void WriteString(string s)
        {
            if (s == null) return;
            for (int i = 0; i < s.Length; i++) WriteChar(s[i]);
        }
    }
}
