namespace OS.Hal
{
    // Which CP437 glyph a Unicode character is drawn with.
    //
    // The table next door is the classic IBM repertoire, indexed 0..255 — it
    // knows nothing about Unicode. Everything a text interface draws with is in
    // there, just under a different number: the box-drawing runes Terminal.Gui
    // uses live at 0xB3, 0xC4, 0xDA and friends.
    //
    // Only what is actually used is mapped. An unmapped character draws as '?',
    // which is honest — it says the glyph is missing rather than picking a
    // lookalike and quietly changing the text.
    internal static partial class FontCp437
    {
        /// <summary>
        /// The glyph index for a character, or -1 when nothing here can draw it.
        /// </summary>
        public static int Index(char ch)
        {
            // ASCII is at its own codepoint, which is the whole point of CP437.
            if (ch >= (char)0x20 && ch < (char)0x7F) return ch;

            switch (ch)
            {
                // Single-line box drawing.
                case (char)0x2500: return 0xC4;   // ─
                case (char)0x2502: return 0xB3;   // │
                case (char)0x250C: return 0xDA;   // ┌
                case (char)0x2510: return 0xBF;   // ┐
                case (char)0x2514: return 0xC0;   // └
                case (char)0x2518: return 0xD9;   // ┘
                case (char)0x251C: return 0xC3;   // ├
                case (char)0x2524: return 0xB4;   // ┤
                case (char)0x252C: return 0xC2;   // ┬
                case (char)0x2534: return 0xC1;   // ┴
                case (char)0x253C: return 0xC5;   // ┼

                // Double-line, which the library uses for a focused frame.
                case (char)0x2550: return 0xCD;   // ═
                case (char)0x2551: return 0xBA;   // ║
                case (char)0x2554: return 0xC9;   // ╔
                case (char)0x2557: return 0xBB;   // ╗
                case (char)0x255A: return 0xC8;   // ╚
                case (char)0x255D: return 0xBC;   // ╝
                case (char)0x2560: return 0xCC;   // ╠
                case (char)0x2563: return 0xB9;   // ╣
                case (char)0x2566: return 0xCB;   // ╦
                case (char)0x2569: return 0xCA;   // ╩
                case (char)0x256C: return 0xCE;   // ╬

                // Blocks and shades — scrollbars and the stipple behind a
                // dialog are drawn with these.
                case (char)0x2580: return 0xDF;   // ▀
                case (char)0x2584: return 0xDC;   // ▄
                case (char)0x2588: return 0xDB;   // █
                case (char)0x258C: return 0xDD;   // ▌
                case (char)0x2590: return 0xDE;   // ▐
                case (char)0x2591: return 0xB0;   // ░
                case (char)0x2592: return 0xB1;   // ▒
                case (char)0x2593: return 0xB2;   // ▓

                // Arrows and marks, for scrollbars and list selection.
                case (char)0x25B2: return 0x1E;   // ▲
                case (char)0x25BA: return 0x10;   // ►
                case (char)0x25BC: return 0x1F;   // ▼
                case (char)0x25C4: return 0x11;   // ◄
                case (char)0x25C6: return 0x04;   // ◆ (CP437 diamond)
                case (char)0x2022: return 0x07;   // •
                case (char)0x221A: return 0xFB;   // √ (the library's check mark)

                // Punctuation a title bar is likely to carry.
                case (char)0x00B7: return 0xFA;   // ·
                case (char)0x2014: return 0xC4;   // — drawn as a rule
                case (char)0x2013: return 0xC4;   // –
                case (char)0x00AB: return 0xAE;   // «
                case (char)0x00BB: return 0xAF;   // »

                default: return -1;
            }
        }

        /// <summary>True when this character has a glyph of its own.</summary>
        public static bool CanDraw(char ch) => Index(ch) >= 0;
    }
}
