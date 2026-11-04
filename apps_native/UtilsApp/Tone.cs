using System;
using SharpOS.AppSdk;

namespace UtilsApp
{
    // tone [HZ [MS]]: a sine wave on the sound card (step246) — 440 Hz for a
    // second by default. The first program that makes a sound, and the check
    // that the whole path plays: the samples, the kernel's queue, the card.
    //
    // beep [HZ [MS]] (step248): the PC speaker and the sound card at once —
    // 800 Hz for 200 ms, Console.Beep's default. Whichever of the two the
    // machine has is heard; it says which it found.
    internal static class Tone
    {
        private const string ToneUsage = "usage: tone [HZ [MS]]   a sine, 440 Hz for 1000 ms by default";
        private const string BeepUsage = "usage: beep [HZ [MS]]   PC speaker and sound card, 800 Hz for 200 ms by default";

        public static int Run(string[] args)
        {
            if (!Parse(args, ToneUsage, 440, 1000, out double hz, out int ms, out int exit)) return exit;
            if (Audio.SampleRate == 0)
            {
                Console.Error.WriteLine("tone: no sound card");
                return 1;
            }
            Play(hz, ms);
            return 0;
        }

        public static int Beep(string[] args)
        {
            if (!Parse(args, BeepUsage, 800, 200, out double hz, out int ms, out int exit)) return exit;
            bool speaker = Audio.Speaker((int)Math.Round(hz), ms);
            bool card = Audio.SampleRate != 0;
            if (!speaker && !card)
            {
                Console.Error.WriteLine("beep: no PC speaker and no sound card");
                return 1;
            }
            if (card) Play(hz, ms);
            else System.Threading.Thread.Sleep(ms);
            return 0;
        }

        private static bool Parse(string[] args, string usage, double defaultHz, int defaultMs,
                                  out double hz, out int ms, out int exit)
        {
            hz = defaultHz;
            ms = defaultMs;
            exit = 0;
            if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h" || args[0] == "-?"))
            {
                Console.WriteLine(usage);
                return false;
            }
            if (args.Length > 0 && (!double.TryParse(args[0], out hz) || hz <= 0 || hz > 20000)
                || args.Length > 1 && (!int.TryParse(args[1], out ms) || ms <= 0)
                || args.Length > 2)
            {
                Console.Error.WriteLine(usage);
                exit = 2;
                return false;
            }
            return true;
        }

        // A block at a time, as fast as the queue takes it; a 5 ms fade at
        // each end, so the start and the stop do not click. Back when it has
        // been heard, not when it was queued.
        private static void Play(double hz, int ms)
        {
            int rate = Audio.SampleRate;
            int frames = (int)((long)rate * ms / 1000);
            int fade = rate / 200;
            var block = new short[2 * 1024];
            double step = 2 * Math.PI * hz / rate;
            int done = 0;
            while (done < frames)
            {
                int count = Math.Min(1024, frames - done);
                for (int i = 0; i < count; i++)
                {
                    int n = done + i;
                    double gain = Math.Min(1.0, Math.Min(n, frames - 1 - n) / (double)fade);
                    short s = (short)(Math.Sin(step * n) * 12000 * gain);
                    block[2 * i] = s;
                    block[2 * i + 1] = s;
                }
                int sent = 0;
                while (sent < 2 * count)
                {
                    int taken = Audio.Write(new ReadOnlySpan<short>(block, sent, 2 * count - sent));
                    sent += taken;
                    if (taken == 0) System.Threading.Thread.Sleep(10);
                }
                done += count;
            }
            while (Audio.QueuedFrames > 0) System.Threading.Thread.Sleep(10);
        }
    }
}
