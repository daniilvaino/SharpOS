namespace OS.Hal
{
    // Phase B#3 sub-step 1 — own i8042/PS-2 keyboard, independent of the
    // UEFI SimpleTextInput path (the Phase-B post-EBS off-ramp analogue
    // of the own 16550 UART vs the UEFI ConOut mirror).
    //
    // Deliberately NON-DESTRUCTIVE while UEFI is still alive: we only
    // read STATUS (0x64) and drain DATA (0x60) — no controller self-test
    // / reset / config-byte rewrite, because UEFI's own 8042 driver
    // still feeds the launcher and a 0xAA/0x60 sequence would disturb
    // shared controller state (targeted, not sledgehammer). Full
    // controller ownership (self-test, enable, IRQ1) belongs to the
    // post-EBS take-over (Phase C) / the interactive shell.
    //
    // Scancode set 1 (QEMU's i8042 runs translation on, emitting set-1
    // make/break: break = make | 0x80, 0xE0 = extended prefix). The
    // decoder is a pure function over (scancode, modifier state) — unit
    // testable headless with a synthetic script, no hardware needed.
    internal static class Ps2Keyboard
    {
        private const ushort Data = 0x60;
        private const ushort Status = 0x64;

        private const byte StsOutputFull = 0x01;  // OBF — byte waiting in 0x60
        private const byte StsAuxData = 0x20;      // data is from the mouse

        // Modifier latch (value-typed statics — no cctor trap).
        private static bool s_shift;
        private static bool s_caps;
        private static bool s_extended;

        public static byte ReadStatus() => PortIo.In8(Status);

        // True iff an 8042 plausibly responds (QEMU/real: not the
        // all-ones float of an absent port).
        public static bool IsPresent() => PortIo.In8(Status) != 0xFF;

        // Non-blocking single-byte read of a *keyboard* scancode. Skips
        // mouse (AUX) bytes. Returns false when nothing is pending.
        public static bool TryReadScancode(out byte scancode)
        {
            scancode = 0;
            byte sts = PortIo.In8(Status);
            if ((sts & StsOutputFull) == 0) return false;
            byte b = PortIo.In8(Data);
            if ((sts & StsAuxData) != 0) return false;   // mouse byte — drop
            scancode = b;
            return true;
        }

        // Drain up to `max` pending keyboard bytes, decoding each through
        // the pure decoder. Returns the count drained (0 = idle).
        public static int Poll(int max)
        {
            int n = 0;
            while (n < max && TryReadScancode(out byte sc))
            {
                Decode(sc, out _, out _);
                n++;
            }
            return n;
        }

        public enum KeyKind { None, Char, Enter, Backspace, Escape, Control }

        // Pure: feed one set-1 scancode + current modifier latch, get a
        // classified key. Make codes only produce output; break codes
        // (>=0x80) only update modifier state. 0xE0 sets the extended
        // latch for the next byte (extended keys are classed Control
        // here — the line editor only needs Char/Enter/Backspace/Esc).
        public static KeyKind Decode(byte sc, out char ch, out byte make)
        {
            ch = '\0';
            make = (byte)(sc & 0x7F);
            bool isBreak = (sc & 0x80) != 0;

            if (sc == 0xE0) { s_extended = true; return KeyKind.None; }
            bool ext = s_extended;
            s_extended = false;

            // Modifier make/break (left+right shift = 0x2A/0x36).
            if (make == 0x2A || make == 0x36) { s_shift = !isBreak; return KeyKind.Control; }
            if (make == 0x3A && !isBreak) { s_caps = !s_caps; return KeyKind.Control; }
            if (isBreak) return KeyKind.None;          // ignore other releases
            if (ext) return KeyKind.Control;           // arrows/etc — not needed yet

            if (make == 0x1C) return KeyKind.Enter;
            if (make == 0x0E) return KeyKind.Backspace;
            if (make == 0x01) return KeyKind.Escape;

            if (make >= Set1Normal.Length) return KeyKind.Control;
            byte baseCh = Set1Normal[make];
            if (baseCh == 0) return KeyKind.Control;

            bool upper = s_shift ^ (s_caps && baseCh >= (byte)'a' && baseCh <= (byte)'z');
            byte outCh = upper ? Set1Shift[make] : baseCh;
            if (outCh == 0) outCh = baseCh;
            ch = (char)outCh;
            return KeyKind.Char;
        }

        public static void ResetState() { s_shift = false; s_caps = false; s_extended = false; }

        // Set-1 make-code -> ASCII, index = scancode (0x00..0x39). 0 =
        // not a printable key. static readonly byte[] is safe here
        // (Phase 4, post GcStaticsMaterializer — same as Font8x8/
        // CoreClrProbe). Shift table = the US-QWERTY shifted glyphs.
        private static readonly byte[] Set1Normal = new byte[0x40]
        {
            0,    0,    (byte)'1',(byte)'2',(byte)'3',(byte)'4',(byte)'5',(byte)'6', // 00-07
            (byte)'7',(byte)'8',(byte)'9',(byte)'0',(byte)'-',(byte)'=',0,    0,     // 08-0F (0E=bksp,0F=tab)
            (byte)'q',(byte)'w',(byte)'e',(byte)'r',(byte)'t',(byte)'y',(byte)'u',(byte)'i', // 10-17
            (byte)'o',(byte)'p',(byte)'[',(byte)']',0,    0,    (byte)'a',(byte)'s', // 18-1F (1C=enter,1D=ctrl)
            (byte)'d',(byte)'f',(byte)'g',(byte)'h',(byte)'j',(byte)'k',(byte)'l',(byte)';', // 20-27
            (byte)'\'',(byte)'`',0,   (byte)'\\',(byte)'z',(byte)'x',(byte)'c',(byte)'v', // 28-2F (2A=lshift)
            (byte)'b',(byte)'n',(byte)'m',(byte)',',(byte)'.',(byte)'/',0,   (byte)'*', // 30-37 (36=rshift)
            0,    (byte)' ',0,    0,    0,    0,    0,    0,                       // 38-3F (39=space)
        };

        private static readonly byte[] Set1Shift = new byte[0x40]
        {
            0,    0,    (byte)'!',(byte)'@',(byte)'#',(byte)'$',(byte)'%',(byte)'^', // 00-07
            (byte)'&',(byte)'*',(byte)'(',(byte)')',(byte)'_',(byte)'+',0,    0,     // 08-0F
            (byte)'Q',(byte)'W',(byte)'E',(byte)'R',(byte)'T',(byte)'Y',(byte)'U',(byte)'I', // 10-17
            (byte)'O',(byte)'P',(byte)'{',(byte)'}',0,    0,    (byte)'A',(byte)'S', // 18-1F
            (byte)'D',(byte)'F',(byte)'G',(byte)'H',(byte)'J',(byte)'K',(byte)'L',(byte)':', // 20-27
            (byte)'"',(byte)'~',0,    (byte)'|',(byte)'Z',(byte)'X',(byte)'C',(byte)'V', // 28-2F
            (byte)'B',(byte)'N',(byte)'M',(byte)'<',(byte)'>',(byte)'?',0,   (byte)'*', // 30-37
            0,    (byte)' ',0,    0,    0,    0,    0,    0,                       // 38-3F
        };
    }
}
