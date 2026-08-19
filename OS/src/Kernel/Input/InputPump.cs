using OS.Hal;
using OS.Kernel.Threading;

namespace OS.Kernel.Input
{
    // Collects keystrokes whether or not anything is reading them.
    //
    // Both keyboards are polled: a PS/2 byte waits in the 8042 and a USB report
    // waits in the xHCI ring until someone asks. While a command runs, nobody
    // asks — the shell is inside the command, not at its prompt — so keys were
    // simply not seen until it finished. Ctrl+C was the visible casualty: it
    // arrived, was noticed after the work it was meant to interrupt had already
    // completed, and looked like a key that did nothing.
    //
    // A thread rather than the timer tick. Draining USB means touching the
    // controller's event ring, which is neither short nor obviously safe in
    // interrupt context; a thread costs one wake-up per interval and can be
    // preempted like anything else.
    internal static unsafe class InputPump
    {
        private const uint PollIntervalMs = 10;   // ~2 polls per keystroke at typing speed

        private static bool s_started;
        private static ulong s_scancodes;
        private static ulong s_breaks;
        private static ulong s_unhandled;
        public static ulong Unhandled => s_unhandled;

        // The handler runs on a thread of its own. Windows delivers console
        // control events that way, and the reason showed up the first time we
        // did it inline: PowerShell's handler blocks while it stops the running
        // pipeline, so the pump never came back to poll. The first Ctrl+C
        // worked and every one after it was never seen — the keyboard had
        // stopped being read by the thread that read it.
        private static Event s_ctrlSignal = null!;

        public static ulong Scancodes => s_scancodes;
        public static ulong Breaks => s_breaks;

        public static void Start()
        {
            if (s_started) return;
            s_started = true;
            s_ctrlSignal = new Event(manualReset: false, initialState: false);
            unsafe
            {
                // Spawned as a hosted thread, not a bare kernel one: that path
                // gives it a TEB, which the runtime needs before it will accept
                // the thread at all. Without it, calling the registered handler
                // threw EEException out of the callback prologue and killed the
                // shell instead of cancelling the command.
                Scheduler.SpawnHosted(&CtrlDeliveryEntry, null, 64 * 1024);
                Scheduler.Spawn(&PumpEntry, 64 * 1024);
            }
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static uint CtrlDeliveryEntry(void* unused)
        {
            // Attaching is deferred to the first delivery, not done at start.
            // This thread is created while the machine is still booting, and
            // introducing it to a runtime that does not exist yet went straight
            // through SetupThread into a ThreadStore that had not been created:
            // a null read during boot, long before any Ctrl+C.
            bool attached = false;

            while (true)
            {
                s_ctrlSignal.Wait();

                // Bracketed because "the handler hung" and "the handler
                // returned and the shell hung afterwards" need completely
                // different fixes, and from outside they look the same: a
                // prompt that never comes back.
                // No handler means no runtime worth talking to yet — a key
                // pressed before the shell is up has nowhere to go.
                if (OS.PAL.SharpOSHost.ConsoleWin32.CtrlHandlerCount == 0) continue;

                if (!attached)
                {
                    attached = SharpOSHostRuntime.AttachCurrentThread() != 0;
                    OS.Hal.Console.WriteLine(attached
                        ? "[ctrlc] delivery thread attached to runtime"
                        : "[ctrlc] delivery thread NOT attached — Ctrl+C will not work");
                    if (!attached) continue;
                }

                // Silent on success. The two brackets around this call were
                // what told "the handler hung" apart from "it returned and the
                // shell hung after" — worth every line while that was the
                // question, worth none once it is answered.
                if (!OS.PAL.SharpOSHost.ConsoleWin32.RaiseCtrlEvent(0 /* CTRL_C_EVENT */))
                    s_unhandled++;
            }
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void PumpEntry()
        {
            while (true)
            {
                // Bounded per pass so a stuck controller cannot hold the CPU:
                // a real burst of typing is a handful of codes per interval.
                for (int i = 0; i < 64; i++)
                {
                    if (!ScancodeSource.TryReadHardware(out byte sc)) break;
                    s_scancodes++;
                    if (DetectBreak(sc)) s_breaks++;
                    ScancodeSource.Push(sc);
                }

                Scheduler.Sleep(PollIntervalMs);
            }
        }

        // A detector for exactly one combination, deliberately separate from
        // the console's decoder. That decoder is stateful and owned by whoever
        // reads keys; feeding it a second time from here would corrupt its idea
        // of which modifiers are down. Tracking one key is cheap enough that
        // sharing was not worth the coupling.
        private const byte CtrlMake  = 0x1D;
        private const byte CtrlBreak = 0x9D;
        private const byte CMake     = 0x2E;
        private const byte Extended  = 0xE0;

        private static bool s_ctrlDown;
        private static bool s_sawExtended;

        private static bool DetectBreak(byte sc)
        {
            if (sc == Extended) { s_sawExtended = true; return false; }

            bool extended = s_sawExtended;
            s_sawExtended = false;

            // Both control keys report 0x1D; the right one is prefixed. Either
            // is a control key for this purpose.
            if (sc == CtrlMake)  { s_ctrlDown = true;  return false; }
            if (sc == CtrlBreak) { s_ctrlDown = false; return false; }

            if (sc == CMake && s_ctrlDown && !extended)
            {
                // Raise it here, at the moment the key arrives, rather than
                // when someone next reads: being timely is the entire point.
                // The key itself still goes into the ring — the line editor
                // draws "^C" from it, and dropping it here would fix
                // cancellation by breaking the echo.
                s_ctrlSignal.Set();
                return true;
            }

            return false;
        }
    }
}
