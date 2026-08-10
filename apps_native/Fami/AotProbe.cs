using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Fami.Core;
using Fami.Core.CPU;

namespace Fami
{
    // Headless driver for the grown-up .NET build. Runs a ROM for a fixed
    // number of frames with no window, no sound and no input, and prints a
    // checksum of each screen it produces.
    //
    // Two jobs. Correctness: the checksums must match between this build and
    // the SharpOS one, which turns "the port works" from a judgement about a
    // photograph of a monitor into a comparison of numbers. Speed: the
    // milliseconds per frame here are the budget SharpOS is measured against,
    // on the same machine, so a slow build can be told apart from a slow
    // emulator.
    internal static class AotProbe
    {
        // One frame of NTSC master cycles, from upstream's own frame loop
        // (Interface/Main.cs). Duplicated rather than referenced because that
        // file is the SDL front-end, which is exactly what we do not compile.
        private const int CyclesPerFrame = 89342;

        private static int Main(string[] args)
        {
            string rom = args.Length > 0 ? args[0] : "game.nes";
            int frames = args.Length > 1 ? int.Parse(args[1]) : 600;

            if (!File.Exists(rom))
            {
                Console.WriteLine($"probe: no such ROM: {rom}");
                return 2;
            }

            var nes = new MC6502State();
            nes.Init();

            using (var f = File.OpenRead(rom))
            {
                nes.LoadCartridge(Cartridge.Load(f, nes));
            }

            nes.Reset();

            // Optional third argument: hold Start briefly, once a second.
            //
            // Without it a frozen checksum proves nothing — a title screen
            // waiting for a button press looks exactly like a hung emulator.
            // With it, a picture that still never changes is a machine that has
            // genuinely stopped responding.
            bool pressStart = args.Length > 2;
            uint button = args.Length > 2 && args[2] == "a" ? 0x80u : 0x10u;

            uint lastTimerBox = 0;
            var timerChanges = new System.Collections.Generic.List<int>();

            var sw = Stopwatch.StartNew();
            int cyclesLeft = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                // One press, not a repeated one: Start also pauses, so
                // holding it down once a second would start the game and then
                // toggle the pause for the rest of the run.
                if (pressStart) nes.Controller[0] = (frame >= 200 && frame < 208) ? button : 0;

                cyclesLeft += CyclesPerFrame - (frame % 2);
                while (cyclesLeft > 0)
                {
                    cyclesLeft -= (int)nes.Step();
                }

                // Watch the TIME digits in the Super Mario Bros status bar and
                // record how many frames pass between changes.
                //
                // "The clock ticks faster than seconds" is either the game
                // being itself or the emulator running fast, and eyeballing a
                // monitor cannot tell those apart. Counting frames can: the
                // answer is a number of frames per tick, which is a property of
                // the ROM and is the same on any correct emulator.
                uint timerBox = 0;
                for (int y = 24; y < 32; y++)
                    for (int x = 200; x < 232; x++)
                        timerBox = (timerBox ^ nes.Ppu.buffer[(y * 256) + x]) * 16777619u;

                if (frame > 0 && timerBox != lastTimerBox) timerChanges.Add(frame);
                lastTimerBox = timerBox;

                // Print sparsely: every frame would make the output itself a
                // measurable part of the runtime.
                if (frame % 60 == 0 || frame == frames - 1)
                {
                    Console.WriteLine($"frame {frame,5}  crc {Checksum(nes.Ppu.buffer):X8}");
                }
            }
            sw.Stop();

            // Dump the last frame as a PPM next to the ROM. The checksums say
            // whether the picture changed; they say nothing about whether it is
            // the right picture. Comparing this against what SharpOS puts on
            // screen separates "the emulator cannot run this cartridge" from
            // "our blitter draws it wrong" — the two produce identical
            // symptoms and opposite fixes.
            DumpPpm(nes.Ppu.buffer, "frame.ppm");

            // Colour histogram, for when the image is not at hand: a picture
            // that is one colour plus a handful of pixels is a PPU that never
            // rendered, whatever it looks like.
            var histogram = new System.Collections.Generic.Dictionary<uint, int>();
            foreach (uint pixel in nes.Ppu.buffer)
            {
                histogram.TryGetValue(pixel, out int seen);
                histogram[pixel] = seen + 1;
            }
            Console.WriteLine($"distinct colours in last frame: {histogram.Count}");
            foreach (var entry in histogram.OrderByDescending(e => e.Value).Take(5))
            {
                Console.WriteLine($"  {entry.Key:X6}  {entry.Value} px");
            }

            // Intervals between clock ticks, ignoring the title screen: the
            // demo has to be playing before there is a clock at all.
            var intervals = new System.Collections.Generic.List<int>();
            for (int i = 1; i < timerChanges.Count; i++)
                if (timerChanges[i] > 400) intervals.Add(timerChanges[i] - timerChanges[i - 1]);
            if (intervals.Count > 2)
            {
                intervals.Sort();
                int median = intervals[intervals.Count / 2];
                Console.WriteLine($"timer ticks every {median} frames = {median / 60.0988:F2} s at true NTSC rate");
            }

            double msPerFrame = sw.Elapsed.TotalMilliseconds / frames;
            Console.WriteLine($"probe: {frames} frames in {sw.ElapsedMilliseconds} ms " +
                              $"= {msPerFrame:F2} ms/frame ({1000.0 / msPerFrame:F1} fps)");
            return 42;
        }

        // Fami packs its palette as 0x00BBGGRR (Ppu.palScreen), so red comes
        // out of the low byte. Same order the SharpOS blitter has to undo.
        private static void DumpPpm(uint[] buffer, string path)
        {
            using var file = File.Create(path);
            using var writer = new StreamWriter(file);
            writer.Write("P3\n256 240\n255\n");
            for (int i = 0; i < buffer.Length; i++)
            {
                uint pixel = buffer[i];
                writer.Write($"{pixel & 0xFF} {(pixel >> 8) & 0xFF} {(pixel >> 16) & 0xFF}\n");
            }
        }

        // FNV-1a over the frame. Same function as the TriCNES probe uses, so
        // the two emulators' outputs can be compared frame for frame.
        private static uint Checksum(uint[] buffer)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < buffer.Length; i++)
            {
                uint pixel = buffer[i];
                for (int b = 0; b < 4; b++)
                {
                    hash = (hash ^ ((pixel >> (b * 8)) & 0xFF)) * 16777619u;
                }
            }
            return hash;
        }
    }
}
