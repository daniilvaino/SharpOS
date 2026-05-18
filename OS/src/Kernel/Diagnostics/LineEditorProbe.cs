using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase B#3 sub-step 2 — line-editor oracle. Headless QEMU has no
    // keys, so drive the full PS/2 set-1 decoder + LineEditor with a
    // fixed synthetic scancode script and assert the committed buffer
    // exactly. Deterministic, zero hardware interaction. Real typing
    // proof is the interactive shell under SHARPOS_GUI=1.
    internal static unsafe class LineEditorProbe
    {
        public static void Run()
        {
            // Type "cat x", Backspace, Enter -> committed line "cat ".
            // set-1 make codes: c=2E a=1E t=14 space=39 x=2D
            //                   backspace=0E enter=1C
            byte* script = stackalloc byte[]
            {
                0x2E, 0x1E, 0x14, 0x39, 0x2D, // c a t _ x
                0x0E,                         // Backspace
                0x1C,                         // Enter
            };
            const int scriptLen = 7;

            Ps2Keyboard.ResetState();
            LineEditor.Reset();
            bool submitted = false;

            for (int i = 0; i < scriptLen; i++)
            {
                Ps2Keyboard.KeyKind k = Ps2Keyboard.Decode(script[i], out char ch, out _);
                LineEditor.Status st = LineEditor.Feed(k, ch);
                if (st == LineEditor.Status.Submitted) { submitted = true; break; }
            }
            Ps2Keyboard.ResetState();

            bool pass = submitted && LineEditor.Matches("cat ");

            Console.Write("[lined] len=");
            Console.WriteInt(LineEditor.Length);
            Console.Write(" buf=\"");
            char[] b = LineEditor.Buffer;
            for (int i = 0; i < LineEditor.Length; i++) Console.WriteChar(b[i]);
            Console.Write("\" sub=");
            Console.WriteInt(submitted ? 1 : 0);
            Console.WriteLine(pass ? " PASS" : " FAIL");

            LineEditor.Reset();
        }
    }
}
