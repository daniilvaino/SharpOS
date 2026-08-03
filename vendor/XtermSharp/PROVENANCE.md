# XtermSharp — fork

**Upstream:** https://github.com/migueldeicaza/XtermSharp
**Our fork:** https://github.com/daniilvaino/XtermSharp (`master`)
**Imported:** 2026-08-02, fork commit `1bed529`, as a `git subtree --squash` at `vendor/XtermSharp/`
**License:** MIT (see [LICENSE](LICENSE)) — © xterm.js authors, SourceLair, Christopher
Jeffrey, Miguel de Icaza. All four copyright lines travel with the code.

Unlike [Iced](../Iced/) and [PeNet](../PeNet/), which are near-pristine cuts kept
recognizable against their upstreams, this is a **real fork**: it will lose NStack, shed the
renderer/selection/PTY/Mac layers, move onto our own std, and diverge behaviorally.
Upstream is dormant, so nothing is expected to be pulled back down; the subtree exists so
our own work has a public home to be pushed up to.

## Why XtermSharp and not XTerm.NET

Chosen on measurements from `work/TermRace` over the terminal corpus vendored in
`work/shitty/tests` (see that runner's README for the full scoreboard, 2026-08-02):

- **Grid correctness.** On the xterm.js fixtures 23 cases fail in both engines, 16 only in
  XTerm.NET, 7 only here; on the libvterm DSL, 23 both, 11 only XTerm.NET, 3 only here.
  One engine roughly contains the other rather than trading wins.
- **Allocation.** 9.7× vs 51.9× bytes allocated per MiB of input. XTerm.NET stores a
  `string` per cell; under a non-moving mark-sweep GC that is a different regime, and
  fixing it would mean rewriting its buffer, parser and renderer.
- **Kernel fit.** netstandard2.0, unsafe pointer parser, one dependency (NStack, used by 5
  files) against net6.0 + Unicode.net + Wcwidth and a wider BCL surface.

Against it: XtermSharp is not crash-hardened. Of its corpus failures ~1688 are a single
`NotImplementedException` class out of `Terminal.MatchColor` (four TODO stubs in one file);
the genuine memory bugs are ~47 `IndexOutOfRange` + 15 `NullReference` + 12
`ArgumentOutOfRange`, plus one hang. Closing those is the first work item of the fork.

## Not in the kernel build yet

`OS.csproj` includes vendored source per directory by name ([Iced](../../OS/OS.csproj),
PeNet); no `<Compile Include>` points here. This tree is currently consumed only by the
desktop-.NET test runner in `work/TermRace`, which runs on the full BCL with JIT. The
kernel case — NativeAOT against `std/no-runtime/` — is a separate exercise and has not
started.

## Our changes

| what | why |
| --- | --- |
| `XtermSharp/XtermSharp.csproj` — dropped `Visible="False"` metadata on the `InternalsVisibleTo` `AssemblyAttribute` | modern SDKs pass every metadata entry to the attribute constructor, emitting `InternalsVisibleTo("Tests", Visible)`; the project did not compile |
| `Terminal.MatchColor` — implemented nearest-palette-entry search over `Color.DefaultAnsiColors` instead of `throw new NotImplementedException` | on the SGR 38;2 / 48;2 direct-color path, i.e. every truecolor sequence killed the terminal |
| `Terminal.EmitA11yTab` — empty body instead of `throw new NotImplementedException` | accessibility hook raised from the HT handler (`InputHandler.cs:531`); nothing consumes it, and it sat on the path of every `\t` |
| `InputHandler.CharAttributes` — bounds checks on `pars` for SGR 38/48, both `;2;` and `;5;` | truncated color sequences read past the end of the parameter array |
| `Buffer.PreviousTabStop` / `NextTabStop` — clamp index and `limit` to `tabStops.Length` | `MarginRight` (DECSLRM) can name a column past the last tab stop, and `tabStops` lags `Cols` across a resize |
| `TerminalCommandExtensions` OSC 20/21 — `?? ""` on `Title`/`IconTitle` | both stay null until a title is set; a report request before that dereferenced null |
| `Buffer.SetMargins` — clamp margins into `[0, Cols-1]`, `left <= right` | DECSLRM parameters were never validated against the page width; `Print` uses `MarginRight` as the wrap edge, so the cursor walked off the line |
| `TerminalCommandExtensions.csiDECSLRM` — treat an omitted/zero parameter as the default, not column 0 | `CSI ; s` collapsed both margins onto column 0, after which DCH computed a negative delete count |
| `Terminal.DeleteChars` — return when the clamped count is `<= 0` | `DeleteCells` reads a negative count as "copy from before the start of the line" |
| `GetRectangleFromRequest` — clamp after applying the origin-mode offsets, not before | adding `ScrollTop` to an already-clamped `bottom` pushed DECERA/DECSERA/DECRQCRA rectangles back off the page |
| `Terminal.SetCursor` — re-clamp inside the scroll region under origin mode | CUP in a region with a non-zero top parked the cursor past the last line |
| `Terminal.ReverseIndex` — only decrement `Y` when it is above 0 | RI issued above the scroll region walked the cursor to row -1 |
| `InputHandler.EraseInDisplay` — bound the `Lines[j + 1]` unwrap | there is no next line to unwrap when erasing on the last row |
| `csiDECCRA` — bound the destination column (`colTarget + col`), not the source offset | the rectangle copy ran off the right edge of the line |
| `InputHandler.ScrollUp` / `ScrollDown` / `DeleteLines` — clamp the count to the scroll-region height | each line is a splice, so `CSI 10000004 S` ran for minutes; a count above the region height is indistinguishable from one that clears it |
| `Terminal.RestrictCursor` (new) — called at the head of the cursor, erase and line/char editing commands | printing into the last column leaves X one past the right edge with the wrap deferred; every command that read the cursor measured from a column that does not exist. This is xterm.js's `_restrictCursor`, and it is the off-by-one behind the CUB/EL/ED/HT fixture cluster |
| `RuneHelper.bisearch` call — pass `count - 1`, not `count` | it reads `table[max,1]`, so the count read one row past the end and threw on every CJK code point |
| `InputHandler.Print` — real column width via `RuneHelper.ConsoleWidth` instead of the hardcoded `chWidth = 1` | wide glyphs occupied one cell. NStack's `Rune.ColumnWidth` is unusable (same off-by-one, unpatched upstream — that is what the "1 until we get a fixed NStack" comment meant), so the fork's own wcwidth port is used |
| `BufferLine.GetTrimmedLength` — sum `data[j].Width`, not `data[i].Width` | the loop multiplied the last cell's width by the column count instead of summing the row |
| `InputHandler.ScrollDown` — insert the blank line at `ScrollTop`, not `ScrollBottom` | SD deleted the bottom line of the region and put the blank back in the same place, so the region never moved |
| `csiDECSET`/`csiDECRESET` mode 20 (LNM) — drive `Options.ConvertEol` instead of a TODO comment | the mode was parsed and dropped, so LF after `CSI 20 h` did not imply CR |
| `EscapeSequenceParser.ControlDispatched` (new hook) + `InputHandler.precedingCodepoint` | REP repeats the preceding *printed* character; ECMA-48 leaves the post-control case undefined and xterm.js makes it a no-op, but we repeated whatever cell happened to be to the left |
| `InputHandler.RepeatPrecedingCharacter` — advance the cursor past the copies and wrap the run across lines | REP left the cursor in place (the next print overwrote the copies) and clipped at the right margin instead of wrapping |
| `Terminal.Reset` (RIS) — clear both buffers and refill the viewport | `Setup` restored only the modes, so a full reset left the screen and scrollback intact; `Buffer.Clear` drops the lines without recreating them, hence the explicit `FillViewportRows` |
| `csiDECRESET` 1049 — always clear the alt buffer when activating the normal one | only 1047 asked for the clear, so leaving via 1049l left the alt screen stale and the next 1049h re-entered the old content |
| `Terminal.SaveCursor`/`RestoreCursor` — actually save the modes and the charset, and restrict the cursor on restore | `savedMarginMode`/`savedOriginMode`/`savedWraparound`/`savedReverseWraparound` were only ever written by `SoftReset`, so every DECRC restored zeroes — in particular it silently switched wraparound off |
| `InputHandler.CursorPrecedingLine` (CPL) — subtract the parameter once | it did `Y -= param` and then `newY = Y - param`, moving two lines per requested line |
| `CSI j` (HPB) — registered as a backward cursor move | ECMA-48 8.3.58; xterm.js has no handler, so it silently did nothing |
| `InputHandler.Print` wrap — scroll only when the cursor is exactly at the region bottom | `>=` also scrolled the region whenever the cursor sat below it, costing the region one line per wrap |
| `Terminal.Index` (IND) — same equality fix, plus stop at the last row | it scrolled the region whenever the next line was past the region bottom |
| `Terminal.Index` / `ReverseIndex` — collapse a pending wrap first | both read the cursor, so a wrap left pending sent the next character to the following line |
| `InsertLines` / `DeleteLines` / `ShiftColumns` / `InsertColumn` — no-op outside the scroll region, and cover its last line (`<=`) | IL/DL/DECIC edited lines the region does not own, and the loops stopped one line short |
| `InsertLines` / `DeleteLines` — leave the cursor at the left margin | xterm.js does; libvterm notes real xterm does not, see the divergences below |
| `CSI Ps SP @` / `CSI Ps SP A` (SL/SR) — implemented as `Terminal.ShiftColumns` | the space intermediate was ignored, so SR ran as a plain cursor-up |
| `Tab` / `CursorForwardTab` / `CursorBackwardTab` — do nothing while a wrap is pending | tabbing collapsed the pending column, so the next character overwrote the last one instead of wrapping |
| `CursorNextLine` / `CursorPrecedingLine` — delegate to `CursorDown`/`CursorUp` | they had their own bounds and ignored the scroll region |
| Execute handlers 11/12 (VT, FF) — route through `LineFeed` | they bypassed the carriage return that LNM adds |
| `EscapeSequenceParser.EXECUTABLES` — exclude 0x18/0x1a, include 0x19 | CAN was listed as an ordinary executable, so a per-state rule overrode the anywhere-rule and CAN no longer cancelled a sequence in progress |
| `csiDECSET`/`csiDECRESET` mode 6 (DECOM) — home the cursor to the region origin | setting or resetting origin mode left the cursor where it was |
| `RepeatPrecedingCharacter` — honour IRM, and wrap instead of collapsing a pending wrap | REP overwrote under insert mode and repeated onto the character it was copying |

All of the above were found by the corpus runner in `work/TermRace` and confirmed with its
`--reduce` mode, which shrinks a failing corpus file to a minimal input that still fails at
the same stack frame. State as of 2026-08-02: **every byte corpus is clean** — corpus 16/16, tmux 4166/4166,
mosh 16/16 + 11/11, ghostty 616/616 + 3271/3271 + 20/20, fuzz 400/400. Grid fixtures are xterm.js 74/76,
libvterm 376/431 and alacritty 31/45, up from 46/76, 330/431 and 28/45 at import.

Two known divergences, both on the same axis — whether an operation preserves a pending
wrap. xterm.js restricts the cursor with `maxCol = cols` in the erase and
character-insert family, preserving a pending wrap. Doing that here passes alacritty's
`erase_in_line` but fails the xterm.js fixtures `t0050-ICH` and `t0055-EL` — the two
reference corpora genuinely disagree, and XTerm.NET (the other port) splits the other way.
The same split shows up on DECRC: alacritty's `wrapline_alt_toggle` wants the saved
pending-wrap column to survive the restore, xterm.js's `t0060-DECSC` and `t0061-CSI_s` want
it collapsed. A third one: xterm.js moves the cursor to the left margin after IL/DL, while libvterm's
`13state_edit` records that neither xterm nor xfce4-terminal do, and its block fails for us
now. We follow xterm.js's own fixtures in all three cases; see the remark on
`RestrictCursor`.

Keep this table growing as the fork diverges — once the naming discipline of
`CLAUDE.md` §"Инвариант 2" applies (partial types moving to `SharpOS.*` namespaces), this
file is what explains why our behavior differs from upstream and from xterm.js.
