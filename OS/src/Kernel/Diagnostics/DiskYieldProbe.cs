using OS.Hal;
using OS.Kernel.Memory;
using OS.Kernel.Threading;

namespace OS.Kernel.Diagnostics
{
    // Does a disk read leave the CPU to others (plan §11, stage 2)?
    //
    // A worker counts its turns while the boot thread reads a large file. It
    // yields on every turn, so it runs only when the reader is not running:
    // with the old drivers — a whole command with preemption suppressed,
    // polling — the reader never let go between its first and last sector
    // and the count stayed at what preemption ticks gave. With the drivers
    // asleep in IrqWait between issue and completion the worker gets those
    // gaps. Reported with the sleeps the read took and its time, so a read
    // that got slower for it shows.
    internal static unsafe class DiskYieldProbe
    {
        private const string Path = "/EFI/BOOT/BOOTX64.EFI";

        private static volatile bool s_stop;
        private static volatile bool s_done;
        private static ulong s_turns;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void Worker()
        {
            while (!s_stop)
            {
                s_turns++;
                Scheduler.Yield();
            }
            s_done = true;
            Scheduler.Exit();
        }

        public static void Run()
        {
            if (Fs.Current == null) return;
            if (Fs.Current.ReadFile(Path, null, 0, out uint size) < 0 || size == 0)
            {
                Log.Write(LogLevel.Warn, "[diskyield] FAIL no " + Path);
                return;
            }
            void* buffer = NativeArena.Allocate(size);
            if (buffer == null) return;

            s_stop = false;
            s_done = false;
            s_turns = 0;
            if (Scheduler.Spawn(&Worker, 0) == null) return;
            Scheduler.Yield();                      // let it start: one turn
            ulong turnsBefore = s_turns;
            ulong sleepsBefore = IrqWait.Sleeps;

            ulong started = OS.Hal.Timer.Hpet.ReadCounter();
            int read = Fs.Current.ReadFile(Path, (byte*)buffer, (int)size, out _);
            ulong ticks = OS.Hal.Timer.Hpet.ReadCounter() - started;

            ulong turns = s_turns - turnsBefore;
            ulong sleeps = IrqWait.Sleeps - sleepsBefore;
            s_stop = true;
            for (int i = 0; i < 1000 && !s_done; i++) Scheduler.Yield();

            ulong hz = OS.Hal.Timer.Hpet.FrequencyHz;
            bool interrupts = Ahci.InterruptSource >= 0
                || (OS.Hal.Usb.UsbMassStorage.Controller?.InterruptSource ?? -1) >= 0;
            bool ok = read >= 0 && (!interrupts || (sleeps > 0 && turns > 0));

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("[diskyield] read ");
            Console.WriteUInt(size / 1024);
            Console.Write(" KiB in ");
            Console.WriteULong(hz == 0 ? 0 : ticks * 1000 / hz);
            Console.Write(" ms; the other thread ran ");
            Console.WriteULong(turns);
            Console.Write(" times, the reader slept ");
            Console.WriteULong(sleeps);
            Console.Write(interrupts ? " times (interrupts)" : " times (polling)");
            Console.Write(ok ? " PASS" : " FAIL");
            Log.EndLine();
        }
    }
}
