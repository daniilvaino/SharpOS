using System;
using System.Runtime;
using ManagedDoom;
using SharpOS.AppSdk;

namespace DoomApp
{
    // P2 headless bring-up entry for the ManagedDoom freestanding build.
    // Boots the real game pipeline — args -> config -> GameContent (WAD off
    // the ESP via the AppHost read service) -> Doom core with Null
    // video/sound/music/input — and ticks the opening sequence (attract-mode
    // demo = full game sim) for a few seconds of game time. No rendering yet:
    // the GOP blit + PS/2 input shims are the next milestone; this stage
    // proves WAD parsing + statics + the whole sim on the app std.
    //
    // Exit codes: 42 = ticked to the end, 1 = managed exception (printed).
    internal static unsafe class Entry
    {
        // ~5 seconds of game time at 35 tics/sec.
        private const int TicsToRun = 175;

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

                var doom = new Doom(args, config, content, null, null, null, null);
                AppHost.WriteString("doom: core up, ticking\n");

                int tic = 0;
                for (; tic < TicsToRun; tic++)
                {
                    UpdateResult result = doom.Update();
                    if (result == UpdateResult.Completed)
                        break;

                    if (tic % 35 == 0)
                    {
                        AppHost.WriteString("doom: tic ");
                        AppHost.WriteUInt((uint)tic);
                        AppHost.WriteChar('\n');
                    }
                }

                AppHost.WriteString("doom: ticked ");
                AppHost.WriteUInt((uint)tic);
                AppHost.WriteString(" — sim alive\n");
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
