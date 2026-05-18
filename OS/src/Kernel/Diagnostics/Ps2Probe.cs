using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase B#3 sub-step 1 — own PS/2 keyboard bring-up oracle.
    //
    // Headless QEMU has no key input, so the regression oracle is the
    // PURE set-1 decoder run over a fixed synthetic scancode script
    // (zero hardware interaction, fully deterministic). Plus a single
    // non-destructive read of the real i8042 STATUS port for presence
    // (we do NOT issue controller commands — UEFI still owns the 8042
    // for the launcher). Interactive real-key proof comes with the
    // shell sub-step under SHARPOS_GUI=1.
    internal static unsafe class Ps2Probe
    {
        public static void Run()
        {
            byte status = Ps2Keyboard.ReadStatus();
            bool present = Ps2Keyboard.IsPresent();

            // Synthetic set-1 script: type a, Shift+b, 1, Shift+1,
            // Enter, Backspace, Esc. Expected Char stream "aB1!" with
            // the three control keys classified. Hand-traced; exact
            // equality is the oracle (no precomputed hash needed).
            byte* script = stackalloc byte[]
            {
                0x1E,             // 'a'
                0x2A, 0x30, 0xAA, // LShift, 'b'->'B', LShift release
                0x02,             // '1'
                0x2A, 0x02, 0xAA, // LShift, '1'->'!', LShift release
                0x1C,             // Enter
                0x0E,             // Backspace
                0x01,             // Esc
            };
            const int scriptLen = 11;

            Ps2Keyboard.ResetState();
            char* got = stackalloc char[8];
            int gi = 0;
            bool sawEnter = false, sawBksp = false, sawEsc = false;

            for (int i = 0; i < scriptLen; i++)
            {
                Ps2Keyboard.KeyKind k = Ps2Keyboard.Decode(script[i], out char ch, out _);
                if (k == Ps2Keyboard.KeyKind.Char && gi < 8) got[gi++] = ch;
                else if (k == Ps2Keyboard.KeyKind.Enter) sawEnter = true;
                else if (k == Ps2Keyboard.KeyKind.Backspace) sawBksp = true;
                else if (k == Ps2Keyboard.KeyKind.Escape) sawEsc = true;
            }
            Ps2Keyboard.ResetState();

            bool textOk = gi == 4 && got[0] == 'a' && got[1] == 'B'
                                  && got[2] == '1' && got[3] == '!';
            bool pass = textOk && sawEnter && sawBksp && sawEsc;

            Console.Write("[ps2] status=0x");
            Console.WriteHex(status);
            Console.Write(present ? " present=Y decode=\"" : " present=N decode=\"");
            for (int i = 0; i < gi; i++) Console.WriteChar(got[i]);
            Console.Write("\" ent=");
            Console.WriteInt(sawEnter ? 1 : 0);
            Console.Write(" bks=");
            Console.WriteInt(sawBksp ? 1 : 0);
            Console.Write(" esc=");
            Console.WriteInt(sawEsc ? 1 : 0);
            Console.WriteLine(pass ? " PASS" : " FAIL");
        }
    }
}
