using System;
using System.IO;

namespace TriCNES
{
    // Headless driver for the AOT proving build.
    //
    // Answers three questions in one run, all of which have to be "yes" before
    // the SharpOS port is worth writing:
    //   1. does the core compile and link under NativeAOT (no reflection, no
    //      dynamic code, nothing the trimmer has to guess about)?
    //   2. does it emulate — i.e. does a real ROM produce a moving picture
    //      rather than a blank or constant frame?
    //   3. is the frame buffer reachable as plain ints, which is all the
    //      SharpOS front end will have?
    //
    // Output is deliberately a checksum per frame rather than an image: it is
    // comparable between machines and between the two builds, and it fails
    // loudly if the picture is static.
    internal static class AotProbe
    {
        private static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: TriCNESAot <rom.nes> [frames]");
                return 2;
            }

            string romPath = args[0];
            int frames = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 60;

            if (!File.Exists(romPath))
            {
                Console.WriteLine($"ROM not found: {romPath}");
                return 2;
            }

            Console.WriteLine($"ROM   : {romPath} ({new FileInfo(romPath).Length} bytes)");

            Emulator emu = new Emulator();
            Cartridge cart = new Cartridge(romPath);
            emu.Cart = cart;

            // Both back-references, exactly as the upstream GUI wires them.
            // The mapper reaches the console through Cart.Emu (it reads the
            // 72-pin connector to decide whether the cartridge even has power),
            // so leaving Emu null crashes on the first emulated cycle.
            cart.Emu = emu;
            cart.MapperChip.Cart = cart;

            Console.WriteLine($"mapper: {cart.MemoryMapper} (sub {cart.SubMapper})");

            emu.Reset();

            uint previous = 0;
            int moving = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                emu._CoreFrameAdvance();

                uint crc = Checksum(emu.Screen.Bits);
                if (frame > 0 && crc != previous) moving++;
                previous = crc;

                // Early frames are the interesting ones (reset, first draws);
                // after that a sample is enough to prove it keeps running.
                if (frame < 5 || frame % 20 == 0)
                    Console.WriteLine($"frame {frame,4}: crc=0x{crc:X8}");
            }

            Console.WriteLine($"frames={frames} changed={moving}");

            // A picture that never changes means the CPU is not really running,
            // which compiles and "works" right up until someone looks at it.
            if (moving == 0)
            {
                Console.WriteLine("FAIL: frame never changed");
                return 1;
            }

            Console.WriteLine("OK");
            return 0;
        }

        private static uint Checksum(int[] pixels)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = (uint)pixels[i];
                hash = (hash ^ (p & 0xFF)) * 16777619u;
                hash = (hash ^ ((p >> 8) & 0xFF)) * 16777619u;
                hash = (hash ^ ((p >> 16) & 0xFF)) * 16777619u;
                hash = (hash ^ ((p >> 24) & 0xFF)) * 16777619u;
            }
            return hash;
        }
    }
}
