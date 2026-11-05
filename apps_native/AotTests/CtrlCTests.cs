using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AotTests
{
    // Ctrl+C (release 0.1 §10), one case a run: --ctrl-c CASE. Each prints
    // "[ctrlc] ready CASE" when the next Ctrl+C is wanted — the test driver
    // presses it then (QMP) — and the exit code tells the rest:
    //   spin, sleep, read  no handler: the kernel ends it with 130 out of a
    //                      busy loop, a sleep, a read of the terminal
    //   handled            CancelKeyPress cancels: exits 7 on its own
    //   uncancelled        CancelKeyPress runs, does not cancel: 130
    //   hung               the handler never returns: the second Ctrl+C ends it (130)
    //   posix              PosixSignalRegistration cancels, then is disposed:
    //                      the second Ctrl+C ends it (130)
    //   input              TreatControlCAsInput: ReadKey gets Ctrl+C, exits 9
    //   winch              SIGWINCH, without a key: exits 11 (RunWinch)
    internal static partial class AppEntry
    {
        private static volatile int s_ctrlCSeen;

        private static void Ready(string name) => Console.Write("[ctrlc] ready " + name + "\n");

        private static int RunCtrlC(string name)
        {
            switch (name)
            {
                case "spin":
                    Ready(name);
                    while (true) s_ctrlCSeen++;
                case "sleep":
                    Ready(name);
                    while (true) Thread.Sleep(50);
                case "read":
                    Ready(name);
                    Console.ReadKey(true);
                    Console.Write("[ctrlc] read returned\n");
                    return 1;
                case "handled":
                    Console.CancelKeyPress += (_, e) => { e.Cancel = true; s_ctrlCSeen = 1; };
                    Ready(name);
                    return WaitSeen(1) ? 7 : 2;
                case "uncancelled":
                    Console.CancelKeyPress += (_, e) => Console.Write("[ctrlc] handler ran, not cancelling\n");
                    Ready(name);
                    WaitSeen(1);
                    return 2;
                case "hung":
                    Console.CancelKeyPress += (_, e) =>
                    {
                        e.Cancel = true;
                        Ready("hung-again");
                        while (true) Thread.Sleep(50);
                    };
                    Ready(name);
                    WaitSeen(1);
                    return 2;
                case "posix":
                    var registration = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                    {
                        ctx.Cancel = true;
                        s_ctrlCSeen = 1;
                    });
                    Ready(name);
                    if (!WaitSeen(1)) return 2;
                    registration.Dispose();
                    Console.Write("[ctrlc] posix handled, registration disposed\n");
                    Ready("posix-again");
                    WaitSeen(2);
                    return 3;
                case "input":
                    Console.TreatControlCAsInput = true;
                    Ready(name);
                    ConsoleKeyInfo key = Console.ReadKey(true);
                    Console.Write("[ctrlc] read key " + ((int)key.KeyChar).ToString() + " " + key.Key.ToString() + " " + key.Modifiers.ToString() + "\n");
                    return key.KeyChar == '\u0003' && key.Key == ConsoleKey.C && key.Modifiers == ConsoleModifiers.Control ? 9 : 4;
                case "winch":
                    return RunWinch();
                default:
                    Console.Write("[ctrlc] no case " + name + "\n");
                    return 5;
            }
        }

        // SIGWINCH, no key wanted: the program's own side panel narrows the
        // main one and closing it widens it back — a signal each time, and
        // the width read in the handler is the new one. Exits 11.
        private static int RunWinch()
        {
            int before = Console.WindowWidth;
            int seenWidth = 0;
            using var registration = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ =>
            {
                seenWidth = Console.WindowWidth;
                s_ctrlCSeen++;
            });
            if (SharpOS.AppSdk.AppHost.Terminal.SidePanel(30) <= 0)
            {
                Console.Write("[winch] no side panel\n");
                return 6;
            }
            bool narrowed = WaitSeen(1);
            int narrow = seenWidth;
            SharpOS.AppSdk.AppHost.Terminal.SidePanel(0);
            bool widened = WaitSeen(2);
            Console.Write("[winch] width " + before.ToString() + " -> " + narrow.ToString() + " -> " + seenWidth.ToString()
                + " signals=" + s_ctrlCSeen.ToString() + "\n");
            return narrowed && widened && narrow < before && seenWidth == before ? 11 : 6;
        }

        // Up to 30 s for the handler; the driver presses within one.
        private static bool WaitSeen(int value)
        {
            for (int i = 0; i < 600; i++)
            {
                if (s_ctrlCSeen >= value) return true;
                Thread.Sleep(50);
            }
            return false;
        }
    }
}
