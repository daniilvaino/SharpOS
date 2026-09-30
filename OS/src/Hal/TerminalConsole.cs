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

        // The damage range the last paint consumed, and the paints it could not
        // start because the engine lock was held. A frame that reached the
        // engine and never reached the screen is one or the other: a range that
        // did not cover it, or a paint that never ran. Read by the idle report,
        // which fires while that frame is still the only thing on the screen.
        // No initialisers: one would give this class a static constructor, and
        // this class runs before the area GC statics live in is materialized
        // (limits §1). Before the first paint the range reads 0..0, which
        // paints= tells apart from a real one.
        private static int s_lastRangeStart;
        private static int s_lastRangeEnd;
        private static int s_paintSkips;
        private static int s_fed;
        private static int s_bailRendering;
        private static int s_bailFeeding;
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

        /// <summary>Is a full-screen program drawing right now?</summary>
        /// <remarks>
        /// The alternate buffer is the question. A program that switches to it
        /// (ESC [ ? 1049 h - every curses-shaped interface does, ours included)
        /// is saying it owns the display until it switches back, and a line the
        /// kernel prints over that interface is not a message: the program's
        /// next repaint erases it, usually within a frame. It was read as
        /// flicker, which is the most it could ever be.
        ///
        /// Asked on every character written, so nothing here may allocate,
        /// write, or take a lock.
        /// </remarks>
        public static bool ProgramOwnsScreen
        {
            get
            {
                // s_ready first, and the order is the whole point. This is
                // asked on every character the kernel writes, including the
                // earliest boot output, and s_terminal is a GC static - the
                // area those live in is materialized late (limits section 1).
                // Reading it before that stopped the boot dead right after the
                // [uefi] lines, which go straight to ConOut and so were the
                // last thing anyone saw. s_ready is a plain bool: it lives in
                // the image and is safe to ask at any time.
                if (!s_ready) return false;

                var terminal = s_terminal;
                if (terminal == null) return false;

                var buffers = terminal.Buffers;
                return buffers != null && buffers.IsAlternateBuffer;
            }
        }

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
            InvalidatePaletteCache();      // the defaults are built from these

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
            if (!s_ready) return;
            if (s_rendering) { s_bailRendering++; return; }
            if (s_feeding) { s_bailFeeding++; return; }
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
            s_fed++;

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

        /// <summary>The paint state the idle report prints.</summary>
        public static void ReadPaintState(out int rangeStart, out int rangeEnd, out int skips,
                                          out int fed, out bool alternate,
                                          out int bailRendering, out int bailFeeding)
        {
            rangeStart = s_lastRangeStart;
            rangeEnd = s_lastRangeEnd;
            skips = s_paintSkips;
            fed = s_fed;
            alternate = IsAlternateScreen;
            bailRendering = s_bailRendering;
            bailFeeding = s_bailFeeding;
        }

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

            // Preemption off across the whole critical section, as it already is
            // for the paint Putc does — Putc runs inside Platform.WriteChar,
            // which suppresses, and this path had nothing.
            //
            // Enter is a bounded spin that gives up rather than wait, on the
            // stated assumption that a holder finishes within its own slice. A
            // paint that can be preempted breaks it, and the cost is not a
            // skipped paint but lost text: s_rendering stays set while another
            // thread runs, and every character that thread writes is dropped by
            // the guard at the top of Putc. The launcher's first frame went that
            // way whole — 16266 characters, none of them fed, while the paint
            // interrupted in its row phase belonged to another thread.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!TryEnter()) { s_paintSkips++; return; }
                Paint();
                Exit();
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        // Callers hold the engine lock.
        private static void Paint()
        {
            if (s_rendering) return;

            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            s_rendering = true;
            s_dirty = false;

            var buffer = s_terminal.Buffer;

            // The cursor is pixels, not a cell: erase it while it is still
            // where it was drawn. A scroll moves those pixels with the text,
            // and the shadow says the cell under them holds whatever it held
            // — so no row redraw ever replaces them. Erased after the scroll,
            // a stale block rode up the screen at the end (or the start) of
            // the line it had been parked on.
            EraseCursor(buffer);

            ulong phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            ApplyScroll(buffer);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenScrollTicks,
                (long)(OS.Kernel.Diagnostics.PerfCounters.Now() - phase));

            s_terminal.GetUpdateRange(out int startY, out int endY);
            s_lastRangeStart = startY;
            s_lastRangeEnd = endY;
            s_terminal.ClearUpdateRange();

            phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            long switches = OS.Kernel.Diagnostics.PerfCounters.Value(
                OS.Kernel.Diagnostics.PerfCounter.SchedSwitches);
            if (startY <= endY)
            {
                if (startY < 0) startY = 0;
                if (endY >= s_rows) endY = s_rows - 1;
                for (int y = startY; y <= endY; y++)
                    DrawRow(buffer, y);
            }
            long elapsed = (long)(OS.Kernel.Diagnostics.PerfCounters.Now() - phase);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenRowTicks, elapsed);

            // Counted separately when nothing else got the processor in the
            // meantime. This phase is wall clock inside a preempted thread, so
            // during the census — four stress threads, fifty thousand context
            // switches a second — it charges other threads' work to the paint.
            if (OS.Kernel.Diagnostics.PerfCounters.Value(
                    OS.Kernel.Diagnostics.PerfCounter.SchedSwitches) == switches)
            {
                OS.Kernel.Diagnostics.PerfCounters.Add(
                    OS.Kernel.Diagnostics.PerfCounter.ScreenRowCleanPaints, 1);
                OS.Kernel.Diagnostics.PerfCounters.Add(
                    OS.Kernel.Diagnostics.PerfCounter.ScreenRowCleanTicks, elapsed);
            }

            phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            DrawCursor(buffer);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCursorTicks,
                (long)(OS.Kernel.Diagnostics.PerfCounters.Now() - phase));

            s_rendering = false;
            s_lastPaint = OS.Hal.Timer.Hpet.ReadCounter();
            OS.Kernel.Diagnostics.PerfCounters.CountScreen(started);
        }

        /// <summary>Repaints every row — after a resize or a mode switch.</summary>
        public static void Redraw()
        {
            if (!s_ready || s_rendering) return;

            // Suppressed for the same reason as Flush: this holds the engine
            // lock across a full-screen repaint.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!TryEnter()) { s_paintSkips++; return; }

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
            finally { OS.Kernel.Threading.Preemption.Allow(); }
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
            // Paint erased the cursor before calling here; nothing drawn over
            // the grid is left to move with the pixels.
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
            // A cell the shadow does not know was blank when the cursor went
            // on it (DrawCursor paints a space there); put the blank back
            // rather than leave the block for a redraw that may never come.
            if (s_shadowValid[s_cursorSlot])
                FbConsole.DrawCellFast(Margin + x * CellW, Margin + y * CellH,
                    GlyphOf(s_shadowChar[s_cursorSlot]),
                    s_shadowFg[s_cursorSlot], s_shadowBg[s_cursorSlot]);
            else
                FbConsole.DrawCellFast(Margin + x * CellW, Margin + y * CellH, ' ',
                    FbConsole.Pack(200, 200, 200), FbConsole.Pack(s_bgR, s_bgG, s_bgB));
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

            // Tallied per row, not per cell: PerfCounters.Add is an interlocked
            // add, and a paint walks thirteen thousand cells.
            int drawn = 0, skipped = 0;

            // Hoisted: Length is a property over the line's array, and
            // CharData.Null is a static on a type that has a class constructor,
            // so reading it per cell pays a class-constructor check per cell.
            int lineLength = line.Length;
            var empty = CharData.Null;

            for (int x = 0; x < s_cols; x++)
            {
                var cell = x < lineLength ? line[x] : empty;

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
                {
                    skipped++;
                    continue;
                }

                drawn++;
                FbConsole.DrawCellFast(Margin + x * CellW, py, GlyphOf(glyph), fg, bg);

                s_shadowChar[slot] = glyph;
                s_shadowFg[slot] = fg;
                s_shadowBg[slot] = bg;
                s_shadowValid[slot] = true;
            }

            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCellsDrawn, drawn);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCellsSkipped, skipped);
        }

        // Every colour the grid can hold, worked out once.
        //
        // Called twice per cell, so twenty-seven thousand times a paint. What
        // makes that expensive is invisible here: Color is a class whose
        // palette lives in a static field behind a class constructor, so every
        // read of it carries a construction check, and every colour then costs
        // a list indexer and three field loads through a reference. Two hundred
        // and fifty-eight entries, done once, replace all of it with an array
        // read.
        //
        // Not claimed to be where the paint's time goes. At 1920x1080 a paint
        // spends 16.6 ms in the row loop and draws 3052 of the 13430 cells it
        // walks, which reads two ways — 1.3 us deciding per cell, or 5.5 us
        // writing per cell drawn — and the phase counters cannot separate them.
        // The `cell8x16` rate in [fbperf] is what settles it.
        private const int PaletteSlots = 258;          // 0..255 plus the two defaults

        // Allocated in BuildPaletteCache rather than by a field initializer, and
        // that is not a style choice. This class has no static constructor, so
        // every static it reads — the cell shadow, the column count — is a plain
        // load. One field initializer anywhere in the class would give it one,
        // and then each of those reads carries a class-constructor check: the
        // very cost being removed here, spread over the whole row loop instead.
        private static uint[] s_paletteFg;
        private static uint[] s_paletteBg;
        private static bool s_paletteCached;

        // The engine materializes its palette only after Phase 2, and the
        // background is settable, so the cache is built on demand and dropped
        // when either changes rather than assumed to be ready.
        private static void InvalidatePaletteCache() => s_paletteCached = false;

        private static void BuildPaletteCache()
        {
            if (s_paletteFg == null)
            {
                s_paletteFg = new uint[PaletteSlots];
                s_paletteBg = new uint[PaletteSlots];
            }

            var palette = Color.DefaultAnsiColors;

            for (int i = 0; i < PaletteSlots; i++)
            {
                s_paletteFg[i] = ComputePaletteColor(i, true, palette);
                s_paletteBg[i] = ComputePaletteColor(i, false, palette);
            }

            // Left uncached while the palette is still absent: caching the
            // fallback would freeze the screen into grey-on-background for the
            // rest of the boot.
            s_paletteCached = palette != null;
        }

        private static uint ComputePaletteColor(int index, bool isForeground,
                                                System.Collections.Generic.List<Color> palette)
        {
            // 256 = default foreground, 257 = inverted default. Anything else indexes
            // the engine's palette, which is only materialized after Phase 2.
            if (index >= 256 || palette == null || index < 0 || index >= palette.Count)
                return isForeground ? FbConsole.Pack(200, 200, 200) : FbConsole.Pack(s_bgR, s_bgG, s_bgB);

            var color = palette[index];
            return FbConsole.Pack(color.Red, color.Green, color.Blue);
        }

        private static uint PaletteColor(int index, bool isForeground)
        {
            if (!s_paletteCached) BuildPaletteCache();

            if ((uint)index < PaletteSlots)
                return isForeground ? s_paletteFg[index] : s_paletteBg[index];

            return isForeground ? FbConsole.Pack(200, 200, 200) : FbConsole.Pack(s_bgR, s_bgG, s_bgB);
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
