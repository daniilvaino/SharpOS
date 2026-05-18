# Step 75 — Phase B#3: PS/2 keyboard → line editor → native-tier shell (CLOSED)

**Status:** CLOSED. Phase B's interactive half. Built on the Phase B#2
framebuffer pillar (step 74), this delivers input + a command shell —
completing the **native-tier console off-ramp** (the plan's insurance
policy if the Roslyn path stalls; UART/display/keyboard are shared
with the §1 path regardless). Commits 75..78 (step-number prefixes
continue the prior-session `7x` scheme; done/ writeup sequence is
074 → 075).

## What was done

Four verify-before-commit sub-steps, each with a headless-deterministic
oracle (no keyboard exists headless, so every oracle is a pure
synthetic-script self-test; real typing is eyeball-proven under
`SHARPOS_GUI=1`):

- **75 — own i8042/PS-2 keyboard** (`OS.Hal.Ps2Keyboard`). The
  post-EBS analogue of the own 16550 UART. Non-destructive while UEFI
  is alive: STATUS (0x64) read + DATA (0x60) drain only, NO controller
  self-test / reset / config rewrite — UEFI's own 8042 driver still
  feeds the launcher and a 0xAA/0x60 sequence would disturb shared
  state (targeted, not sledgehammer). Pure set-1 make/break decoder
  with shift/caps latch → Char/Enter/Backspace/Escape/Control.
  Oracle: STATUS presence + decode a synthetic script →
  `[ps2] status=0x1C present=Y decode="aB1!" ent=1 bks=1 esc=1 PASS`.
- **76 — line editor** (`OS.Hal.LineEditor`). Pure single-line buffer
  (printable insert / Backspace / Enter); no FbConsole/hardware
  dependency → clean oracle. `[lined] len=4 buf="cat " sub=1 PASS`
  (type "cat x", Backspace, Enter, through the real decoder).
- **77 — shell engine** (`OS.Hal.Shell`). Hand-rolled tokeniser
  (string indexing only — no Substring/Split, not guaranteed here).
  Commands: help/ver/mem/devices/echo/clear(/exit). `mem` sums
  Usable regions from the boot map (PhysicalMemory keeps no stats).
  Pure of any input loop. Oracle drives Execute() with literals →
  `[shell] known=ok unk=rejected memMiB=449 regions=11 PASS`.
- **78 — interactive REPL + output indirection**. `OS.Hal.ShellOut`
  (serial Console always; + `OS.Hal.FbTty` when live), `FbTty`
  (text cursor over FbConsole: newline/wrap/clear-on-overflow/
  Backspace-erase). `Shell.ExecuteCore` rewritten over char[] so the
  REPL needs no `new string`; `Execute(string)` copies into a scratch
  buffer (oracle path unchanged). `Shell.RunInteractive()` polls
  Ps2Keyboard → Decode → LineEditor, echoes to serial+FbTty,
  dispatches on Enter, until `exit`. Gated `Probes.ShellInteractive`
  **default-off** (a blocking poll loop would hang the headless
  regression run; ILC dead-codes it) — flip true + `SHARPOS_GUI=1`
  for the live shell. Headless verification = the engine oracle still
  PASS after the refactor (help now lists `exit` = the rewritten
  core runs).

## Lessons learned

- **Headless has no input — so every interactive layer needs a pure
  self-test.** Decoder, line editor, and shell engine were each made
  pure functions over (synthetic input → asserted output), keeping
  the always-on regression battery meaningful while the blocking
  REPL stays default-off. Eyeball is reserved for what genuinely
  can't be self-checked (the live FbTty REPL under GUI).
- **Don't reset shared hardware a still-live owner depends on.** The
  8042 is driven by UEFI for the launcher; the own PS/2 driver stays
  read/drain-only until the post-EBS take-over, exactly mirroring the
  own-UART-alongside-UEFI-ConOut pattern.
- **`static readonly byte[]/char[]` keeps working in Phase 4** (font,
  scancode maps, scratch buffers) — post GcStaticsMaterializer, same
  as CoreClrProbe. The cctor trap is a pre-materializer concern only.

## Files

- `OS/src/Hal/Ps2Keyboard.cs`, `LineEditor.cs`, `Shell.cs`,
  `ShellOut.cs`, `FbTty.cs`
- `OS/src/Kernel/Diagnostics/{Ps2Probe,LineEditorProbe,ShellProbe,Probes}.cs`
- `OS/src/Boot/BootSequence.cs` (Phase4 hooks + gated REPL tail)

## Regression battery (current green baseline)

own-UART PRESENT · `[fb] map+rw OK px0=0x123456` · `[fbtext]
1280x800 fmt=1 crc=0x7A1D4075 PASS` · `[ps2] … PASS` · `[lined] …
PASS` · `[shell] known=ok unk=rejected memMiB=449 regions=11 PASS` ·
EH-gates L15=1501/L16=1616/L17=1704 · hosted census OK=19 DEG=2
FAIL=20.

## Deferred

- `run-normalhello` shell command (wiring the ELF/normal-hello path
  into the shell — heavier; the launcher already covers running it).
- FbTty scroll-by-copy (clear-on-overflow is fine for a boot shell).
- The single SehUnwind §11 root — large standalone step, scheduled
  Phase D after Phase B per the DAG.

## Next

Phase B (native-tier console off-ramp) is functionally closed: serial
+ framebuffer + keyboard + line editor + shell, each with a permanent
regression oracle. Per the reconciled DAG ordering (B → D → E), the
next major front is **Phase D — the SehUnwind §11 root** (the C#
RtlVirtualUnwind port that unblocks every deferred 💥 census case:
sockets, OpenSSL crypto, OS threads), or resuming the main Roslyn
path. Decide direction at the next step.
