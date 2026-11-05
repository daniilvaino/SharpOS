using OS.Kernel.Threading;

namespace OS.Kernel.Process
{
    /// <summary>
    /// Ctrl+C: SIGINT to the group that owns the terminal (TerminalOwner), as
    /// a Unix terminal sends it to its foreground process group.
    /// </summary>
    /// <remarks>
    /// Each process of the group either ends with 130 (128 + SIGINT) — the
    /// default — or, when it listens (Console.CancelKeyPress,
    /// PosixSignalRegistration), gets the signal: a thread of its own waits for
    /// it (ProcessOpInterrupt) and runs the handlers, which may cancel the end.
    /// A listener still busy with the previous Ctrl+C is ended: a hung handler
    /// does not make the program unstoppable.
    ///
    /// A process that reads Ctrl+C as a key (Console.TreatControlCAsInput: the
    /// shell's line editor, a full-screen program), as with ISIG off on Unix,
    /// gets no signal, and the key goes on to be read; the others of its group
    /// still get the signal — a program the launcher started in its own group
    /// stops, the launcher does not.
    /// </remarks>
    internal static unsafe class TerminalInterrupt
    {
        /// <summary>Exit code of a process ended by Ctrl+C.</summary>
        public const int InterruptedExitCode = 130;

        /// <summary>What became of a Ctrl+C.</summary>
        public enum Outcome
        {
            /// <summary>No process owns the terminal, or one waits for a hosted program: the hosted runtime's, if anyone's.</summary>
            NoProcess,
            /// <summary>A signal, and the key goes no further.</summary>
            Signal,
            /// <summary>A process of the owning group reads it as a key: it goes on (the others had the signal).</summary>
            Key,
        }

        /// <summary>Ctrl+C was pressed.</summary>
        public static Outcome Raise()
        {
            uint group = TerminalOwner.Foreground;
            if (group == 0) group = SessionGroup();
            Outcome outcome = Raise(group, out int ended, out int signalled);
            // A line a Ctrl+C: what it did is otherwise only seen as its effect.
            OS.Hal.Console.WriteLine("[ctrlc] group=" + group.ToString() + " ended=" + ended.ToString()
                + " signalled=" + signalled.ToString() + " key=" + (outcome != Outcome.Signal ? "on" : "spent")
                + (outcome == Outcome.NoProcess ? " (no process: hosted runtime)" : ""));
            return outcome;
        }

        // Nobody took the terminal: it is the session's, the group of the
        // program the kernel started (the shell, the launcher), as a terminal
        // is its login shell's until a job takes it; 0 when there is none.
        private static uint SessionGroup()
        {
            for (int i = 0; i < AppProcesses.MaxRunning; i++)
            {
                AppProcess p = AppProcesses.InSlot(i);
                if (p != null && p.LauncherId == 0 && !p.KillRequested && p.ExitedWord == 0) return p.Group;
            }
            return 0;
        }

        private static Outcome Raise(uint group, out int ended, out int signalled)
        {
            ended = signalled = 0;
            if (group == 0) return Outcome.NoProcess;

            int count = 0;
            bool aReader = false, hosted = false;
            AppProcess[] members = new AppProcess[AppProcesses.MaxRunning];
            Preemption.Suppress();
            for (int i = 0; i < AppProcesses.MaxRunning; i++)
            {
                AppProcess p = AppProcesses.InSlot(i);
                if (p == null || p.Group != group || p.KillRequested || p.ExitedWord != 0) continue;
                if (p.InHostedRun) hosted = true;
                else if (p.ControlCIsInput) aReader = true;
                else members[count++] = p;
            }
            Preemption.Allow();
            if (hosted) return Outcome.NoProcess;
            if (count == 0) return aReader ? Outcome.Key : Outcome.NoProcess;

            for (int i = 0; i < count; i++)
            {
                AppProcess p = members[i];
                Preemption.Suppress();
                bool deliver = p.InterruptListening && p.InterruptsHandled == p.InterruptCount;
                if (deliver) p.InterruptCount++;
                Preemption.Allow();

                if (deliver)
                {
                    signalled++;
                    fixed (uint* word = &p.InterruptCount)
                        AddressWait.WakeByAddressAll(word);
                }
                else
                {
                    ended++;
                    AppServiceBuilder.RequestEnd(p, InterruptedExitCode, failed: true);
                }
            }
            return aReader ? Outcome.Key : Outcome.Signal;
        }

        /// <summary>
        /// The listener's wait: back when Ctrl+C came after the
        /// <paramref name="handled"/>-th, with the count now; false when the
        /// waiting thread is being ended.
        /// </summary>
        public static bool Wait(AppProcess self, uint handled, out uint now)
        {
            Preemption.Suppress();
            self.InterruptsHandled = handled;
            Preemption.Allow();
            while (true)
            {
                now = self.InterruptCount;
                if (now != handled) return true;
                Thread current = Scheduler.Current;
                if (current != null && current.KillRequested) return false;
                fixed (uint* word = &self.InterruptCount)
                    AddressWait.WaitOnAddress(word, &handled, 4, 0xFFFFFFFFu);
            }
        }
    }
}
