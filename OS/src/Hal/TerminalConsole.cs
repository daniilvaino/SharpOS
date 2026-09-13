using XtermSharp;

namespace OS.Hal
{
    // Kernel front-end for the vendored XtermSharp engine (vendor/XtermSharp).
    //
    // FbTty is the boot console: allocation-free, value-typed statics only, a
    // handful of SGR codes, and clear-on-overflow instead of scrolling. It has
    // to be that way, because it runs from Phase 0 where `new` does not work.
    //
    // This is the other end: once the runtime is up it hands every byte to a
    // real terminal emulator and paints the resulting cell grid. Requirements
    // are hard, not stylistic:
    //   - Phase 2, GC statics materialized. Color.DefaultAnsiColors is a
    //     static List<Color> with a class constructor and MatchColor reads it,
    //     so this cannot be constructed earlier (see
    //     docs/nativeaot-nostd-kernel-limits.md §1).
    //   - Phase 3, the GOP framebuffer identity-mapped — there is nowhere to
    //     draw before that.
    //
    // Serial output is deliberately NOT routed through here (see
    // Platform.WriteChar): if this front-end breaks, the UART log has to
    // survive to say so.
    //
    // Painting is the part that decides whether a boot log is watchable:
    //   - a shadow copy of what is on screen, so only cells that actually
    //     changed are blitted;
    //   - scrolling moves pixels with FbConsole.ScrollUp instead of redrawing
    //     every row (the engine marks the whole screen dirty on a scroll, and
    //     a boot log scrolls constantly);
    //   - FbConsole.DrawCharFast for the common scale-1, fully-inside glyph.
    internal static unsafe class TerminalConsole
    {
        private const int Margin = 8;
        // CP437 8x16, not the 8x8 boot font.
        //
        // Font8x8 carries ASCII plus six box glyphs, which is a boot log's
        // whole vocabulary and not an interface's: every corner, tee and
        // vertical rule of a window frame came out as "?". FontCp437 is the
        // classic IBM repertoire, so the glyphs exist rather than being drawn
        // by hand. Taller cells also halve the row count, which reads better
        // than 98 rows of 8-pixel text on a 1280x800 screen.
        private const int CellW = 8;
        private const int CellH = 16;

        // A kernel console has no way to scroll back yet, so scrollback is only
        // memory. Keep a little for a future pager, not the 1000-line default.
        private const int ScrollbackLines = 64;

        private static Terminal s_terminal;
        private static bool s_ready;
        private static bool s_rendering;              // re-entrancy guard
        // Set while a character is being fed to the engine. The engine
        // allocates as it scrolls, an allocation can grow the kernel heap, and
        // growing the heap logs a line — which came back into Putc on the same
        // thread, found the engine lock taken by itself and spun out its whole
        // bound before dropping each character. Four such lines cost a
        // NativeAOT app printing 400 lines 108 spins and about a third of its
        // output time (step169). A line written from inside the engine still
        // reaches every other sink; it cannot reach this one either way.
        // Global rather than per thread: Putc runs with preemption suppressed
        // (Platform.WriteChar), so while one thread feeds no other can get here.
        private static bool s_feeding;
        // Feeding and painting both mutate engine state that is shared across
        // threads — the parser's ReadingBuffer holds a raw pointer that only
        // stays valid for the duration of one Feed. Two writers at once made it
        // read through a null buffer. Feeding waits for it (bounded), painting
        // does not: a skipped repaint is invisible, a skipped character is not.
        private static int s_engineLock;
        // Set when text has been fed, cleared once it is on screen. Read without
        // the lock so an idle reader can skip Flush entirely instead of taking
        // the lock on every poll and starving the thread doing the writing.
        private static volatile bool s_dirty;
        private static byte[] s_encode;               // reused UTF-8 scratch
        private static byte s_bgR, s_bgG, s_bgB;

        // Shadow of the painted grid: glyph + resolved colours per cell.
        private static char[] s_shadowChar;
        private static uint[] s_shadowFg;
        private static uint[] s_shadowBg;
        private static bool[] s_shadowValid;

        private static int s_cols;
        private static int s_rows;
        private static int s_lastYBase;

        // Where the block cursor is currently painted, -1 when it is not on screen.
        private static int s_cursorSlot = -1;

        public static bool IsReady => s_ready;

        public static Terminal Engine => s_terminal;

        /// <summary>
        /// Builds the engine sized to the framebuffer. Returns false when the FB is
        /// unavailable (headless) — the caller keeps using FbTty.
        /// </summary>
        public static bool TryInit(byte br, byte bg, byte bb)
        {
            if (s_ready) return true;
            if (!Framebuffer.IsAvailable) return false;

            int cols = ((int)Framebuffer.Width - 2 * Margin) / CellW;
            int rows = ((int)Framebuffer.Height - 2 * Margin) / CellH;
            if (cols < 8 || rows < 4) return false;

            // ConvertEol on: the kernel's console emits a bare LF for a line break and
            // drops CR entirely (see FbTty.Putc), i.e. it behaves like a TTY with ONLCR.
            // Without this the carriage never returns and every line starts one column
            // further right than the last.
            var options = new TerminalOptions
            {
                Cols = cols,
                Rows = rows,
                ConvertEol = true,
                Scrollback = ScrollbackLines
            };

            s_terminal = new Terminal(null, options);
            s_encode = new byte[4];
            s_cols = cols;
            s_rows = rows;
            s_bgR = br; s_bgG = bg; s_bgB = bb;

            int cells = cols * rows;
            s_shadowChar = new char[cells];
            s_shadowFg = new uint[cells];
            s_shadowBg = new uint[cells];
            s_shadowValid = new bool[cells];
            s_lastYBase = s_terminal.Buffer.YBase;

            FbConsole.Clear(br, bg, bb);
            s_ready = true;
            return true;
        }

        /// <summary>
        /// True while the alternate screen buffer is active — that is, while
        /// something is drawing a full-screen interface rather than printing
        /// lines.
        /// </summary>
        public static bool IsAlternateScreen
            => s_ready && s_terminal != null && s_terminal.Buffers.IsAlternateBuffer;

        /// <summary>Feeds one character; nothing is drawn until Flush.</summary>
        public static void Putc(char ch)
        {
            if (!s_ready || s_rendering || s_feeding) return;
            // Text must not be dropped: a lost character desynchronises whatever
            // wrote it from what it later reads back, and PSReadLine answers that
            // by redrawing forever. Painting may be skipped, feeding may not.
            ulong waitStarted = OS.Kernel.Diagnostics.PerfCounters.SinkClock();
            if (!Enter())
            {
                OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.TerminalDrops);
                return;
            }
            OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.TerminalLockTsc, waitStarted);

            int length = EncodeUtf8(ch, s_encode);
            ulong feedStarted = OS.Kernel.Diagnostics.PerfCounters.SinkClock();
            s_feeding = true;
            s_terminal.Feed(s_encode, length);
            s_feeding = false;
            OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.TerminalFeedTsc, feedStarted);
            s_dirty = true;

            // Callers that go through Platform.WriteChar one character at a time (Log's
            // Begin/EndLine pair, for instance) never reach Platform.Write's flush, so a
            // line break paints what is pending — unless a paint went out moments ago;
            // see CoalescePaints.
            if (ch == '\n' && PaintDue())
                Paint();

            Exit();
        }

        // Paints at most this often from line breaks while a pump is there to
        // finish the job. Faster than a screen refreshes; slow enough that a
        // program printing lines scrolls once per batch, not once per line.
        private const ulong LineBreakPaintsPerSecond = 60;

        private static bool s_coalesce;
        private static ulong s_lastPaint;

        /// <summary>
        /// Lets line breaks skip painting when the screen was painted less than a
        /// frame ago; the caller promises to call <see cref="Flush"/> regularly.
        /// </summary>
        /// <remarks>
        /// Every line break used to paint, and a paint that follows new lines at
        /// the bottom moves the whole screen up. A program printing 200 lines
        /// paid for 200 full-screen moves — half a millisecond each under QEMU,
        /// two thirds of what its output cost (step169). Batched, ten lines are
        /// one move of ten rows, which costs the same as a move of one.
        ///
        /// Only line breaks and the end of an application's write that ends a
        /// line (<see cref="FlushWhenDue"/>) are deferred. An explicit Flush —
        /// the end of a kernel string or of any other application write, a
        /// wait for input — still paints at once, so a panic, a frame or an
        /// echoed key is never late. Until someone promises to
        /// flush, nothing changes: early boot has no pump, and a deferred line
        /// there would sit unpainted.
        /// </remarks>
        public static void CoalescePaints() => s_coalesce = true;

        /// <summary>
        /// Paints now, unless the screen was painted less than a frame ago and
        /// a pump will get to it within one interval.
        /// </summary>
        /// <remarks>
        /// For the end of an application's write that ends a line. A paint at
        /// the end of every write turned each line a NativeAOT app printed into
        /// its own full-screen move: BenchAot's 200 lines cost 283 paints and
        /// 1.4 ms a line, against 21 paints for the same work on the hosted
        /// runtime, whose writes never forced a paint (step169).
        ///
        /// Lines only. A full-screen interface's writes keep painting at once:
        /// deferring them showed the first piece of each redrawn frame alone,
        /// and a launcher that redraws unchanged frames flickered.
        /// </remarks>
        public static void FlushWhenDue()
        {
            if (!PaintDue())
                return;
            Flush();
        }

        private static bool PaintDue()
        {
            if (!s_coalesce)
                return true;

            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            if (hz == 0)
                return true;

            return OS.Hal.Timer.Hpet.ReadCounter() - s_lastPaint >= hz / LineBreakPaintsPerSecond;
        }

        private static bool TryEnter()
        {
            return System.Threading.Interlocked.CompareExchange(ref s_engineLock, 1, 0) == 0;
        }

        // Bounded spin, then give up. Never blocks outright: this runs on the
        // logging path and inside the fault handler, where waiting on a lock the
        // faulting thread already holds would turn a crash dump into a hang.
        // The bound is small because nothing inside the critical section yields,
        // so a holder always finishes within its own slice; the spin only covers
        // a preemption landing mid-section.
        private static bool Enter()
        {
            for (int spin = 0; spin < 1 << 12; spin++)
            {
                if (TryEnter()) return true;
                System.Threading.Interlocked.MemoryBarrier();
            }
            return false;
        }

        private static void Exit()
        {
            System.Threading.Interlocked.Exchange(ref s_engineLock, 0);
        }

        /// <summary>True when characters were fed but not yet painted.</summary>
        public static bool HasPendingOutput => s_dirty;

        public static void Puts(string text)
        {
            if (!s_ready || text == null) return;
            for (int i = 0; i < text.Length; i++)
                Putc(text[i]);
        }

        /// <summary>
        /// Paints what changed since the last call. Scrolling is handled first, as a
        /// pixel move, so the rows that merely shifted are not re-rendered.
        /// </summary>
        public static void Flush()
        {
            if (!s_ready || s_rendering) return;
            if (!TryEnter()) return;
            Paint();
            Exit();
        }

        // Callers hold the engine lock.
        private static void Paint()
        {
            if (s_rendering) return;

            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            s_rendering = true;
            s_dirty = false;

            var buffer = s_terminal.Buffer;
            ApplyScroll(buffer);

            s_terminal.GetUpdateRange(out int startY, out int endY);
            s_terminal.ClearUpdateRange();

            EraseCursor(buffer);

            if (startY <= endY)
            {
                if (startY < 0) startY = 0;
                if (endY >= s_rows) endY = s_rows - 1;
                for (int y = startY; y <= endY; y++)
                    DrawRow(buffer, y);
            }

            DrawCursor(buffer);

            s_rendering = false;
            s_lastPaint = OS.Hal.Timer.Hpet.ReadCounter();
            OS.Kernel.Diagnostics.PerfCounters.CountScreen(started);
        }

        /// <summary>Repaints every row — after a resize or a mode switch.</summary>
        public static void Redraw()
        {
            if (!s_ready || s_rendering) return;
            if (!TryEnter()) return;

            s_rendering = true;
            FbConsole.Clear(s_bgR, s_bgG, s_bgB);
            for (int i = 0; i < s_shadowValid.Length; i++)
                s_shadowValid[i] = false;

            var buffer = s_terminal.Buffer;
            for (int y = 0; y < s_rows; y++)
                DrawRow(buffer, y);

            s_lastYBase = buffer.YBase;
            s_rendering = false;
            s_terminal.ClearUpdateRange();
            Exit();
        }

        // The engine does not announce scrolls, but YBase counts the lines that left
        // the top of the screen. Moving those pixels beats re-rendering every glyph:
        // a scrolled screen has every row marked dirty even though only the last one
        // holds new text.
        // Decided from the boot-time measurement rather than assumed: the same
        // code ran at 622 MiB/s on one machine and 4 MiB/s on another, purely
        // because of how their firmware marks video memory.
        private static bool ReadingScreenIsTooSlow()
        {
            ulong scroll = OS.Kernel.Diagnostics.FbPerfProbe.ScrollMibPerSecond;
            if (scroll == 0) return false;      // never measured — keep the old path
            return scroll < 100;
        }

        private static void ApplyScroll(XtermSharp.Buffer buffer)
        {
            int delta = buffer.YBase - s_lastYBase;
            s_lastYBase = buffer.YBase;

            if (delta <= 0) return;
            // Pixels move, so a cursor painted before the scroll is now somewhere else;
            // forget it rather than erasing at a stale position.
            s_cursorSlot = -1;

            if (delta >= s_rows || ReadingScreenIsTooSlow())
            {
                // Repaint every cell instead of moving pixels.
                //
                // Moving pixels reads the screen back, and on a machine whose
                // firmware marks the framebuffer uncacheable those reads run at
                // a few MiB/s — a single scrolled line took seconds. Redrawing
                // touches far fewer bytes (glyphs, not the whole screen) and
                // never reads, so it wins outright there and is no worse
                // anywhere else.
                for (int i = 0; i < s_shadowValid.Length; i++)
                    s_shadowValid[i] = false;
                return;
            }

            FbConsole.ScrollUp(Margin, Margin, s_cols * CellW, s_rows * CellH, delta * CellH);

            int shift = delta * s_cols;
            int total = s_cols * s_rows;
            for (int i = 0; i + shift < total; i++)
            {
                s_shadowChar[i] = s_shadowChar[i + shift];
                s_shadowFg[i] = s_shadowFg[i + shift];
                s_shadowBg[i] = s_shadowBg[i + shift];
                s_shadowValid[i] = s_shadowValid[i + shift];
            }
            for (int i = total - shift; i < total; i++)
                s_shadowValid[i] = false;
        }

        // The cursor is drawn as an inverted cell rather than tracked as terminal
        // state: erase restores the cell from the shadow, so no extra bookkeeping is
        // needed and a repaint of that row simply overwrites it.
        /// <summary>
        /// The CP437 glyph for a character, or '?' when the font has none.
        /// </summary>
        /// <remarks>
        /// A question mark is the honest answer for a missing glyph: it says
        /// the character could not be drawn, where a lookalike would quietly
        /// change what the screen says.
        /// </remarks>
        private static int GlyphOf(char ch)
        {
            int glyph = FontCp437.Index(ch);
            return glyph >= 0 ? glyph : '?';
        }

        private static void EraseCursor(XtermSharp.Buffer buffer)
        {
            if (s_cursorSlot < 0) return;

            int y = s_cursorSlot / s_cols;
            int x = s_cursorSlot % s_cols;
            if (s_shadowValid[s_cursorSlot])
                FbConsole.DrawCellFast(Margin + x * CellW, Margin + y * CellH,
                    GlyphOf(s_shadowChar[s_cursorSlot]),
                    s_shadowFg[s_cursorSlot], s_shadowBg[s_cursorSlot]);
            s_cursorSlot = -1;
        }

        private static void DrawCursor(XtermSharp.Buffer buffer)
        {
            // DECTCEM: the engine tracks ESC[?25l / ESC[?25h, we just honour it.
            // Full-screen apps (the launcher menu, anything drawing its own UI)
            // turn the cursor off rather than leave a block parked after their
            // last write.
            if (s_terminal.CursorHidden) return;

            int x = buffer.X;
            int y = buffer.YBase + buffer.Y - buffer.YDisp;
            if (x < 0 || x >= s_cols || y < 0 || y >= s_rows) return;

            int slot = y * s_cols + x;
            char glyph = s_shadowValid[slot] ? s_shadowChar[slot] : ' ';
            uint fg = s_shadowValid[slot] ? s_shadowFg[slot] : FbConsole.Pack(200, 200, 200);
            uint bg = s_shadowValid[slot] ? s_shadowBg[slot] : FbConsole.Pack(s_bgR, s_bgG, s_bgB);

            FbConsole.DrawCellFast(Margin + x * CellW, Margin + y * CellH, GlyphOf(glyph), bg, fg);
            s_cursorSlot = slot;
        }

        private static void DrawRow(XtermSharp.Buffer buffer, int y)
        {
            int index = buffer.YDisp + y;
            if (index < 0 || index >= buffer.Lines.Length) return;

            var line = buffer.Lines[index];
            int py = Margin + y * CellH;
            int rowBase = y * s_cols;

            for (int x = 0; x < s_cols; x++)
            {
                var cell = x < line.Length ? line[x] : CharData.Null;

                // The trailing half of a wide glyph carries width 0 and no code of its
                // own; the leading half already painted both columns' worth.
                if (cell.Width == 0) continue;

                char glyph = cell.Code == 0 ? ' ' : (char)(cell.Code > 0xFFFF ? '?' : cell.Code);

                // Attribute packing (CharData): flags << 18 | fg << 9 | bg, where fg/bg
                // are 256-colour palette indices (256/257 = default fg/bg).
                int attribute = cell.Attribute;
                uint fg = PaletteColor((attribute >> 9) & 0x1ff, isForeground: true);
                uint bg = PaletteColor(attribute & 0x1ff, isForeground: false);

                // SGR 7 is a flag, not a colour: the renderer is what swaps them.
                if ((((FLAGS)(attribute >> 18)) & FLAGS.INVERSE) != 0)
                {
                    uint swap = fg;
                    fg = bg;
                    bg = swap;
                }

                int slot = rowBase + x;
                if (s_shadowValid[slot]
                    && s_shadowChar[slot] == glyph
                    && s_shadowFg[slot] == fg
                    && s_shadowBg[slot] == bg)
                    continue;

                FbConsole.DrawCellFast(Margin + x * CellW, py, GlyphOf(glyph), fg, bg);

                s_shadowChar[slot] = glyph;
                s_shadowFg[slot] = fg;
                s_shadowBg[slot] = bg;
                s_shadowValid[slot] = true;
            }
        }

        private static uint PaletteColor(int index, bool isForeground)
        {
            // 256 = default foreground, 257 = inverted default. Anything else indexes
            // the engine's palette, which is only materialized after Phase 2.
            if (index >= 256)
                return isForeground ? FbConsole.Pack(200, 200, 200) : FbConsole.Pack(s_bgR, s_bgG, s_bgB);

            var palette = Color.DefaultAnsiColors;
            if (palette == null || index < 0 || index >= palette.Count)
                return isForeground ? FbConsole.Pack(200, 200, 200) : FbConsole.Pack(s_bgR, s_bgG, s_bgB);

            var color = palette[index];
            return FbConsole.Pack(color.Red, color.Green, color.Blue);
        }

        private static int EncodeUtf8(char ch, byte[] destination)
        {
            uint value = ch;
            if (value < 0x80)
            {
                destination[0] = (byte)value;
                return 1;
            }
            if (value < 0x800)
            {
                destination[0] = (byte)(0xC0 | (value >> 6));
                destination[1] = (byte)(0x80 | (value & 0x3F));
                return 2;
            }
            destination[0] = (byte)(0xE0 | (value >> 12));
            destination[1] = (byte)(0x80 | ((value >> 6) & 0x3F));
            destination[2] = (byte)(0x80 | (value & 0x3F));
            return 3;
        }
    }
}
