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
    //
    // The screen holds panels, as tmux panes: each its own engine,
    // queue and shadow in a rectangle of the screen. The main one is the
    // terminal of the kernel and of programs; the side one, at the right, is
    // written by the shell (the object inspector). A program drawing on the
    // alternate screen gets the whole screen back while it does.
    /// <summary>The engine's answers to queries go to the terminal's input (step217).</summary>
    internal sealed class AnswersToInput : XtermSharp.SimpleTerminalDelegate
    {
        public override void Send(byte[] data) => OS.Kernel.Input.TerminalInput.Answer(data);
    }

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
        internal const int CellW = 8;
        internal const int CellH = 16;

        // What a panel keeps above its screen to scroll back to (Shift+PgUp).
        private const int ScrollbackLines = 1000;

        // The side panel's least width, and the main one's.
        private const int MinSideColumns = 10;
        private const int MinMainColumns = 20;

        private static TerminalPane s_main;
        private static TerminalPane s_side;
        private static Terminal s_terminal;           // s_main's engine
        private static bool s_ready;
        private static bool s_rendering;              // re-entrancy guard
        // Set while queued bytes are being fed to the engine. The engine
        // allocates as it scrolls, an allocation can grow the kernel heap, and
        // growing the heap logs a line — which came back on the same thread,
        // found the engine lock taken by itself and spun out its whole bound
        // (step169). Now the line is queued after the write being fed, and the
        // feeding loop takes it. Global rather than per thread: feeding runs
        // with preemption suppressed, so while one thread feeds no other can
        // get here.
        internal static bool s_feeding;
        // Feeding and painting both mutate engine state that is shared across
        // threads — the parser's ReadingBuffer holds a raw pointer that only
        // stays valid for the duration of one Feed. Two writers at once made it
        // read through a null buffer. Feeding waits for it (bounded), painting
        // does not: a skipped repaint is invisible, a skipped character is not.
        // One lock for all panels: they share the screen.
        private static int s_engineLock;
        // Set when text has been queued, cleared once it is on screen. Read without
        // the lock so an idle reader can skip Flush entirely instead of taking
        // the lock on every poll and starving the thread doing the writing.
        internal static volatile bool s_dirty;

        // The damage range the last paint consumed, and the paints it could not
        // start because the engine lock was held. A frame that reached the
        // engine and never reached the screen is one or the other: a range that
        // did not cover it, or a paint that never ran. Read by the idle report,
        // which fires while that frame is still the only thing on the screen.
        // No initialisers: one would give this class a static constructor, and
        // this class runs before the area GC statics live in is materialized
        // (limits §1). Before the first paint the range reads 0..0, which
        // paints= tells apart from a real one.
        internal static int s_lastRangeStart;
        internal static int s_lastRangeEnd;
        private static int s_paintSkips;
        internal static int s_fed;
        private static int s_bailRendering;
        private static int s_bailFeeding;
        internal static byte s_bgR, s_bgG, s_bgB;

        // The whole grid the screen holds, and how much of it the side panel
        // was asked for (0: none) and has (0: hidden, by request or because
        // the main panel is on the alternate screen).
        private static int s_screenCols;
        private static int s_screenRows;
        private static int s_sideAsked;
        private static int s_sideShown;
        // The panel the keys are for (Ctrl+Tab): 0 the main one, 1 the side one.
        private static int s_focus;

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

        /// <summary>The main panel's engine: the terminal of the kernel and of programs.</summary>
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

            s_screenCols = cols;
            s_screenRows = rows;
            s_bgR = br; s_bgG = bg; s_bgB = bb;
            InvalidatePaletteCache();      // the defaults are built from these

            // Its answers (cursor position, device attributes) are the
            // terminal's input, as on Unix: the reader of the terminal gets
            // them (step217, TerminalInput).
            s_main = new TerminalPane(new AnswersToInput(), cols, rows, Margin, Margin);
            s_terminal = s_main.Engine;

            FbConsole.Clear(br, bg, bb);
            s_ready = true;
            return true;
        }

        // The terminal options both panels are built with. ConvertEol on: the
        // kernel's console emits a bare LF for a line break and drops CR
        // entirely (see FbTty.Putc), i.e. it behaves like a TTY with ONLCR.
        // Without this the carriage never returns and every line starts one
        // column further right than the last.
        internal static TerminalOptions Options(int cols, int rows) => new TerminalOptions
        {
            Cols = cols,
            Rows = rows,
            ConvertEol = true,
            Scrollback = ScrollbackLines
        };

        /// <summary>
        /// True while the alternate screen buffer is active — that is, while
        /// something is drawing a full-screen interface rather than printing
        /// lines.
        /// </summary>
        public static bool IsAlternateScreen
            => s_ready && s_terminal != null && s_terminal.Buffers.IsAlternateBuffer;

        // The pipe to the screen (step218). Writers queue bytes; the engine
        // gets them in one go (Feed), and the screen is painted when the
        // queue is empty (Flush) — by whoever asks: the input pump every
        // interval, a reader waiting for a key, the end of a kernel string.
        //
        // It replaced four rules for when to paint, each written for one
        // symptom (paint at the end of a write; except one ending a line,
        // step169; not "when due" for everything; not "all but the first
        // write of a burst"). Painting at the end of every write put each
        // 4 KiB piece of a launcher frame on screen alone, the cursor drawn
        // wherever that piece's last move had left it — the black block
        // flickering about the screen. Now nothing is painted while there is
        // more to feed, and a program printing lines is painted once per
        // pump interval, not once per line.
        //
        // One writer at a time, as before: Platform.WriteChar suppresses
        // preemption and turns away a nested call, so the queue needs no lock
        // on the writing side. The reading side moves only under the engine
        // lock.

        /// <summary>Queues one character for the screen.</summary>
        public static void Putc(char ch)
        {
            if (!s_ready) return;

            byte* encoded = stackalloc byte[4];
            int length = EncodeUtf8(ch, encoded);
            s_main.Enqueue(encoded, length);

            // Before the pump nobody paints at intervals, and Log writes its
            // lines a character at a time, never reaching Platform.Write's
            // flush: a line break paints.
            if (ch == '\n' && !s_pumped)
                Flush();
        }

        /// <summary>
        /// Queues UTF-8 for the side panel (the shell's inspector) and gives
        /// it to the engine; false when there is no screen. Queued while the
        /// panel is hidden too: it shows what it was given when it comes back.
        /// </summary>
        public static bool WriteSide(byte* utf8, int length)
        {
            if (!s_ready || utf8 == null || length < 0) return false;
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                EnsureSide();
                // A long write goes in pieces the queue holds, each fed before the next.
                while (length > 0)
                {
                    int piece = length < TerminalPane.QueueBytes / 2 ? length : TerminalPane.QueueBytes / 2;
                    s_side.Enqueue(utf8, piece);
                    utf8 += piece;
                    length -= piece;
                }
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
            Feed();
            // From the top: a tree is read from its root, what does not fit
            // is below (the inspector clears the scrollback with each command).
            Scroll(s_side, int.MinValue / 2, paint: false);
            return true;
        }

        /// <summary>
        /// A key the screen takes for itself before it is the program's input:
        /// Ctrl+Tab and Ctrl+Shift+Tab move the keys between the panels,
        /// Shift+PgUp/PgDn/Home/End scroll the panel that has them; with the
        /// side panel, the arrows, PgUp/PgDn, Home and End scroll it without
        /// Shift and Esc gives the keys back to the main one. True when the
        /// key was the screen's. On the alternate screen none is: it has no
        /// scrollback, and Shift+PgUp selects in an editor. Asked by the input
        /// pump for each press, before any program can read it; a program
        /// drawing on the framebuffer itself gets the terminal painted over
        /// its frame by a scroll, until its next one.
        /// </summary>
        public static bool TakeKey(byte make, bool extended, bool shift, bool ctrl)
        {
            if (!s_ready || IsAlternateScreen) return false;

            if (make == 0x0F && !extended && ctrl)
            {
                if (s_sideShown == 0) return false;
                // Two panels: the next one and the one before are the same.
                s_focus = 1 - s_focus;
                Scroll(s_main, 0, paint: true);
                return true;
            }

            bool side = s_focus == 1 && s_sideShown > 0;
            if (!(shift || side)) return false;
            if (!extended)
            {
                if (make != 0x01 || !side) return false;   // Esc
                s_focus = 0;
                Scroll(s_main, 0, paint: true);
                return true;
            }
            TerminalPane pane = side ? s_side : s_main;
            int page = s_screenRows - 1;
            switch (make)
            {
                case 0x48: return side && Scroll(pane, -1, paint: true);
                case 0x50: return side && Scroll(pane, 1, paint: true);
                case 0x47: return Scroll(pane, int.MinValue / 2, paint: true);
                case 0x4F: return Scroll(pane, int.MaxValue / 2, paint: true);
                case 0x49: return Scroll(pane, -page, paint: true);
                case 0x51: return Scroll(pane, page, paint: true);
                default: return false;
            }
        }

        /// <summary>A key went to the program: its panel shows the end again, as xterm does.</summary>
        public static void KeyForProgram()
        {
            if (!s_ready) return;
            var buffer = s_terminal.Buffer;
            if (buffer.YDisp != buffer.YBase) Scroll(s_main, int.MaxValue / 2, paint: true);
        }

        // Moves a panel's view by lines (up when negative), then paints. True:
        // the key that asked is taken whether or not the view could move.
        private static bool Scroll(TerminalPane pane, int lines, bool paint)
        {
            if (!s_ready || pane == null || s_rendering || s_feeding) return true;
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!Enter()) return true;
                FeedQueued();
                if (lines != 0) pane.Engine.ScrollLines(lines);
                if (paint) PaintOrRedrawLocked();
                Exit();
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
            return true;
        }

        /// <summary>
        /// Asks for a side panel <paramref name="columns"/> wide at the right
        /// of the screen, 0 for none; the main panel takes what is left, less
        /// a column for the rule between them. Returns the width granted, or
        /// -1 when there is no screen.
        /// </summary>
        public static int SetSidePanel(int columns)
        {
            if (!s_ready) return -1;
            if (columns < 0) columns = 0;
            if (columns > 0 && columns < MinSideColumns) columns = MinSideColumns;
            if (columns > s_screenCols - 1 - MinMainColumns) columns = s_screenCols - 1 - MinMainColumns;
            if (columns < MinSideColumns) columns = 0;
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (columns > 0) EnsureSide();
                s_sideAsked = columns;
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
            Redraw();
            return columns;
        }

        // Built on first use, at the width asked or the least; laid out by the
        // next redraw. Callers suppress switching.
        private static void EnsureSide()
        {
            if (s_side == null)
                s_side = new TerminalPane(new XtermSharp.SimpleTerminalDelegate(),
                    s_sideAsked > 0 ? s_sideAsked : MinSideColumns, s_screenRows, Margin, Margin);
        }

        /// <summary>The side panel's width on the screen now; 0 while it is hidden.</summary>
        public static int SidePanelColumns => s_sideShown;

        /// <summary>
        /// Gives the engine everything queued, without painting.
        /// </summary>
        /// <remarks>
        /// At the end of an application's write: what the program said has to
        /// be in the engine before anyone asks it a question — whether the
        /// alternate screen is on (who owns the screen, where the output
        /// goes), where the cursor is.
        /// </remarks>
        public static void Feed()
        {
            if (!s_ready || !HasQueued) return;
            // Asked from inside the engine (a log line while it feeds or
            // paints): the lock is this thread's own, and the loop already
            // running takes the bytes.
            if (s_feeding) { s_bailFeeding++; return; }
            if (s_rendering) { s_bailRendering++; return; }

            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                ulong waitStarted = OS.Kernel.Diagnostics.PerfCounters.SinkClock();
                if (!Enter()) return;
                OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.TerminalLockTsc, waitStarted);
                FeedQueued();
                Exit();
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        private static bool HasQueued => s_main.HasQueued || (s_side != null && s_side.HasQueued);

        // Callers hold the engine lock. Until the queues are empty: bytes a
        // log line adds while the engine works go after the write being fed.
        private static void FeedQueued()
        {
            ulong started = OS.Kernel.Diagnostics.PerfCounters.SinkClock();
            while (HasQueued)
            {
                s_main.FeedQueued();
                if (s_side != null) s_side.FeedQueued();
            }
            OS.Kernel.Diagnostics.PerfCounters.CountTsc(OS.Kernel.Diagnostics.PerfCounter.TerminalFeedTsc, started);
        }

        private static bool s_pumped;

        /// <summary>
        /// The input pump paints what is queued every interval from now on;
        /// line breaks no longer have to.
        /// </summary>
        public static void PumpStarted() => s_pumped = true;

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
        internal static bool Enter()
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
        /// Feeds what is queued, then paints what changed since the last call.
        /// Scrolling is handled first, as a pixel move, so the rows that merely
        /// shifted are not re-rendered.
        /// </summary>
        public static void Flush()
        {
            if (!s_ready || s_rendering || s_feeding) return;

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
                FeedQueued();
                PaintOrRedrawLocked();
                Exit();
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        // Callers hold the engine lock and have fed the queue. The main panel
        // went to the alternate screen or left it: the layout changes with
        // it, and all of the screen repaints.
        private static void PaintOrRedrawLocked()
        {
            if (SideWanted != s_sideShown)
                RedrawLocked();
            else
                PaintLocked();
        }

        // Callers hold the engine lock and have fed the queue. The cursor is
        // in the panel that has the keys.
        private static void PaintLocked()
        {
            if (s_rendering) return;
            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            s_rendering = true;
            s_dirty = false;
            s_main.Paint(cursor: s_focus == 0);
            if (s_sideShown > 0)
            {
                s_side.Paint(cursor: s_focus == 1);
                DrawRule(Margin + (s_screenCols - s_sideShown - 1) * CellW);
            }
            s_rendering = false;
            OS.Kernel.Diagnostics.PerfCounters.CountScreen(started);
        }

        /// <summary>Repaints every row — after a resize or a mode switch.</summary>
        public static void Redraw()
        {
            if (!s_ready || s_rendering || s_feeding) return;

            // Suppressed for the same reason as Flush: this holds the engine
            // lock across a full-screen repaint.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!TryEnter()) { s_paintSkips++; return; }
                FeedQueued();
                RedrawLocked();
                Exit();
            }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        // The side panel's width as things stand: none while the main panel is
        // on the alternate screen — a full-screen program gets the screen.
        private static int SideWanted
            => s_sideAsked > 0 && s_side != null && !s_terminal.Buffers.IsAlternateBuffer ? s_sideAsked : 0;

        // Callers hold the engine lock and have fed the queue. Lays the
        // panels out as asked, then paints all of the screen, margins and the
        // rule between the panels too.
        private static void RedrawLocked()
        {
            s_rendering = true;
            s_dirty = false;

            int side = SideWanted;
            int mainCols = side > 0 ? s_screenCols - side - 1 : s_screenCols;
            s_main.Resize(mainCols, s_screenRows, Margin, Margin);
            if (side > 0)
                s_side.Resize(side, s_screenRows, Margin + (mainCols + 1) * CellW, Margin);
            s_sideShown = side;
            if (side == 0) s_focus = 0;

            FbConsole.Clear(s_bgR, s_bgG, s_bgB);
            s_main.Repaint();
            if (side > 0)
            {
                DrawRule(Margin + mainCols * CellW);
                s_side.Repaint();
            }
            s_rendering = false;
        }

        // The rule between the panels: a vertical line down the column, dim,
        // bright while the side panel has the keys.
        private static void DrawRule(int px)
        {
            byte shade = (byte)(s_focus == 1 ? 200 : 90);
            uint fg = FbConsole.Pack(shade, shade, shade);
            uint bg = FbConsole.Pack(s_bgR, s_bgG, s_bgB);
            int glyph = GlyphOf('│');
            for (int y = 0; y < s_screenRows; y++)
                FbConsole.DrawCellFast(px, Margin + y * CellH, glyph, fg, bg);
        }

        /// <summary>
        /// The CP437 glyph for a character, or '?' when the font has none.
        /// </summary>
        /// <remarks>
        /// A question mark is the honest answer for a missing glyph: it says
        /// the character could not be drawn, where a lookalike would quietly
        /// change what the screen says.
        /// </remarks>
        internal static int GlyphOf(char ch)
        {
            int glyph = FontCp437.Index(ch);
            return glyph >= 0 ? glyph : '?';
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

        internal static uint PaletteColor(int index, bool isForeground)
        {
            if (!s_paletteCached) BuildPaletteCache();

            if ((uint)index < PaletteSlots)
                return isForeground ? s_paletteFg[index] : s_paletteBg[index];

            return isForeground ? FbConsole.Pack(200, 200, 200) : FbConsole.Pack(s_bgR, s_bgG, s_bgB);
        }

        private static int EncodeUtf8(char ch, byte* destination)
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

    /// <summary>
    /// One panel of the screen: an engine, the queue of bytes for
    /// it, and the shadow of what it has painted in its rectangle.
    /// </summary>
    internal sealed unsafe class TerminalPane
    {
        internal const int QueueBytes = 64 * 1024;               // a power of two

        public readonly Terminal Engine;

        private readonly byte[] _queue;
        private volatile uint _queueHead;               // next byte to feed
        private volatile uint _queueTail;               // next byte to fill

        // Shadow of the painted grid: glyph + resolved colours per cell.
        private char[] _shadowChar;
        private uint[] _shadowFg;
        private uint[] _shadowBg;
        private bool[] _shadowValid;

        private int _cols;
        private int _rows;
        private int _x;                                  // the top left corner, in pixels
        private int _y;
        private int _lastYDisp;

        // Where the block cursor is currently painted, -1 when it is not on screen.
        private int _cursorSlot;

        public TerminalPane(ITerminalDelegate answers, int cols, int rows, int x, int y)
        {
            Engine = new Terminal(answers, TerminalConsole.Options(cols, rows));
            _queue = new byte[QueueBytes];
            _cursorSlot = -1;
            Place(cols, rows, x, y);
        }

        public bool HasQueued => _queueTail != _queueHead;

        // A new size and place: a fresh shadow, nothing of it painted.
        private void Place(int cols, int rows, int x, int y)
        {
            _cols = cols;
            _rows = rows;
            _x = x;
            _y = y;
            int cells = cols * rows;
            _shadowChar = new char[cells];
            _shadowFg = new uint[cells];
            _shadowBg = new uint[cells];
            _shadowValid = new bool[cells];
            _lastYDisp = Engine.Buffer.YDisp;
            _cursorSlot = -1;
        }

        /// <summary>Callers hold the engine lock.</summary>
        public void Resize(int cols, int rows, int x, int y)
        {
            if (cols == _cols && rows == _rows && x == _x && y == _y) return;
            if (cols != _cols || rows != _rows) Engine.Resize(cols, rows);
            Place(cols, rows, x, y);
        }

        public void Enqueue(byte* bytes, int length)
        {
            if (_queueTail - _queueHead + (uint)length > QueueBytes)
            {
                // Full: the writer feeds what is there itself. Text must not
                // be dropped — a lost character desynchronises whatever wrote
                // it from what it later reads back, and PSReadLine answers
                // that by redrawing forever. Dropped only when the engine is
                // held by the code this write interrupted.
                TerminalConsole.Feed();
                if (_queueTail - _queueHead + (uint)length > QueueBytes)
                {
                    OS.Kernel.Diagnostics.PerfCounters.Increment(OS.Kernel.Diagnostics.PerfCounter.TerminalDrops);
                    return;
                }
            }

            uint tail = _queueTail;
            for (int i = 0; i < length; i++)
                _queue[(int)((tail + (uint)i) & (QueueBytes - 1))] = bytes[i];
            _queueTail = tail + (uint)length;
            TerminalConsole.s_dirty = true;
        }

        // Callers hold the engine lock. Until the queue is empty: bytes a log
        // line adds while the engine works (it allocates, and a growing heap
        // logs) go after the write being fed, never inside it. A character cut
        // at the end of the ring is carried over by the engine itself.
        public void FeedQueued()
        {
            while (true)
            {
                uint head = _queueHead;
                uint pending = _queueTail - head;
                if (pending == 0) break;

                int at = (int)(head & (QueueBytes - 1));
                int length = (int)pending;
                if (length > QueueBytes - at) length = QueueBytes - at;

                TerminalConsole.s_feeding = true;
                fixed (byte* queue = _queue)
                    Engine.Feed((System.IntPtr)(queue + at), length);
                TerminalConsole.s_feeding = false;

                _queueHead = head + (uint)length;
                TerminalConsole.s_fed += length;
            }
        }

        // Callers hold the engine lock and have fed the queue: the cursor is
        // drawn where the last byte left it, not where a piece of a frame did.
        public void Paint(bool cursor)
        {
            var buffer = Engine.Buffer;

            // The cursor is pixels, not a cell: erase it while it is still
            // where it was drawn. A scroll moves those pixels with the text,
            // and the shadow says the cell under them holds whatever it held
            // — so no row redraw ever replaces them. Erased after the scroll,
            // a stale block rode up the screen at the end (or the start) of
            // the line it had been parked on.
            EraseCursor();

            ulong phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            ApplyScroll(buffer);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenScrollTicks,
                (long)(OS.Kernel.Diagnostics.PerfCounters.Now() - phase));

            Engine.GetUpdateRange(out int startY, out int endY);
            TerminalConsole.s_lastRangeStart = startY;
            TerminalConsole.s_lastRangeEnd = endY;
            Engine.ClearUpdateRange();

            phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            long switches = OS.Kernel.Diagnostics.PerfCounters.Value(
                OS.Kernel.Diagnostics.PerfCounter.SchedSwitches);
            if (startY <= endY)
            {
                if (startY < 0) startY = 0;
                if (endY >= _rows) endY = _rows - 1;
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

            if (!cursor) return;
            phase = OS.Kernel.Diagnostics.PerfCounters.Now();
            DrawCursor(buffer);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCursorTicks,
                (long)(OS.Kernel.Diagnostics.PerfCounters.Now() - phase));
        }

        /// <summary>Paints every row of the panel; callers cleared the screen under it.</summary>
        public void Repaint()
        {
            for (int i = 0; i < _shadowValid.Length; i++)
                _shadowValid[i] = false;
            _cursorSlot = -1;

            var buffer = Engine.Buffer;
            for (int y = 0; y < _rows; y++)
                DrawRow(buffer, y);

            _lastYDisp = buffer.YDisp;
            Engine.ClearUpdateRange();
        }

        // The engine does not announce scrolls, but YDisp — the buffer line at the
        // top of the view — moves down by the lines that left it: the output's,
        // or a scroll back down. Moving those pixels beats re-rendering every glyph:
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

        private void ApplyScroll(XtermSharp.Buffer buffer)
        {
            int delta = buffer.YDisp - _lastYDisp;
            _lastYDisp = buffer.YDisp;

            // Up (a scroll back): the engine marked every row, and the shadow
            // redraws what differs.
            if (delta <= 0) return;
            // Paint erased the cursor before calling here; nothing drawn over
            // the grid is left to move with the pixels.
            _cursorSlot = -1;

            if (delta >= _rows || ReadingScreenIsTooSlow())
            {
                // Repaint every cell instead of moving pixels.
                //
                // Moving pixels reads the screen back, and on a machine whose
                // firmware marks the framebuffer uncacheable those reads run at
                // a few MiB/s — a single scrolled line took seconds. Redrawing
                // touches far fewer bytes (glyphs, not the whole screen) and
                // never reads, so it wins outright there and is no worse
                // anywhere else.
                for (int i = 0; i < _shadowValid.Length; i++)
                    _shadowValid[i] = false;
                return;
            }

            FbConsole.ScrollUp(_x, _y, _cols * TerminalConsole.CellW, _rows * TerminalConsole.CellH,
                delta * TerminalConsole.CellH);

            int shift = delta * _cols;
            int total = _cols * _rows;
            for (int i = 0; i + shift < total; i++)
            {
                _shadowChar[i] = _shadowChar[i + shift];
                _shadowFg[i] = _shadowFg[i + shift];
                _shadowBg[i] = _shadowBg[i + shift];
                _shadowValid[i] = _shadowValid[i + shift];
            }
            for (int i = total - shift; i < total; i++)
                _shadowValid[i] = false;
        }

        // The cursor is drawn as an inverted cell rather than tracked as terminal
        // state: erase restores the cell from the shadow, so no extra bookkeeping is
        // needed and a repaint of that row simply overwrites it.
        private void EraseCursor()
        {
            if (_cursorSlot < 0) return;

            int y = _cursorSlot / _cols;
            int x = _cursorSlot % _cols;
            // A cell the shadow does not know was blank when the cursor went
            // on it (DrawCursor paints a space there); put the blank back
            // rather than leave the block for a redraw that may never come.
            if (_shadowValid[_cursorSlot])
                FbConsole.DrawCellFast(_x + x * TerminalConsole.CellW, _y + y * TerminalConsole.CellH,
                    TerminalConsole.GlyphOf(_shadowChar[_cursorSlot]),
                    _shadowFg[_cursorSlot], _shadowBg[_cursorSlot]);
            else
                FbConsole.DrawCellFast(_x + x * TerminalConsole.CellW, _y + y * TerminalConsole.CellH, ' ',
                    FbConsole.Pack(200, 200, 200),
                    FbConsole.Pack(TerminalConsole.s_bgR, TerminalConsole.s_bgG, TerminalConsole.s_bgB));
            _cursorSlot = -1;
        }

        private void DrawCursor(XtermSharp.Buffer buffer)
        {
            // DECTCEM: the engine tracks ESC[?25l / ESC[?25h, we just honour it.
            // Full-screen apps (the launcher menu, anything drawing its own UI)
            // turn the cursor off rather than leave a block parked after their
            // last write.
            if (Engine.CursorHidden) return;

            int x = buffer.X;
            int y = buffer.YBase + buffer.Y - buffer.YDisp;
            if (x < 0 || x >= _cols || y < 0 || y >= _rows) return;

            int slot = y * _cols + x;
            char glyph = _shadowValid[slot] ? _shadowChar[slot] : ' ';
            uint fg = _shadowValid[slot] ? _shadowFg[slot] : FbConsole.Pack(200, 200, 200);
            uint bg = _shadowValid[slot] ? _shadowBg[slot]
                : FbConsole.Pack(TerminalConsole.s_bgR, TerminalConsole.s_bgG, TerminalConsole.s_bgB);

            FbConsole.DrawCellFast(_x + x * TerminalConsole.CellW, _y + y * TerminalConsole.CellH,
                TerminalConsole.GlyphOf(glyph), bg, fg);
            _cursorSlot = slot;
        }

        private void DrawRow(XtermSharp.Buffer buffer, int y)
        {
            int index = buffer.YDisp + y;
            if (index < 0 || index >= buffer.Lines.Length) return;

            var line = buffer.Lines[index];
            int py = _y + y * TerminalConsole.CellH;
            int rowBase = y * _cols;

            // Tallied per row, not per cell: PerfCounters.Add is an interlocked
            // add, and a paint walks thirteen thousand cells.
            int drawn = 0, skipped = 0;

            // Hoisted: Length is a property over the line's array, and
            // CharData.Null is a static on a type that has a class constructor,
            // so reading it per cell pays a class-constructor check per cell.
            int lineLength = line.Length;
            var empty = CharData.Null;

            for (int x = 0; x < _cols; x++)
            {
                var cell = x < lineLength ? line[x] : empty;

                // The trailing half of a wide glyph carries width 0 and no code of its
                // own; the leading half already painted both columns' worth.
                if (cell.Width == 0) continue;

                char glyph = cell.Code == 0 ? ' ' : (char)(cell.Code > 0xFFFF ? '?' : cell.Code);

                // Attribute packing (CharData): flags << 18 | fg << 9 | bg, where fg/bg
                // are 256-colour palette indices (256/257 = default fg/bg).
                int attribute = cell.Attribute;
                uint fg = TerminalConsole.PaletteColor((attribute >> 9) & 0x1ff, isForeground: true);
                uint bg = TerminalConsole.PaletteColor(attribute & 0x1ff, isForeground: false);

                // SGR 7 is a flag, not a colour: the renderer is what swaps them.
                if ((((FLAGS)(attribute >> 18)) & FLAGS.INVERSE) != 0)
                {
                    uint swap = fg;
                    fg = bg;
                    bg = swap;
                }

                int slot = rowBase + x;
                if (_shadowValid[slot]
                    && _shadowChar[slot] == glyph
                    && _shadowFg[slot] == fg
                    && _shadowBg[slot] == bg)
                {
                    skipped++;
                    continue;
                }

                drawn++;
                FbConsole.DrawCellFast(_x + x * TerminalConsole.CellW, py, TerminalConsole.GlyphOf(glyph), fg, bg);

                _shadowChar[slot] = glyph;
                _shadowFg[slot] = fg;
                _shadowBg[slot] = bg;
                _shadowValid[slot] = true;
            }

            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCellsDrawn, drawn);
            OS.Kernel.Diagnostics.PerfCounters.Add(
                OS.Kernel.Diagnostics.PerfCounter.ScreenCellsSkipped, skipped);
        }
    }
}
