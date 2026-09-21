using System;
using System.IO;
using System.Runtime;
using Fami.Core;
using Fami.Core.CPU;
using SharpOS.AppSdk;

namespace Fami
{
    // Fami on SharpOS: cartridge off the ESP, picture to the GOP framebuffer,
    // keyboard into controller port 1.
    //
    // The second NES emulator on this system, and the reason for the first
    // one's existence being questioned: TriCNES models the console cycle by
    // cycle and scores 141/141 on AccuracyCoin, but costs ~19 ms per frame on
    // a laptop, which is not a game so much as a slideshow. This core is
    // instruction-stepped and runs the same ROM in ~2 ms. Accuracy reference
    // and playable emulator are different jobs; both stay.
    //
    // The emulator core is upstream and untouched. Everything here is the
    // console it plugs into.
    //
    // Exit codes: 42 = clean quit (Esc), 1 = managed exception, 2 = no ROM.
    internal static unsafe class Entry
    {
        // Where the cartridge lives on the ESP, staged by the build like
        // DOOM1.WAD is.
        private const string RomPath = "apps/GAME.NES";

        // One frame of NTSC master cycles, from upstream's own frame loop
        // (Fami.Core\Interface\Main.cs). Duplicated rather than referenced
        // because that file is the SDL front-end, which is exactly what this
        // project does not compile. Upstream also drops one cycle on odd
        // frames, which is the real console's short scanline; kept, because a
        // frame loop that is 1/89342 fast drifts against the PPU and shows up
        // as a jitter nobody will connect back to this line.
        private const int CyclesPerFrame = 89342;

        // Controller bit order, as the NES shift register reads them:
        // A, B, Select, Start, Up, Down, Left, Right. Same values upstream's
        // ControllerButtonEnum carries in its low byte.
        private const uint BtnA = 0x80;
        private const uint BtnB = 0x40;
        private const uint BtnSelect = 0x20;
        private const uint BtnStart = 0x10;
        private const uint BtnUp = 0x08;
        private const uint BtnDown = 0x04;
        private const uint BtnLeft = 0x02;
        private const uint BtnRight = 0x01;

        // Per-frame timing goes to the same console that draws over the
        // picture, so it is off by default: the measurement destroys the thing
        // being measured. Flip on when the question is speed, not gameplay.
        private const bool ShowTiming = false;

        private static uint s_buttons;
        private static bool s_quit;
        private static uint s_unpacedFrames;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            AppHost.WriteString("fami: start\n");

            try
            {
                if (!AppHost.FileExists(RomPath))
                {
                    AppHost.WriteString("fami: no ");
                    AppHost.WriteString(RomPath);
                    AppHost.WriteChar('\n');
                    return 2;
                }

                // Start-up is stepped out loud on purpose. Each of these calls
                // reaches a different part of the core (opcode tables, the
                // cartridge parser, the mapper, the reset vector read), and
                // when one of them dies without an exception — a hang, or a
                // fault the app cannot catch — the last line printed is the
                // only thing that says which.
                MC6502State nes = new MC6502State();
                nes.Init();
                AppHost.WriteString("fami: init\n");

                // Opened, parsed and closed as three visible steps rather than
                // one `using` block. Upstream's parser prints the mapper number
                // as its last act and then builds the mapper object, so a
                // `using` would leave that construction and the stream's
                // disposal inside the same unlit gap.
                FileStream rom = File.OpenRead(RomPath);
                AppHost.WriteString("fami: rom open\n");

                Cartridge cart = Cartridge.Load(rom, nes);
                AppHost.WriteString("fami: cartridge parsed\n");

                rom.Dispose();
                AppHost.WriteString("fami: rom closed\n");

                nes.LoadCartridge(cart);
                AppHost.WriteString("fami: mapper attached\n");

                nes.Reset();
                AppHost.WriteString("fami: reset\n");

                GopVideo video = null;
                if (AppHost.TryGetFramebuffer(
                        out ulong fbBase, out uint fbWidth, out uint fbHeight,
                        out uint fbStride, out uint fbFormat))
                {
                    video = new GopVideo(fbBase, fbWidth, fbHeight, fbStride, fbFormat);
                    AppHost.WriteString("fami: video ");
                    AppHost.WriteUInt(fbWidth);
                    AppHost.WriteChar('x');
                    AppHost.WriteUInt(fbHeight);
                    AppHost.WriteString(" scale ");
                    AppHost.WriteUInt((uint)video.Scale);
                    AppHost.WriteChar('\n');
                }
                else
                {
                    AppHost.WriteString("fami: headless (no framebuffer)\n");
                }

                RunLoop(nes, video);

                AppHost.WriteString("fami: quit\n");
                return 42;
            }
            catch (Exception e)
            {
                // The bare marker goes out before anything is asked of the
                // exception object. Reading Message allocates and dispatches,
                // and if that is what faults, a catch block that leads with it
                // prints nothing at all — leaving a crash that looks identical
                // to a hang.
                AppHost.WriteString("fami: exception caught\n");
                AppHost.WriteString(e.Message ?? "(no message)");
                AppHost.WriteChar('\n');
                return 1;
            }
        }

        // 60 Hz if there is a clock to pace against, otherwise as fast as the
        // machine goes. Unpaced is not a fallback worth hiding: without a time
        // source the emulator would run at whatever speed the host manages,
        // which is wrong but visible, rather than wrong and silent.
        private static void RunLoop(MC6502State nes, GopVideo video)
        {
            bool paced = AppHost.TryGetHpet(out ulong counterAddress, out ulong frequencyHz)
                         && counterAddress != 0 && frequencyHz != 0;

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
                    AppHost.WriteString("fami: HPET present but STOPPED - running unpaced\n");
                    paced = false;
                }
            }

            AppHost.WriteString(paced
                ? "fami: paced 60 Hz (HPET)\n"
                : "fami: unpaced, no timing (VBox: modifyvm --hpet on)\n");

            ulong ticksPerFrame = paced ? frequencyHz / 60UL : 0UL;
            ulong next = paced ? ReadCounter() + ticksPerFrame : 0UL;

            // Timed separately, because "it feels laggy" has two very different
            // causes with opposite fixes: a slow core or a slow blit (pixels to
            // an uncached framebuffer). Input is sampled once per emulated
            // frame, so whichever is slow becomes the input latency directly.
            ulong emulateTicks = 0, blitTicks = 0;
            int framesTimed = 0;
            ulong windowStart = paced ? ReadCounter() : 0;

            int cyclesLeft = 0;
            uint frameNumber = 0;

            while (!s_quit)
            {
                PumpKeyboard();
                nes.Controller[0] = s_buttons;

                ulong t0 = paced ? ReadCounter() : 0;

                cyclesLeft += CyclesPerFrame - (int)(frameNumber++ & 1);
                while (cyclesLeft > 0)
                {
                    cyclesLeft -= (int)nes.Step();
                }

                ulong t1 = paced ? ReadCounter() : 0;
                video?.Blit(nes.Ppu.buffer);
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
                        AppHost.WriteString("fami: frames ");
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

            AppHost.WriteString("fami: frame ");
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
        // mask. Presses and releases both matter — a controller reports what is
        // HELD, so a missed release leaves the character walking forever.
        private static void PumpKeyboard()
        {
            for (int i = 0; i < 32; i++)
            {
                if (AppHost.TryReadKey(out KeyInfo key) != AppServiceStatus.Ok) return;
                if (!key.HasRaw) continue;

                uint mask = MapKey(key.RawMake, key.RawExtended);
                if (mask == 0)
                {
                    // Esc quits; only on press, so the release does not
                    // immediately re-trigger anything later.
                    if (key.RawMake == 0x01 && key.RawDown) s_quit = true;
                    continue;
                }

                if (key.RawDown) s_buttons |= mask;
                else s_buttons &= ~mask;
            }
        }

        // csc requires a Main for OutputType=Exe; never called — the kernel
        // enters at SharpAppEntry via the PE entry point symbol.
        private static int Main() => 0;

        // Set-1 make codes -> NES buttons. Same layout as the TriCNES port, so
        // muscle memory carries between the two: arrows for the D-pad (both the
        // extended cluster and the numeric keypad, so it works without a
        // dedicated arrow block), Z/X for B/A, Enter/Right-Shift for
        // Start/Select.
        private static uint MapKey(byte make, bool extended)
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
