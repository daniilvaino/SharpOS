using System;
using System.Runtime;
using ManagedDoom;
using SharpOS.AppSdk;

namespace DoomApp
{
    // The playable ManagedDoom entry (step143): args -> config ->
    // GameContent (WAD off the ESP) -> Doom core with GopVideo (transposing
    // GOP blit, step143) + SharpUserInput (raw PS/2 make/break events via
    // the key service) + HPET-paced 35 Hz game loop. Runs until the user
    // quits from the menu (Esc -> Quit -> Y). Headless boots degrade to the
    // unpaced sim-only loop (NullVideo, no input).
    //
    // Exit codes: 42 = clean quit, 1 = managed exception (printed).
    internal static unsafe class Entry
    {

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            AppHost.WriteString("doom: start\n");

            try
            {
                var args = new CommandLineArgs(new string[0]);

                var configPath = ConfigUtilities.GetConfigPath();
                var config = new Config(configPath);
                AppHost.WriteString("doom: config ok\n");

                var content = new GameContent(args);
                AppHost.WriteString("doom: content ok (WAD parsed)\n");

                // GOP framebuffer -> real video (step143); headless boots keep
                // NullVideo (video == null) and stay sim-only.
                GopVideo video = null;
                if (AppHost.TryGetFramebuffer(
                        out ulong fbBase, out uint fbWidth, out uint fbHeight,
                        out uint fbStride, out uint fbFormat))
                {
                    video = new GopVideo(config, content, fbBase, fbWidth, fbHeight, fbStride, fbFormat);
                    AppHost.WriteString("doom: video ");
                    AppHost.WriteUInt(fbWidth);
                    AppHost.WriteChar('x');
                    AppHost.WriteUInt(fbHeight);
                    AppHost.WriteString(fbFormat == 0 ? " RGBX\n" : " BGRX\n");
                }
                else
                {
                    AppHost.WriteString("doom: no framebuffer, headless\n");
                }

                var input = new SharpUserInput(config);

                var doom = new Doom(args, config, content, video, null, null, input);
                AppHost.WriteString("doom: core up, running\n");

                // 35 Hz pacing off the HPET (step143). Without a time source
                // (headless/pre-EBS) the loop runs unpaced, as before. The
                // counter read goes through Stopwatch.ReadCounter — a
                // NoInlining MMIO reader (ILC hoists non-volatile MMIO reads
                // out of spin loops).
                bool paced = AppHost.TryGetHpet(out _, out ulong hpetHz);
                ulong ticksPerFrame = paced ? hpetHz / 35 : 0;
                ulong nextFrame = paced
                    ? System.Diagnostics.Stopwatch.ReadCounter() + ticksPerFrame
                    : 0;

                for (; ; )
                {
                    input.PumpEvents(doom);

                    UpdateResult result = doom.Update();
                    if (result == UpdateResult.Completed)
                        break; // menu quit

                    if (video != null)
                        video.Render(doom, Fixed.One);

                    if (paced)
                    {
                        while (System.Diagnostics.Stopwatch.ReadCounter() < nextFrame)
                        {
                        }
                        nextFrame += ticksPerFrame;

                        // Recover from long stalls (map load, wipe) instead of
                        // fast-forwarding a backlog of frames.
                        ulong now = System.Diagnostics.Stopwatch.ReadCounter();
                        if (now > nextFrame + ticksPerFrame * 35)
                            nextFrame = now + ticksPerFrame;
                    }
                }

                AppHost.WriteString("doom: quit from menu\n");
                return 42;
            }
            catch (Exception e)
            {
                // No GetType() on the app std yet — message + stack trace
                // (populated by the kernel-shared EH engine, step140).
                AppHost.WriteString("doom: EXCEPTION: ");
                AppHost.WriteString(e.Message ?? "(no message)");
                AppHost.WriteChar('\n');
                string trace = e.StackTrace;
                if (trace != null)
                {
                    AppHost.WriteString(trace);
                    AppHost.WriteChar('\n');
                }
                return 1;
            }
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        // csc requires a Main for OutputType=Exe; never called — the real
        // entry is SharpAppBootstrap via /ENTRY (same as the other apps).
        private static int Main() => 0;
    }
}
