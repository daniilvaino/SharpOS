using OS.Hal;
using OS.Hal.Timer;
using System.Threading;
using System.Threading.Tasks;

namespace OS.Kernel.Threading
{
    // Do Tasks work on the kernel scheduler?
    //
    // Terminal.Gui does not use tasks for computation; it uses them to keep
    // background loops alive while the interface waits for input:
    //
    //     Task.Run (ProcessInputResultQueue, token)
    //     Task.Delay (100, token).Wait (token)
    //
    // So this checks exactly that shape, and nothing grander: does the body run
    // on a thread of its own, does Wait return when it finishes, does Delay
    // actually take the time it promises, and does a cancelled token break a
    // wait instead of hanging it.
    internal static class TaskProbe
    {
        private static volatile int s_ran;
        private static volatile int s_stage;
        private static volatile bool s_slow;

        public static void Run()
        {
            Console.Write("[task] ");

            if (!ThreadBackend.IsAvailable)
            {
                Console.WriteLine("SKIP no thread backend installed");
                return;
            }

            bool ok = RunsOnItsOwnThread() && WaitReturnsAfterCompletion()
                      && DelayTakesTime() && CancelBreaksWait() && AwaitResumes();

            Console.WriteLine(ok ? " PASS" : " FAIL");
        }

        // The body must not run inline: every caller in Terminal.Gui passes a
        // loop that never returns, so an inline Task.Run would hang the caller
        // rather than start anything.
        private static bool RunsOnItsOwnThread()
        {
            s_ran = 0;
            s_slow = false;

            Task t = Task.Run(() =>
            {
                Scheduler.Sleep(30);      // still running while we look
                s_slow = true;
                s_ran = 1;
            });

            bool startedSeparately = !s_slow;   // we got here before it finished
            t.Wait();

            Console.Write("own-thread=");
            Console.Write(startedSeparately && s_ran == 1 ? "ok " : "FAIL ");
            return startedSeparately && s_ran == 1;
        }

        private static bool WaitReturnsAfterCompletion()
        {
            s_ran = 0;
            Task t = Task.Run(() => { Scheduler.Sleep(20); s_ran = 7; });
            t.Wait();

            bool ok = s_ran == 7 && t.IsCompleted;
            Console.Write("wait=");
            Console.Write(ok ? "ok " : "FAIL ");
            return ok;
        }

        // Measured against the HPET rather than trusted: a Delay that returns
        // immediately would make every polling loop in the library a spin.
        private static bool DelayTakesTime()
        {
            ulong hz = Hpet.FrequencyHz;
            if (hz == 0) { Console.Write("delay=SKIP "); return true; }

            ulong start = Hpet.ReadCounter();
            Task.Delay(50).Wait();
            ulong elapsedMs = (Hpet.ReadCounter() - start) * 1000 / hz;

            bool ok = elapsedMs >= 40 && elapsedMs <= 400;
            Console.Write("delay=");
            Console.WriteUInt((uint)elapsedMs);
            Console.Write(ok ? "ms " : "ms FAIL ");
            return ok;
        }

        // A cancelled wait has to come back. The library cancels these on exit,
        // and a wait that ignores it would hang the shutdown instead.
        private static bool CancelBreaksWait()
        {
            var cts = new CancellationTokenSource();
            cts.Cancel();

            bool threw = false;
            try { Task.Delay(5000).Wait(cts.Token); }
            catch (OperationCanceledException) { threw = true; }

            Console.Write("cancel=");
            Console.Write(threw ? "ok" : "FAIL");
            return threw;
        }

        // async/await, the half that compiling cannot prove.
        //
        // The compiler rewrites the method below into a state machine; the stage
        // counter only passes 1 if the continuation resumed after an await. The
        // second await is not padding: a machine that resumes a COPY of itself
        // gets the first one right and loses everything after it.
        private static bool AwaitResumes()
        {
            s_stage = 0;

            // Bounded and caught, both deliberately. A machine that never
            // resumes leaves this task unfinished forever, and a faulted one
            // rethrows out of Wait with nothing above to catch it, which takes
            // the whole boot down instead of reporting a failed check.
            Task t = RunAsync();
            for (int waited = 0; waited < 300 && !t.IsCompleted; waited++)
                Scheduler.Sleep(10);

            string? failure = null;
            try { if (t.IsCompleted) t.Wait(); }
            catch (System.Exception ex) { failure = ex.Message; }

            bool ok = s_stage == 3 && t.IsCompleted && failure == null;
            Console.Write(" async=");
            Console.WriteUInt((uint)s_stage);
            Console.Write("/3");
            if (!t.IsCompleted) Console.Write(" never-completed");
            if (failure != null) { Console.Write(" threw: "); Console.Write(failure); }
            if (!ok) Console.Write(" FAIL");
            return ok;
        }

        private static async Task RunAsync()
        {
            s_stage = 1;
            await Task.Delay(10);
            s_stage = 2;                 // reached only after a real resumption
            await Task.Delay(10);
            s_stage = 3;
        }
    }
}
