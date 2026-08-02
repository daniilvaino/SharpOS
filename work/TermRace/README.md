# termrace

Test runner that drives the two candidate C# terminal emulators —
[XtermSharp](../../vendor/XtermSharp) and [XTerm.NET](../XTerm.NET) — through the terminal test
corpus vendored in [shitty/tests](../shitty/tests), and prints a side-by-side scoreboard.

Plain desktop .NET (net8.0), JIT, nothing kernel-related. The point of this stage is to
turn "which engine do we fork into SharpOS" into numbers.

## Running

```powershell
./run.ps1                                    # xtermjs + libvterm + corpus, both engines
./run.ps1 -Suite all -Json baseline.json     # every suite, machine-readable output
./run.ps1 -Suite all -Out reports/run.txt    # same text report the console shows, to a file
./run.ps1 -Suite xtermjs -Filter DECSTBM     # one family of cases, with diffs
./run.ps1 -Suite xtermjs -Engine xtermnet    # one engine
```

Or directly:

```powershell
dotnet run --project src/TermRace -c Release -- --tests ../shitty/tests --suite all --quiet
```

`--out PATH` tees everything printed (summaries, diffs, scoreboard) into a text report;
`--json PATH` writes one record per case (suite, engine, case, status, ms, allocated bytes
per MiB, detail) for post-processing. Both can be given at once.

Exit code is 1 when any case is `FAIL`/`CRASH`/`TIMEOUT`, 0 otherwise.

## Suites

| suite | corpus | oracle |
| --- | --- | --- |
| `xtermjs` | `tests/xtermjs`, 76 `.in`/`.text` pairs | feed bytes through a default-PTY (ONLCR) filter, dump the 80×25 viewport with trailing blanks stripped, diff against `.text` |
| `libvterm` | `tests/libvterm/upstream`, 43 `.test` DSL files → 431 blocks | run `PUSH`/`RESET`/`RESIZE`, assert `?cursor`, `?screen_row`, `?screen_chars`, `?screen_text`, `?screen_eol` |
| `corpus` | `tests/corpus`, 16 named crash regressions | did not throw, did not hang, grid still readable |
| `tmux`, `mosh`, `moshparser`, `ghostty-parser`, `ghostty-stream`, `ghostty-osc`, `fuzz` | the respective byte corpora | same crash oracle; `crash` runs all of them |
| `bench` | tmux corpus, replayed to 8 MiB per workload | MiB/s and allocated bytes per MiB of input, via `GC.GetAllocatedBytesForCurrentThread` |

Every case runs on a watchdog thread (`--timeout`, default 10 s). A hung case is reported
`TIMEOUT` and its engine instance is discarded rather than reused, because a runaway thread
keeps mutating it.

## Scoreboard

At import (2026-08-02), before any fork patches — this is the measurement the engine choice
was made on:

```
suite                xtermsharp pass/total       xtermnet pass/total
xtermjs                       46/76 bad=30              37/76 bad=39
libvterm            330/431 bad=27 skip=74    350/431 bad=34 skip=47
tmux                    2702/4166 bad=1464                 4166/4166
ghostty-stream           3126/3271 bad=145           3270/3271 bad=1
fuzz                       249/400 bad=151                   400/400
bench              145.9 MiB/s 9.65x alloc   83.3 MiB/s 51.91x alloc
```

XtermSharp was more correct on the grid fixtures but not crash-hardened; XTerm.NET survived
almost every byte corpus but lost on the fixtures and allocated ~5× more per MiB (a `string`
per cell). After the fork patches in `vendor/XtermSharp` (see its `PROVENANCE.md`):

```
suite                xtermsharp pass/total       xtermnet pass/total
xtermjs                       53/76 bad=23              37/76 bad=39
libvterm            359/431 bad=25 skip=47    350/431 bad=34 skip=47
every byte corpus                    clean           clean except 1
bench             138.8 MiB/s 20.3x 5/5 ok   91.3 MiB/s 51.9x 5/5 ok
```

Throughput swings run-to-run by tens of percent on the same build; the allocation ratio is
deterministic and is the number to compare. The allocation rise on the left is the workload
mix, not a regression: one workload used to crash and now completes, which is why the cell
carries `N/M ok`.

Per-case detail for any run lives in its `runs/<timestamp>/results.json`.

## What the runner deliberately does not cover yet

- **esctest** (`tests/esctest`, 549 cases). It probes the terminal with `DECRQCRA`
  rectangular checksums instead of dumping grids; neither engine implements the report.
  Path: implement `DECRQCRA` in an engine, add a stdin→Feed / reply→stdout adapter
  process, then point the Python suite at it. Second harness, not this one.
- **alacritty** (`.recording` + JSON grids) and **contour** (`.dump` goldens) — data is
  ready, mapping their schema onto `Screen` is the next cheap win after this.
- **realworld** (280 sessions) — the inputs are `.zst`, and .NET has no in-box zstd.
- **vtebench** — its workloads are POSIX shell generators, so `bench` replays the tmux
  corpus instead. Same measurement, portable input.
- **xterm_vttests**, kitty, konsole, wezterm, windows_terminal, vte, libtsm — upstream
  drivers without ported assertions; usable as fuzz input only.

## Layout

```
src/TermRace/Engines/     ITerminalEngine + Screen, one adapter per emulator
src/TermRace/Suites/      one file per oracle, plus the watchdog
src/TermRace/Program.cs   CLI, suite/engine selection
src/TermRace/Report.cs    scoreboard + JSON
```

Adding a third engine is one file implementing `ITerminalEngine` plus a line in
`Engines.Create`.

### Engine adapters, caveats

- **XtermSharp** takes raw bytes and does its own UTF-8 decoding. Its `ConvertEol` option
  (an engine-side ONLCR emulation) is turned off, because the harness already models the
  PTY line discipline where a fixture calls for it.
- **XTerm.NET** takes `string`, so the adapter owns a `UTF8Encoding` decoder and keeps it
  across `Feed` calls so sequences split across chunks still decode.
- Both engines log parser complaints to stdout; `Program` parks the real writer and
  redirects `Console.Out` to `TextWriter.Null` so the report stays parseable.

### Where the engines live

XtermSharp is our fork, brought in as a `git subtree` at
[vendor/XtermSharp/](../../vendor/XtermSharp) — see its `PROVENANCE.md` for the import
revision and the running list of our changes. XTerm.NET stays an unmodified checkout under
`work/` and is kept wired up on purpose: it is the control group when we start fixing the
fixtures that fail in both.
