using System;
using System.Runtime;
using SharpOS.AppSdk;

namespace TriCNES
{
    // TriCNES on SharpOS: cartridge off the ESP, picture to the GOP
    // framebuffer, keyboard into controller port 1.
    //
    // The emulator core is upstream and untouched; everything here is the
    // console it plugs into. Upstream drives it from a WinForms window and
    // feeds inputs from a recorded TAS file — the controller port is a plain
    // public byte that the GUI writes, and the core only overwrites it while
    // replaying a recording. So live input needs no changes to the core: we
    // just write that byte ourselves.
    //
    // Exit codes: 42 = clean quit (Esc), 1 = managed exception, 2 = no ROM.
    internal static unsafe class Entry
    {
        // Where the cartridge lives on the ESP, staged by the build like
        // DOOM1.WAD is.
        private const string RomPath = "apps/GAME.NES";

        // Controller bit order, as the NES shift register reads them:
        // A, B, Select, Start, Up, Down, Left, Right.
        private const byte BtnA = 0x80;
        private const byte BtnB = 0x40;
        private const byte BtnSelect = 0x20;
        private const byte BtnStart = 0x10;
        private const byte BtnUp = 0x08;
        private const byte BtnDown = 0x04;
        private const byte BtnLeft = 0x02;
        private const byte BtnRight = 0x01;

        // Per-frame timing goes to the same console that draws over the
        // picture, so it is off by default: the measurement destroys the thing
        // being measured. Flip on when the question is speed, not gameplay.
        private const bool ShowTiming = false;

        private static byte s_buttons;
        private static bool s_quit;
        private static uint s_unpacedFrames;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            AppHost.WriteString("tricnes: start\n");

            try
            {
                if (!AppHost.FileExists(RomPath))
                {
                    AppHost.WriteString("tricnes: no ");
                    AppHost.WriteString(RomPath);
                    AppHost.WriteChar('\n');
                    return 2;
                }

                Emulator emu = new Emulator();
                Cartridge cart = new Cartridge(RomPath);
                emu.Cart = cart;

                // Both back-references, exactly as the upstream GUI wires them:
                // the mapper reaches the console through Cart.Emu to read the
                // 72-pin connector, and crashes on the first cycle without it.
                cart.Emu = emu;
                cart.MapperChip.Cart = cart;

                AppHost.WriteString("tricnes: mapper ");
                AppHost.WriteUInt(cart.MemoryMapper);
                AppHost.WriteChar('\n');

                emu.Reset();

                GopVideo video = null;
                if (AppHost.TryGetFramebuffer(
                        out ulong fbBase, out uint fbWidth, out uint fbHeight,
                        out uint fbStride, out uint fbFormat))
                {
                    video = new GopVideo(fbBase, fbWidth, fbHeight, fbStride, fbFormat);
                    AppHost.WriteString("tricnes: video ");
                    AppHost.WriteUInt(fbWidth);
                    AppHost.WriteChar('x');
                    AppHost.WriteUInt(fbHeight);
                    AppHost.WriteString(" scale ");
                    AppHost.WriteUInt((uint)video.Scale);
                    AppHost.WriteChar('\n');
                }
                else
                {
                    AppHost.WriteString("tricnes: headless (no framebuffer)\n");
                }

                RunLoop(emu, video);

                AppHost.WriteString("tricnes: quit\n");
                return 42;
            }
            catch (Exception e)
            {
                AppHost.WriteString("tricnes: exception: ");
                AppHost.WriteString(e.Message ?? "(no message)");
                AppHost.WriteChar('\n');
                return 1;
            }
        }

        // 60 Hz if there is a clock to pace against, otherwise as fast as the
        // machine goes. Unpaced is not a fallback worth hiding: without a time
        // source the emulator would run at whatever speed the host manages,
        // which is wrong but visible, rather than wrong and silent.
        private static void RunLoop(Emulator emu, GopVideo video)
        {
            bool paced = AppHost.TryGetHpet(out ulong counterAddress, out ulong frequencyHz)
                         && counterAddress != 0 && frequencyHz != 0;

            // Say which mode we are in. Without this, "no timing lines" reads
            // as "the emulator hung" when it actually means "there is no clock
            // to measure with" — VirtualBox ships with HPET off by default.
            // Never trust a clock without watching it move. A counter that is
            // present but stopped turns the pacing spin below into a permanent
            // hang — screen frozen, not one line of output, indistinguishable
            // from a crash. (The kernel hit exactly this on real hardware:
            // firmware hands over an HPET whose enable bit reads set while the
            // counter sits still.)
            if (paced)
            {
                ulong t0 = ReadCounter();
                bool moves = false;
                for (int i = 0; i < 1000000 && !moves; i++) moves = ReadCounter() != t0;
                if (!moves)
                {
                    AppHost.WriteString("tricnes: HPET present but STOPPED - running unpaced\n");
                    paced = false;
                }
            }

            AppHost.WriteString(paced
                ? "tricnes: paced 60 Hz (HPET)\n"
                : "tricnes: unpaced, no timing (VBox: modifyvm --hpet on)\n");
            ulong ticksPerFrame = paced ? frequencyHz / 60UL : 0UL;
            ulong next = paced ? ReadCounter() + ticksPerFrame : 0UL;

            // Timed separately, because "it feels laggy" has two very different
            // causes with opposite fixes: a slow core (cycle-accurate
            // emulation) or a slow blit (pixels to an uncached framebuffer).
            // Input is sampled once per emulated frame, so whichever is slow
            // becomes the input latency directly.
            ulong emulateTicks = 0, blitTicks = 0;
            int framesTimed = 0;
            ulong windowStart = paced ? ReadCounter() : 0;

            while (!s_quit)
            {
                PumpKeyboard();
                emu.ControllerPort1 = s_buttons;

                ulong t0 = paced ? ReadCounter() : 0;
                emu._CoreFrameAdvance();
                ulong t1 = paced ? ReadCounter() : 0;
                video?.Blit(emu.Screen.Bits);
                ulong t2 = paced ? ReadCounter() : 0;

                // Count frames even without a clock: the number alone answers
                // "is it running at all", which is the first thing anyone asks
                // when the screen looks wrong.
                if (!paced)
                {
                    if (ShowTiming && ++framesTimed == 60)
                    {
                        framesTimed = 0;
                        s_unpacedFrames += 60;
                        AppHost.WriteString("tricnes: frames ");
                        AppHost.WriteUInt(s_unpacedFrames);
                        AppHost.WriteChar('\n');
                    }
                }
                else if (ShowTiming)
                {
                    emulateTicks += t1 - t0;
                    blitTicks += t2 - t1;
                    if (++framesTimed == 60)
                    {
                        // Whole-loop time too, not just the sum of the parts:
                        // anything outside those two measurements (key polling,
                        // pacing, this very print) is invisible otherwise, and
                        // "the parts add up but the loop is slower" is exactly
                        // the case worth catching.
                        ulong windowTicks = ReadCounter() - windowStart;
                        ReportTiming(emulateTicks, blitTicks, windowTicks, frequencyHz, framesTimed);
                        emulateTicks = 0; blitTicks = 0; framesTimed = 0;
                        windowStart = ReadCounter();
                    }
                }

                if (!paced) continue;

                // Spin to the deadline. Nothing else runs on this core, so
                // sleeping would only mean giving time back to nobody.
                //
                // Bounded anyway: an unbounded wait on a clock is a hang
                // waiting to happen, and one frame skipped beats a frozen
                // machine with no explanation.
                for (ulong guard = 0; ReadCounter() < next && guard < 200000000UL; guard++) { }
                next += ticksPerFrame;
            }
        }

        // The HPET counter is memory-mapped hardware, but nothing in a spin
        // loop writes to it, so the compiler is entitled to read it once and
        // reuse the value forever — turning "wait until the clock reaches X"
        // into an infinite loop. NoInlining forces a real load per iteration.
        //
        // This is why the hang showed up in VirtualBox and not in QEMU: under
        // QEMU a frame takes ~126 ms, longer than the 16 ms budget, so the
        // wait loop was never entered at all.
        //
        // Now a thin forward to the SDK's reader, which also extends a 32-bit
        // counter across its wrap: reading that register raw gave a clock
        // that ran backwards every 300 s on hardware whose HPET is narrow.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ulong ReadCounter()
            => System.Diagnostics.Stopwatch.ReadCounter();

        private static void ReportTiming(ulong emulateTicks, ulong blitTicks,
                                         ulong windowTicks, ulong frequencyHz, int frames)
        {
            ulong perMs = frequencyHz / 1000UL;         // ticks per millisecond
            if (perMs == 0) return;

            ulong loopMs = windowTicks / (ulong)frames / perMs;

            AppHost.WriteString("tricnes: frame ");
            AppHost.WriteUInt((uint)loopMs);
            AppHost.WriteString(" ms (emulate ");
            AppHost.WriteUInt((uint)(emulateTicks / (ulong)frames / perMs));
            AppHost.WriteString(" + blit ");
            AppHost.WriteUInt((uint)(blitTicks / (ulong)frames / perMs));
            AppHost.WriteString(")  fps ");
            AppHost.WriteUInt(loopMs == 0 ? 999u : (uint)(1000UL / loopMs));
            AppHost.WriteChar('\n');
        }

        // Drain whatever key events are queued and fold them into the button
        // byte. Presses and releases both matter — a controller reports what
        // is HELD, so a missed release leaves the character walking forever.
        private static void PumpKeyboard()
        {
            for (int i = 0; i < 32; i++)
            {
                if (AppHost.TryReadKey(out KeyInfo key) != AppServiceStatus.Ok) return;
                if (!key.HasRaw) continue;

                byte mask = MapKey(key.RawMake, key.RawExtended);
                if (mask == 0)
                {
                    // Esc quits; only on press, so the release does not
                    // immediately re-trigger anything later.
                    if (key.RawMake == 0x01 && key.RawDown) s_quit = true;
                    continue;
                }

                if (key.RawDown) s_buttons |= mask;
                else s_buttons &= (byte)~mask;
            }
        }

        // csc requires a Main for OutputType=Exe; never called — the kernel
        // enters at SharpAppEntry via the PE entry point symbol.
        private static int Main() => 0;

        // Set-1 make codes -> NES buttons. Arrows for the D-pad (both the
        // extended cluster and the numeric keypad, so it works without a
        // dedicated arrow block), Z/X for B/A, Enter/Right-Shift for
        // Start/Select — the layout every emulator has used for thirty years.
        private static byte MapKey(byte make, bool extended)
        {
            if (extended)
            {
                switch (make)
                {
                    case 0x48: return BtnUp;
                    case 0x50: return BtnDown;
                    case 0x4B: return BtnLeft;
                    case 0x4D: return BtnRight;
                    default: return 0;
                }
            }

            switch (make)
            {
                case 0x48: return BtnUp;      // keypad 8
                case 0x50: return BtnDown;    // keypad 2
                case 0x4B: return BtnLeft;    // keypad 4
                case 0x4D: return BtnRight;   // keypad 6
                case 0x2C: return BtnB;       // Z
                case 0x2D: return BtnA;       // X
                case 0x1C: return BtnStart;   // Enter
                case 0x36: return BtnSelect;  // Right Shift
                default: return 0;
            }
        }
    }
}
