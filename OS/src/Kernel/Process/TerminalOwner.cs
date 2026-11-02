using OS.Kernel.Threading;

namespace OS.Kernel.Process
{
    /// <summary>
    /// Who the terminal's input belongs to: one process group, as tcsetpgrp on
    /// Unix. A program starts in its starter's group, so a parent and a child
    /// it waits for share the terminal; the shell opens a job — a group of its
    /// own for the stages of a pipeline — and hands the terminal to it.
    /// </summary>
    /// <remarks>
    /// A read by anyone else answers "nothing", and the keys wait for the
    /// owner. There is no stop signal (SIGTTIN): a program not owning the
    /// terminal just sees no keys.
    ///
    /// The groups that handed the terminal on are remembered in order. When
    /// the owning group has no process left, the terminal goes back down that
    /// order to the first with one; with none left it has no owner and goes
    /// to whoever reads. So a shell killed halfway, or a program that never
    /// gave the terminal back, does not leave the keys with nobody.
    /// </remarks>
    internal static class TerminalOwner
    {
        // Above any process id: a process's first group is its own id.
        private const uint FirstJobGroup = 0x80000000;

        private const int Depth = 16;
        private static readonly uint[] s_handedBy = new uint[Depth];
        private static int s_handedCount;
        private static uint s_nextJob = FirstJobGroup;

        /// <summary>The owning group; 0 for none.</summary>
        public static uint Foreground { get; private set; }

        /// <summary>The kernel's own threads (process null) always may.</summary>
        public static bool MayRead(AppProcess process)
            => process == null || Foreground == 0 || process.Group == Foreground;

        /// <summary>The group a new process starts in, its id already given.</summary>
        public static uint GroupForNew(AppProcess process, AppProcess starter)
        {
            if (starter == null) return process.Id;
            return starter.JobGroup != 0 ? starter.JobGroup : starter.Group;
        }

        /// <summary>
        /// Opens a job: what <paramref name="self"/> starts from now on is in a
        /// new group, and the terminal is that group's. False when the terminal
        /// is not the caller's to give; the job is then open, without it.
        /// </summary>
        public static bool BeginJob(AppProcess self)
        {
            Preemption.Suppress();
            try
            {
                self.JobGroup = s_nextJob++;
                if (s_nextJob == 0) s_nextJob = FirstJobGroup;
                return HandToJob(self);
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>
        /// Gives the terminal to the caller's job, again if need be: a stage
        /// that ended before the next one started left its group empty for a
        /// moment, and the terminal came back.
        /// </summary>
        public static bool HandToJob(AppProcess self)
        {
            Preemption.Suppress();
            try
            {
                if (self.JobGroup == 0) return false;
                if (Foreground == self.JobGroup) return true;
                if (Foreground != 0 && Foreground != self.Group) return false;
                if (s_handedCount == Depth)
                {
                    for (int i = 1; i < Depth; i++) s_handedBy[i - 1] = s_handedBy[i];
                    s_handedCount--;
                }
                s_handedBy[s_handedCount++] = self.Group;
                Foreground = self.JobGroup;
                return true;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>
        /// Closes the job: the caller's children are in its own group again,
        /// and the terminal comes back if it was handed on from the caller's
        /// group, whoever holds it now.
        /// </summary>
        public static bool EndJob(AppProcess self)
        {
            Preemption.Suppress();
            try
            {
                self.JobGroup = 0;
                if (Foreground == 0 || Foreground == self.Group) return true;
                for (int i = s_handedCount - 1; i >= 0; i--)
                {
                    if (s_handedBy[i] != self.Group) continue;
                    s_handedCount = i;
                    Foreground = self.Group;
                    return true;
                }
                return false;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>A process has ended and its slot is free: the terminal goes on if its owner is gone.</summary>
        public static void OnProcessEnded()
        {
            Preemption.Suppress();
            try
            {
                while (Foreground != 0 && !AppProcesses.GroupHasMember(Foreground))
                    Foreground = s_handedCount > 0 ? s_handedBy[--s_handedCount] : 0;
            }
            finally { Preemption.Allow(); }
        }
    }
}
